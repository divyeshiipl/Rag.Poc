namespace Rag.Poc.Services;

public class DocumentDiscoveryService
{
    /// <summary>
    /// Discover the documents from the project's folder
    /// </summary>
    /// <param name="projectFolder"></param>
    /// <returns></returns>
    public IEnumerable<string> Discover(
        string projectFolder)
    {
        if (!Directory.Exists(projectFolder))
            return [];

        return Directory
            .EnumerateFiles(
                projectFolder,
                "*.*",
                SearchOption.AllDirectories);
    }
}