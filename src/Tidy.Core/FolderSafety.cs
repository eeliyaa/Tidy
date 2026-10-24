namespace Tidy.Core;

public static class FolderSafety
{
    private static readonly string[] ShortcutExtensions = [".lnk", ".url"];

    public static string ValidateFolder(string path, string? applicationDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Choose a folder first.", nameof(path));
        var directory = new DirectoryInfo(Path.GetFullPath(path));
        if (!directory.Exists) throw new DirectoryNotFoundException("The selected folder no longer exists.");
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("A linked or reparse-point folder cannot be organized.");

        string full = Normalize(directory.FullName);
        string? root = Path.GetPathRoot(full);
        if (root is not null && string.Equals(full, Normalize(root), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A drive root cannot be organized.");

        var protectedPaths = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tidy"),
            applicationDirectory ?? AppContext.BaseDirectory
        };
        foreach (string candidate in protectedPaths.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            string critical = Normalize(candidate);
            if (IsWithin(full, critical)) throw new InvalidOperationException("Windows, application, and program installation locations are protected.");
        }
        return directory.FullName;
    }

    public static bool IsShortcut(string path) => ShortcutExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static bool IsReparsePoint(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    public static bool IsWithin(string path, string parent)
    {
        string normalizedPath = Normalize(path);
        string normalizedParent = Normalize(parent);
        return normalizedPath.Equals(normalizedParent, StringComparison.OrdinalIgnoreCase) ||
               normalizedPath.StartsWith(normalizedParent, StringComparison.OrdinalIgnoreCase);
    }

    public static string Normalize(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
}
