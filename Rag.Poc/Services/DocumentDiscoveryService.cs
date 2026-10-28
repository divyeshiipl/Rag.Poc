namespace Rag.Poc.Services;

/// <summary>
/// Finds document files in a project folder, including all subfolders.
/// </summary>
public class DocumentDiscoveryService
{
    /// <summary>
    /// Gets all supported files under a project folder, searching subfolders too.
    /// </summary>
    public IEnumerable<string> Discover(string projectFolder)
    {
        if (!Directory.Exists(projectFolder))
            return [];

        return Directory
            .EnumerateFiles(projectFolder, "*.*", SearchOption.AllDirectories)
            .Where(f => SupportedExtensions.Contains(Path.GetExtension(f)));
    }

    /// <summary>
    /// File extensions the extractor pipeline supports; other files are skipped.
    /// </summary>
    private static readonly HashSet<string> SupportedExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
        ".txt", ".md", ".docx", ".xlsx", ".xls", ".csv", ".sql"
        };
}