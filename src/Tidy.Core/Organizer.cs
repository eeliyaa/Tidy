using System.Security.Cryptography;

namespace Tidy.Core;

public sealed class Organizer
{
    private readonly object sync = new();
    private OperationJournal? sessionJournal;
    private int activeOperation;

    public bool IsOperationRunning => Volatile.Read(ref activeOperation) != 0;
    public bool CanUndo
    {
        get { lock (sync) return !IsOperationRunning && sessionJournal?.Entries.Any(entry => entry.Completed) == true; }
    }

    public async Task<PreviewResult> PreviewAsync(string folder, CancellationToken cancellationToken = default)
    {
        folder = FolderSafety.ValidateFolder(folder);
        string? appExe = Environment.ProcessPath is null ? null : Path.GetFullPath(Environment.ProcessPath);
        string appDirectory = Path.GetFullPath(AppContext.BaseDirectory);
        return await Task.Run(() =>
        {
            var plans = new List<FilePlan>();
            var warnings = new List<string>();
            foreach (string path in Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (FolderSafety.IsReparsePoint(path)) { warnings.Add($"Skipped linked item: {Path.GetFileName(path)}"); continue; }
                    if (FolderSafety.IsShortcut(path)) { warnings.Add($"Skipped shortcut: {Path.GetFileName(path)}"); continue; }
                    if (FolderSafety.IsWithin(path, FolderSafety.Normalize(appDirectory))) { warnings.Add($"Skipped Tidy application file: {Path.GetFileName(path)}"); continue; }
                    if (appExe is not null && string.Equals(Path.GetFullPath(path), appExe, StringComparison.OrdinalIgnoreCase))
                    { warnings.Add("Skipped Tidy's own executable."); continue; }
                    var info = new FileInfo(path);
                    string category = FileCategories.GetCategory(path);
                    plans.Add(new(path, Path.Combine(folder, category, info.Name), category, info.Length, info.LastWriteTimeUtc));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
                { warnings.Add($"Could not inspect {Path.GetFileName(path)}: {exception.Message}"); }
            }
            return new PreviewResult(plans, warnings);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OperationResult> OrganizeAsync(string folder, IReadOnlyList<FilePlan> preview, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        EnterOperation();
        try
        {
            folder = FolderSafety.ValidateFolder(folder);
            var refreshed = await PreviewAsync(folder, cancellationToken).ConfigureAwait(false);
            var current = refreshed.Plans.ToDictionary(plan => plan.Source, StringComparer.OrdinalIgnoreCase);
            if (current.Count != preview.Count || preview.Any(plan => !current.TryGetValue(plan.Source, out var actual) || actual.Length != plan.Length || actual.LastWriteUtc != plan.LastWriteUtc))
                throw new InvalidOperationException("Folder contents changed since preview. Refresh the preview and review it before organizing.");
            CheckWritable(folder);

            var journal = new OperationJournal();
            lock (sync) sessionJournal = journal;
            var errors = new List<string>();
            int moved = 0;
            foreach (FilePlan plan in preview)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    string source = Path.GetFullPath(plan.Source);
                    string destinationDirectory = Path.GetFullPath(Path.GetDirectoryName(plan.Destination)!);
                    if (!FolderSafety.IsWithin(source, FolderSafety.Normalize(folder)) || !FolderSafety.IsWithin(destinationDirectory, FolderSafety.Normalize(folder)) ||
                        !string.Equals(Path.GetDirectoryName(destinationDirectory), folder, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("The proposed move is outside the selected folder's category folders.");
                    if (!File.Exists(source) || FolderSafety.IsReparsePoint(source) || FolderSafety.IsShortcut(source)) throw new IOException("The source is missing, linked, or not eligible to move.");
                    Directory.CreateDirectory(destinationDirectory);
                    if ((File.GetAttributes(destinationDirectory) & FileAttributes.ReparsePoint) != 0) throw new IOException("The category folder is a linked location.");

                    string digest = await HashAsync(source, cancellationToken).ConfigureAwait(false);
                    string destination = plan.Destination;
                    int collision = 0;
                    while (true)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!File.Exists(source) || FolderSafety.IsReparsePoint(source)) throw new IOException("The source changed while preparing the move.");
                        if ((File.GetAttributes(destinationDirectory) & FileAttributes.ReparsePoint) != 0) throw new IOException("The category folder changed to a linked location.");
                        try
                        {
                            // File.Move without overwrite is the final atomic collision guard.
                            File.Move(source, destination);
                            journal.Entries.Add(new(source, destination, digest, true));
                            moved++;
                            break;
                        }
                        catch (IOException) when (File.Exists(destination) || Directory.Exists(destination))
                        { destination = UniquePath(plan.Destination, ++collision); }
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                { errors.Add($"{plan.FileName}: {exception.Message}"); }
                progress?.Report(moved + errors.Count);
            }
            return new OperationResult(moved, errors, journal);
        }
        finally { ExitOperation(); }
    }

    public async Task<UndoResult> UndoAsync(CancellationToken cancellationToken = default)
    {
        EnterOperation();
        try
        {
            OperationJournal? journal;
            lock (sync) journal = sessionJournal;
            if (journal is null) return new(0, ["Undo history is empty for this session."]);
            int restored = 0;
            var errors = new List<string>();
            for (int index = journal.Entries.Count - 1; index >= 0; index--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                JournalEntry entry = journal.Entries[index];
                if (!entry.Completed) continue;
                try
                {
                    string originalFolder = FolderSafety.ValidateFolder(Path.GetDirectoryName(entry.Source)!);
                    string destinationFolder = Path.GetDirectoryName(entry.Destination)!;
                    if (!string.Equals(Path.GetDirectoryName(destinationFolder), originalFolder, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Undo destination is not in a category folder inside the original folder.");
                    if (!Directory.Exists(destinationFolder) || FolderSafety.IsReparsePoint(destinationFolder)) throw new IOException("The category folder is missing or linked.");
                    if (!File.Exists(entry.Destination) || FolderSafety.IsReparsePoint(entry.Destination)) throw new IOException("The organized file is missing or linked.");
                    if (!string.Equals(await HashAsync(entry.Destination, cancellationToken).ConfigureAwait(false), entry.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("The organized file contents changed; it was left in place.");
                    if (File.Exists(entry.Source) || Directory.Exists(entry.Source)) throw new IOException("The original path is occupied; resolve the conflict and retry undo.");
                    File.Move(entry.Destination, entry.Source);
                    journal.Entries[index] = entry with { Completed = false };
                    restored++;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
                { errors.Add($"{Path.GetFileName(entry.Destination)}: {exception.Message}"); }
            }
            return new UndoResult(restored, errors);
        }
        finally { ExitOperation(); }
    }

    public void ClearSession() { lock (sync) sessionJournal = null; }

    private void EnterOperation()
    {
        if (Interlocked.CompareExchange(ref activeOperation, 1, 0) != 0) throw new InvalidOperationException("A file operation is already running.");
    }
    private void ExitOperation() => Volatile.Write(ref activeOperation, 0);

    private static void CheckWritable(string folder)
    {
        string probe = Path.Combine(folder, ".tidy-write-check-" + Guid.NewGuid().ToString("N"));
        try { using var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { throw new InvalidOperationException("The selected folder is not writable.", exception); }
    }

    private static string UniquePath(string path, int number) => Path.Combine(Path.GetDirectoryName(path)!, $"{Path.GetFileNameWithoutExtension(path)} ({number}){Path.GetExtension(path)}");

    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }
}
