namespace Rag.Poc.Services;

public class DocumentSyncService
{
    private readonly RagDbContext _db;
    private readonly DocumentDiscoveryService _discoveryService;
    private readonly ILogger<DocumentSyncService> _logger;

    public DocumentSyncService(RagDbContext db, DocumentDiscoveryService discoveryService, ILogger<DocumentSyncService> logger)
    {
        _db = db;
        _discoveryService = discoveryService;
        _logger = logger;
    }

    #region Public Methods

    /// <summary>
    /// To Sync the Project (Add Docs/ Update Docs and DocChunks)
    /// </summary>
    /// <param name="projectId"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    /// <exception cref="DirectoryNotFoundException"></exception>
    public async Task<SyncResult> SyncProjectAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var result = new SyncResult();
        var project = await _db.Projects.FirstOrDefaultAsync(x => x.Id == projectId, cancellationToken);

        if (project == null)
        {
            throw new InvalidOperationException($"Project '{projectId}' was not found.");
        }

        if (string.IsNullOrWhiteSpace(project.DocumentFolder))
        {
            throw new InvalidOperationException($"Project '{project.Name}' does not have a document folder.");
        }

        var projectFolder = Path.GetFullPath(project.DocumentFolder);

        if (!Directory.Exists(projectFolder))
        {
            throw new DirectoryNotFoundException($"Project document folder was not found: {projectFolder}");
        }

        var existingDocuments = await _db.Documents
                .Where(x => x.ProjectId == projectId)
                .ToListAsync(cancellationToken);

        var discoveredFiles = _discoveryService
                .Discover(projectFolder)
                .Select(Path.GetFullPath)
                .ToList();

        var physicalFilePaths = new HashSet<string>(discoveredFiles, StringComparer.OrdinalIgnoreCase);
        var existingDocumentsByPath = existingDocuments.ToDictionary(x => Path.GetFullPath(x.FilePath), StringComparer.OrdinalIgnoreCase);

        foreach (var filePath in discoveredFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (existingDocumentsByPath.TryGetValue(filePath, out var existingDocument))
            {
                await ProcessExistingDocumentAsync(existingDocument, projectFolder, filePath, result, cancellationToken);
            }
            else
            {
                var newDocument = await CreateNewDocumentAsync(project, projectFolder, filePath, cancellationToken);
                _db.Documents.Add(newDocument);
                result.NewDocuments.Add(newDocument);
                _logger.LogInformation("New document discovered: {FilePath}", filePath);
            }
        }

        foreach (var existingDocument in existingDocuments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var existingPath = Path.GetFullPath(existingDocument.FilePath);

            if (!physicalFilePaths.Contains(existingPath))
            {
                _db.Documents.Remove(existingDocument);
                result.DeletedDocuments.Add(existingDocument);
                _logger.LogInformation("Document deleted from filesystem: {FilePath}", existingDocument.FilePath);
            }
        }

        await _db.SaveChangesAsync(cancellationToken);
        return result;
    }

    #endregion

    #region Private Methods

    /// <summary>
    /// To process existing documents
    /// </summary>
    /// <param name="document"></param>
    /// <param name="projectFolder"></param>
    /// <param name="filePath"></param>
    /// <param name="result"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    private async Task ProcessExistingDocumentAsync(Models.Document document, string projectFolder, string filePath, SyncResult result, CancellationToken cancellationToken)
    {
        var currentHash = await CalculateHashAsync(filePath, cancellationToken);

        if (string.Equals(document.FileHash, currentHash, StringComparison.OrdinalIgnoreCase))
        {
            result.UnchangedDocuments.Add(document);
            _logger.LogInformation("Document unchanged: {FilePath}", filePath);
            return;
        }

        await UpdateChangedDocumentAsync(document, projectFolder, filePath, currentHash, cancellationToken);
        result.ChangedDocuments.Add(document);
        _logger.LogInformation("Document changed: {FilePath}", filePath);
    }

    /// <summary>
    /// To create new document
    /// </summary>
    /// <param name="project"></param>
    /// <param name="projectFolder"></param>
    /// <param name="filePath"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    private async Task<Models.Document> CreateNewDocumentAsync(Project project, string projectFolder, string filePath, CancellationToken cancellationToken)
    {
        var fileInfo = new FileInfo(filePath);
        var fileHash =  await CalculateHashAsync(filePath, cancellationToken);
        var relativePath = GetRelativePath(projectFolder, filePath);

        return new Models.Document
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            FileName = fileInfo.Name,
            RelativePath = relativePath,
            ContentType = GetContentType(fileInfo.Extension),
            FileSize = fileInfo.Length,
            FilePath = fileInfo.FullName,
            FileHash = fileHash,
            LastModifiedUtc = fileInfo.LastWriteTimeUtc,
            Status = "Pending",
            CreatedAtUtc = DateTime.UtcNow,
            ProcessedAtUtc = null
        };
    }
   
    /// <summary>
    /// To update the changed document
    /// </summary>
    /// <param name="document"></param>
    /// <param name="projectFolder"></param>
    /// <param name="filePath"></param>
    /// <param name="newHash"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    private async Task UpdateChangedDocumentAsync(Models.Document document, string projectFolder, string filePath, string newHash, CancellationToken cancellationToken)
    {
        var fileInfo = new FileInfo(filePath);
        await _db.DocumentChunks
            .Where(x => x.DocumentId == document.Id)
            .ExecuteDeleteAsync(cancellationToken);

        var relativePath = GetRelativePath(projectFolder, filePath);

        document.FileName = fileInfo.Name;
        document.RelativePath = relativePath;
        document.ContentType = GetContentType(fileInfo.Extension);
        document.FileSize = fileInfo.Length;
        document.FilePath = fileInfo.FullName;
        document.FileHash = newHash;
        document.LastModifiedUtc = fileInfo.LastWriteTimeUtc;
        document.Status = "Pending";
        document.ProcessedAtUtc = null;
    }

    /// <summary>
    /// To calculate hash for files
    /// </summary>
    /// <param name="filePath"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    private static async Task<string> CalculateHashAsync(string filePath, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 1024 * 64,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan);

        using var sha256 = SHA256.Create();
        var hash = await sha256.ComputeHashAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }

    /// <summary>
    /// To get relative path from project folder
    /// </summary>
    /// <param name="projectFolder"></param>
    /// <param name="filePath"></param>
    /// <returns></returns>
    private static string GetRelativePath(string projectFolder, string filePath)
    {
        return Path.GetRelativePath(projectFolder, filePath).Replace(Path.DirectorySeparatorChar, '/');
    }

    /// <summary>
    /// To get content type of the extention
    /// </summary>
    /// <param name="extension"></param>
    /// <returns></returns>
    private static string GetContentType(string extension)
    {
        return extension.ToLowerInvariant() switch
        {
            ".pdf" => "application/pdf",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            ".txt" => "text/plain",
            ".csv" => "text/csv",
            ".html" or ".htm" => "text/html",
            ".json" => "application/json",
            ".xml" => "application/xml",
            _ => "application/octet-stream"
        };
    }

    #endregion
}