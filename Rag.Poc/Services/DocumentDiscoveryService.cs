namespace Rag.Poc.Services;

public class DocumentDiscoveryService
{
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