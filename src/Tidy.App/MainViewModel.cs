using System.IO;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Tidy.Core;

namespace Tidy.App;

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly Organizer organizer = new();
    private CancellationTokenSource? scanCancellation;
    private readonly object scansLock = new();
    private readonly List<Task> activeScans = [];
    private Task scanTask = Task.CompletedTask;
    private long scanVersion;
    private bool disposed;
    private string folder = string.Empty;
    private string status = "Choose a folder";
    private string details = "Select a folder to see how Tidy will organize your files.";
    private string? error;
    private int skipped;
    private bool scanning;
    private bool operating;

    public ObservableCollection<FilePlan> Plans { get; } = [];
    public ObservableCollection<CategoryCount> Categories { get; } = [];
    public ObservableCollection<string> Warnings { get; } = [];
    public string Folder { get => folder; private set => Set(ref folder, value); }
    public string FolderLabel => string.IsNullOrEmpty(Folder) ? "No folder selected" : Folder;
    public string Status { get => status; private set => Set(ref status, value); }
    public string Details { get => details; private set => Set(ref details, value); }
    public string? Error { get => error; private set => Set(ref error, value); }
    public int Skipped { get => skipped; private set { if (Set(ref skipped, value)) OnChanged(nameof(CountSummary)); } }
    public bool IsScanning { get => scanning; private set { if (Set(ref scanning, value)) RaiseState(); } }
    public bool IsOperating { get => operating; private set { if (Set(ref operating, value)) RaiseState(); } }
    public bool IsBusy => IsScanning || IsOperating;
    public bool HasFolder => !string.IsNullOrEmpty(Folder);
    public bool ShowEmpty => !IsScanning && Plans.Count == 0;
    public bool CanOrganize => !IsBusy && Plans.Count > 0;
    public bool CanUndo => !IsBusy && organizer.CanUndo;
    public bool CanRefresh => HasFolder && !IsBusy;
    public bool CanBrowse => !IsOperating;
    public bool HasWarnings => Warnings.Count > 0;
    public bool HasCategories => Categories.Count > 0;
    public string CategoryEmptyMessage => HasFolder ? "No categories to show." : "Category counts appear after you choose a folder.";
    public int ReadyCount => Plans.Count;
    public string CountSummary => $"{ReadyCount:N0} ready  ·  {Skipped:N0} skipped";
    public string WarningSummary => Warnings.Count == 0 ? string.Empty : string.Join("  ·  ", Warnings.Take(2)) + (Warnings.Count > 2 ? "  ·  See details" : string.Empty);
    public string WarningTooltip => string.Join(Environment.NewLine, Warnings);
    public bool IsFileOperationRunning => organizer.IsOperationRunning;
    public Task CurrentPreviewTask => scanTask;

    public event PropertyChangedEventHandler? PropertyChanged;

    public void SelectFolder(string path)
    {
        if (disposed || IsOperating) return;
        if (string.Equals(Path.GetFullPath(path), Folder, StringComparison.OrdinalIgnoreCase)) { _ = RefreshAsync(); return; }
        Folder = Path.GetFullPath(path);
        OnChanged(nameof(FolderLabel)); OnChanged(nameof(HasFolder)); OnChanged(nameof(CanRefresh));
        _ = RefreshAsync();
    }

    public Task RefreshAsync()
    {
        Task task = RefreshCoreAsync();
        scanTask = task;
        lock (scansLock) activeScans.Add(task);
        _ = task.ContinueWith(completed => { lock (scansLock) activeScans.Remove(completed); }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }

    private async Task RefreshCoreAsync()
    {
        if (disposed || IsOperating || !HasFolder) return;
        scanCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        scanCancellation = cancellation;
        CancellationToken token = cancellation.Token;
        long version = ++scanVersion;
        Plans.Clear(); Categories.Clear(); Warnings.Clear(); Skipped = 0; Error = null;
        OnChanged(nameof(ShowEmpty)); OnChanged(nameof(ReadyCount)); OnChanged(nameof(CountSummary)); OnChanged(nameof(HasWarnings)); OnChanged(nameof(HasCategories)); OnChanged(nameof(CategoryEmptyMessage));
        IsScanning = true; Status = "Scanning folder…"; Details = "Reading top-level files. This does not change them.";
        try
        {
            PreviewResult result = await organizer.PreviewAsync(Folder, token);
            if (disposed || version != scanVersion || token.IsCancellationRequested) return;
            foreach (FilePlan plan in result.Plans) Plans.Add(plan);
            foreach (string warning in result.Warnings) Warnings.Add(warning);
            Skipped = result.Warnings.Count;
            OnChanged(nameof(ReadyCount)); OnChanged(nameof(CountSummary));
            foreach (string name in FileCategories.Names)
            {
                int count = result.Plans.Count(plan => plan.Category == name);
                if (count > 0) Categories.Add(new CategoryCount(name, count));
            }
            OnChanged(nameof(HasCategories)); OnChanged(nameof(CategoryEmptyMessage));
            Status = result.Plans.Count == 0 ? "No files ready to organize" : $"{result.Plans.Count:N0} files ready";
            Details = Warnings.Count == 0 ? "Preview is read-only. Organize Files moves items after confirmation." : "Some items could not be previewed. Review the warnings.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (version != scanVersion) return;
            Error = exception.Message; Status = "This folder could not be scanned"; Details = "Choose another folder or refresh to try again.";
        }
        finally
        {
            cancellation.Dispose();
            if (ReferenceEquals(scanCancellation, cancellation)) scanCancellation = null;
            if (version == scanVersion)
            {
                IsScanning = false;
                OnChanged(nameof(ShowEmpty)); OnChanged(nameof(CanRefresh)); OnChanged(nameof(CanOrganize));
                OnChanged(nameof(HasWarnings)); OnChanged(nameof(WarningSummary)); OnChanged(nameof(WarningTooltip));
            }
        }
    }

    public async Task OrganizeAsync()
    {
        if (!CanOrganize) return;
        IsOperating = true; Error = null; Status = "Organizing files…"; Details = "Please keep Tidy open until the moves finish.";
        try
        {
            var progress = new Progress<int>(done => Details = $"Processed {done:N0} of {Plans.Count:N0} files.");
            OperationResult result = await organizer.OrganizeAsync(Folder, Plans.ToArray(), progress);
            Status = result.Errors.Count == 0 ? $"Organized {result.Moved:N0} files" : $"Moved {result.Moved:N0} files · {result.Errors.Count:N0} need attention";
            Details = result.Errors.Count == 0 ? "Undo remains available until Tidy closes." : string.Join("  ·  ", result.Errors.Take(2));
            Warnings.Clear(); foreach (string item in result.Errors) Warnings.Add(item);
            Skipped = Warnings.Count;
            OnChanged(nameof(HasWarnings)); OnChanged(nameof(WarningSummary)); OnChanged(nameof(WarningTooltip));
            await RefreshAsyncAfterOperation();
        }
        catch (Exception exception) { Error = exception.Message; Status = "Organization did not complete"; Details = "Review the folder and try again."; }
        finally { IsOperating = false; RaiseState(); }
    }

    private async Task RefreshAsyncAfterOperation()
    {
        if (disposed || !HasFolder) return;
        var result = await organizer.PreviewAsync(Folder);
        Plans.Clear(); foreach (FilePlan plan in result.Plans) Plans.Add(plan);
        Categories.Clear();
        foreach (string name in FileCategories.Names)
        {
            int count = result.Plans.Count(plan => plan.Category == name);
            if (count > 0) Categories.Add(new CategoryCount(name, count));
        }
        OnChanged(nameof(HasCategories)); OnChanged(nameof(CategoryEmptyMessage));
        OnChanged(nameof(ReadyCount)); OnChanged(nameof(CountSummary)); OnChanged(nameof(ShowEmpty));
    }

    public async Task UndoAsync()
    {
        if (!CanUndo) return;
        IsOperating = true; Error = null; Status = "Undoing organization…"; Details = "Restoring unchanged files to their original locations.";
        try
        {
            UndoResult result = await organizer.UndoAsync();
            Status = result.Errors.Count == 0 ? $"Restored {result.Restored:N0} files" : $"Restored {result.Restored:N0} · {result.Errors.Count:N0} could not be restored";
            Details = result.Errors.Count == 0 ? "Undo complete." : string.Join("  ·  ", result.Errors.Take(2));
            await RefreshAsyncAfterOperation();
        }
        catch (Exception exception) { Error = exception.Message; Status = "Undo did not complete"; Details = "Review the file conflicts and try again."; }
        finally { IsOperating = false; RaiseState(); }
    }

    public void BlockClose() { Status = "Finishing file operations. Please keep Tidy open."; Details = "Tidy will close when all moves or restores finish."; }

    public async Task StopScanAsync()
    {
        scanVersion++;
        scanCancellation?.Cancel();
        Task[] pending;
        lock (scansLock) pending = activeScans.ToArray();
        try { await Task.WhenAll(pending); } catch (OperationCanceledException) { }
        scanCancellation = null;
    }

    public void Dispose()
    {
        disposed = true; scanVersion++; scanCancellation?.Cancel(); organizer.ClearSession();
    }

    private void RaiseState()
    {
        OnChanged(nameof(IsBusy)); OnChanged(nameof(CanOrganize)); OnChanged(nameof(CanUndo)); OnChanged(nameof(CanRefresh));
        OnChanged(nameof(CanBrowse)); OnChanged(nameof(ShowEmpty));
    }
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; OnChanged(name); return true;
    }
    private void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
