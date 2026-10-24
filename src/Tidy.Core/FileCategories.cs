namespace Tidy.Core;

public static class FileCategories
{
    private static readonly IReadOnlyDictionary<string, string> Extensions = Build();

    private static IReadOnlyDictionary<string, string> Build()
    {
        var categories = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Images"] = [".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".svg", ".heic"],
            ["Videos"] = [".mp4", ".mkv", ".mov", ".avi", ".webm", ".wmv"],
            ["Audio"] = [".mp3", ".wav", ".flac", ".ogg", ".m4a", ".aac"],
            ["Documents"] = [".pdf", ".doc", ".docx", ".txt", ".rtf", ".odt"],
            ["Spreadsheets"] = [".xls", ".xlsx", ".csv", ".ods"],
            ["Presentations"] = [".ppt", ".pptx", ".odp"],
            ["Archives"] = [".zip", ".rar", ".7z", ".tar", ".gz"],
            ["Installers"] = [".exe", ".msi", ".msix", ".msixbundle"],
            ["Code"] = [".py", ".js", ".ts", ".html", ".css", ".json", ".xml", ".cs", ".lua"]
        };
        return categories.SelectMany(pair => pair.Value.Select(extension => (extension, pair.Key)))
            .ToDictionary(item => item.extension, item => item.Key, StringComparer.OrdinalIgnoreCase);
    }

    public static string GetCategory(string path) => Extensions.TryGetValue(Path.GetExtension(path), out var category) ? category : "Other";
    public static IReadOnlyList<string> Names { get; } = ["Images", "Videos", "Audio", "Documents", "Spreadsheets", "Presentations", "Archives", "Installers", "Code", "Other"];
}
