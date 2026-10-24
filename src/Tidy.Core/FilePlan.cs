namespace Tidy.Core;

public sealed record FilePlan(string Source, string Destination, string Category, long Length, DateTime LastWriteUtc)
{
    public string FileName => Path.GetFileName(Source);
    public string RelativeDestination => Path.Combine(Category, Path.GetFileName(Destination));
}

public sealed record CategoryCount(string Name, int Count);
public sealed record JournalEntry(string Source, string Destination, string Sha256, bool Completed);
public sealed class OperationJournal
{
    public DateTime TimestampUtc { get; } = DateTime.UtcNow;
    public List<JournalEntry> Entries { get; } = [];
}
public sealed record PreviewResult(IReadOnlyList<FilePlan> Plans, IReadOnlyList<string> Warnings);
public sealed record OperationResult(int Moved, IReadOnlyList<string> Errors, OperationJournal Journal);
public sealed record UndoResult(int Restored, IReadOnlyList<string> Errors);
