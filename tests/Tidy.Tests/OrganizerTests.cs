using System.IO;
using Tidy.Core;

namespace Tidy.Tests;

public sealed class OrganizerTests : IDisposable
{
    private readonly string parent = Path.Combine(Path.GetTempPath(), "Tidy-Rebuild-Tests-" + Guid.NewGuid().ToString("N"));
    private readonly string folder;
    private readonly Organizer organizer = new();

    public OrganizerTests() { Directory.CreateDirectory(parent); folder = Path.Combine(parent, "work"); Directory.CreateDirectory(folder); }

    [Theory]
    [InlineData("x.PNG", "Images")]
    [InlineData("x.Mp4", "Videos")]
    [InlineData("x.Cs", "Code")]
    [InlineData("x.csv", "Spreadsheets")]
    [InlineData("x.unknown", "Other")]
    [InlineData("no-extension", "Other")]
    public void Categorizes_extensions_case_insensitively(string name, string category) => Assert.Equal(category, FileCategories.GetCategory(name));

    [Fact]
    public async Task Empty_folder_has_empty_preview()
    {
        PreviewResult result = await organizer.PreviewAsync(folder);
        Assert.Empty(result.Plans); Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task Preview_handles_two_thousand_files_and_unicode_names()
    {
        for (int index = 0; index < 2000; index++) File.WriteAllText(Path.Combine(folder, $"item-{index:D4}.txt"), "x");
        const string unicodeName = "تقرير résumé.txt"; File.WriteAllText(Path.Combine(folder, unicodeName), "x");
        PreviewResult preview = await organizer.PreviewAsync(folder);
        Assert.Equal(2001, preview.Plans.Count); Assert.Contains(preview.Plans, plan => plan.FileName == unicodeName);
    }

    [Fact]
    public async Task Preview_is_read_only_and_does_not_make_category_directories()
    {
        string source = Path.Combine(folder, "résumé.txt"); File.WriteAllText(source, "safe");
        PreviewResult preview = await organizer.PreviewAsync(folder);
        Assert.Single(preview.Plans); Assert.True(File.Exists(source)); Assert.False(Directory.Exists(Path.Combine(folder, "Documents")));
        Assert.Equal("résumé.txt", preview.Plans[0].FileName);
    }

    [Fact]
    public async Task Collision_gets_unique_name_and_keeps_existing_destination()
    {
        File.WriteAllText(Path.Combine(folder, "report.txt"), "new"); Directory.CreateDirectory(Path.Combine(folder, "Documents"));
        File.WriteAllText(Path.Combine(folder, "Documents", "report.txt"), "keep");
        PreviewResult preview = await organizer.PreviewAsync(folder);
        OperationResult operation = await organizer.OrganizeAsync(folder, preview.Plans);
        Assert.True(operation.Moved == 1, string.Join(Environment.NewLine, operation.Errors)); Assert.Equal("keep", File.ReadAllText(Path.Combine(folder, "Documents", "report.txt")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(folder, "Documents", "report (1).txt")));
    }

    [Fact]
    public async Task Changed_preview_is_rejected_without_moving_anything()
    {
        File.WriteAllText(Path.Combine(folder, "first.txt"), "first"); PreviewResult preview = await organizer.PreviewAsync(folder);
        File.WriteAllText(Path.Combine(folder, "new.txt"), "new");
        await Assert.ThrowsAsync<InvalidOperationException>(() => organizer.OrganizeAsync(folder, preview.Plans));
        Assert.True(File.Exists(Path.Combine(folder, "first.txt")));
    }

    [Fact]
    public async Task Undo_restores_unchanged_file_and_keeps_files_safe()
    {
        string source = Path.Combine(folder, "image.png"); File.WriteAllText(source, "image");
        PreviewResult preview = await organizer.PreviewAsync(folder); OperationResult result = await organizer.OrganizeAsync(folder, preview.Plans);
        UndoResult undo = await organizer.UndoAsync();
        Assert.True(result.Moved == 1, string.Join(Environment.NewLine, result.Errors)); Assert.Equal(1, undo.Restored); Assert.Empty(undo.Errors); Assert.Equal("image", File.ReadAllText(source));
    }

    [Fact]
    public async Task Undo_does_not_overwrite_a_modified_file()
    {
        string source = Path.Combine(folder, "same.txt"); File.WriteAllText(source, "original");
        var preview = await organizer.PreviewAsync(folder); await organizer.OrganizeAsync(folder, preview.Plans);
        string destination = Path.Combine(folder, "Documents", "same.txt"); File.WriteAllText(destination, "modified");
        UndoResult changed = await organizer.UndoAsync(); Assert.Equal(0, changed.Restored); Assert.Equal("modified", File.ReadAllText(destination));
    }

    [Fact]
    public async Task Undo_does_not_overwrite_an_occupied_original_path()
    {
        string source = Path.Combine(folder, "same.txt"); File.WriteAllText(source, "original");
        var preview = await organizer.PreviewAsync(folder); await organizer.OrganizeAsync(folder, preview.Plans);
        File.WriteAllText(source, "unrelated"); UndoResult result = await organizer.UndoAsync();
        Assert.Equal("unrelated", File.ReadAllText(source)); Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public async Task Restart_has_no_undo_and_leaves_organized_files_in_place()
    {
        File.WriteAllText(Path.Combine(folder, "session.txt"), "content");
        var preview = await organizer.PreviewAsync(folder); await organizer.OrganizeAsync(folder, preview.Plans);
        var newSession = new Organizer(); Assert.False(newSession.CanUndo);
        Assert.True(File.Exists(Path.Combine(folder, "Documents", "session.txt")), string.Join(Environment.NewLine, preview.Plans.Select(p => p.Destination)));
        Assert.Equal("content", File.ReadAllText(Path.Combine(folder, "Documents", "session.txt")));
    }

    [Fact]
    public async Task Disappearing_file_is_not_moved()
    {
        string file = Path.Combine(folder, "gone.txt"); File.WriteAllText(file, "x"); var preview = await organizer.PreviewAsync(folder); File.Delete(file);
        await Assert.ThrowsAsync<InvalidOperationException>(() => organizer.OrganizeAsync(folder, preview.Plans));
        Assert.False(File.Exists(file)); Assert.False(Directory.Exists(Path.Combine(folder, "Documents")));
    }

    [Fact]
    public void Drive_root_and_program_files_are_protected()
    {
        Assert.Throws<InvalidOperationException>(() => FolderSafety.ValidateFolder(Path.GetPathRoot(folder)!));
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles)) Assert.Throws<InvalidOperationException>(() => FolderSafety.ValidateFolder(programFiles));
    }

    [Fact]
    public async Task Skips_shortcuts_and_reparse_points()
    {
        string shortcut = Path.Combine(folder, "skip.lnk"); File.WriteAllText(shortcut, "not a real shortcut");
        var preview = await organizer.PreviewAsync(folder);
        Assert.Empty(preview.Plans); Assert.Contains(preview.Warnings, warning => warning.Contains("shortcut", StringComparison.OrdinalIgnoreCase));
        string target = Path.Combine(folder, "target.txt"); File.WriteAllText(target, "x"); string link = Path.Combine(folder, "link.txt");
        try { File.CreateSymbolicLink(link, target); }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException) { return; }
        preview = await organizer.PreviewAsync(folder);
        Assert.DoesNotContain(preview.Plans, item => item.Source == link);
    }

    [Fact]
    public async Task Failed_move_is_reported_and_successful_moves_can_still_be_undone()
    {
        string good = Path.Combine(folder, "a-good.txt"); string locked = Path.Combine(folder, "z-locked.txt"); File.WriteAllText(good, "ok"); File.WriteAllText(locked, "lock");
        PreviewResult preview = await organizer.PreviewAsync(folder);
        using (var hold = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            OperationResult operation = await organizer.OrganizeAsync(folder, preview.Plans);
            Assert.True(operation.Moved == 1, string.Join(Environment.NewLine, operation.Errors)); Assert.Single(operation.Errors);
        }
        UndoResult undo = await organizer.UndoAsync(); Assert.Equal(1, undo.Restored); Assert.True(File.Exists(good)); Assert.True(File.Exists(locked));
    }

    [Fact]
    public async Task Concurrent_file_operations_are_rejected_and_busy_state_clears()
    {
        string file = Path.Combine(folder, "slow.txt"); File.WriteAllText(file, "x"); var preview = await organizer.PreviewAsync(folder);
        var barrier = new BlockingProgress(); Task<OperationResult> operation = organizer.OrganizeAsync(folder, preview.Plans, barrier);
        try
        {
            Assert.True(barrier.Wait(TimeSpan.FromSeconds(5))); Assert.True(organizer.IsOperationRunning);
            await Assert.ThrowsAsync<InvalidOperationException>(() => organizer.UndoAsync());
        }
        finally { barrier.Release(); }
        await operation; Assert.False(organizer.IsOperationRunning);
    }

    private sealed class BlockingProgress : IProgress<int>
    {
        private readonly ManualResetEventSlim entered = new(false), release = new(false);
        public void Report(int value) { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(8))) throw new TimeoutException(); }
        public bool Wait(TimeSpan timeout) => entered.Wait(timeout);
        public void Release() => release.Set();
    }

    public void Dispose() { try { Directory.Delete(parent, true); } catch { } }
}
