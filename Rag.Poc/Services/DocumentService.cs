namespace Rag.Poc.Services;

/// <summary>
/// Manages document records: finds or creates them, and detects content changes by file hash.
/// </summary>
public class DocumentService
{
    private readonly RagDbContext _db;

    /// <summary>
    /// Creates the service with the database context.
    /// </summary>
    public DocumentService(RagDbContext db)
    {
        _db = db;
    }

    #region Public Methods

    /// <summary>
    /// Gets the document for a project and file path, or creates it. If the file hash changed, updates the hash and resets status to "Pending".
    /// </summary>
    public async Task<Models.Document> GetOrCreateAsync(Guid projectId, string filePath)
    {
        var existing = await _db.Documents
            .FirstOrDefaultAsync(x =>
                x.ProjectId == projectId &&
                x.FilePath == filePath);

        var hash = await CalculateHashAsync(filePath);

        if (existing != null)
        {
            if (existing.FileHash == hash)
                return existing;

            existing.FileHash = hash;
            existing.Status = "Pending";

            await _db.SaveChangesAsync();

            return existing;
        }

        var document = new Models.Document
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            FileName = Path.GetFileName(filePath),
            ContentType = GetContentType(filePath),
            FileSize = new FileInfo(filePath).Length,
            FilePath = filePath,
            FileHash = hash,
            Status = "Pending",
            CreatedAtUtc = DateTime.UtcNow
        };

        _db.Documents.Add(document);

        await _db.SaveChangesAsync();

        return document;
    }

    #endregion

    #region Private Methods

    /// <summary>
    /// Computes the SHA-256 hash of a file as an uppercase hex string.
    /// </summary>
    private static async Task<string> CalculateHashAsync(string filePath)
    {
        await using var stream = File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hash);
    }

    /// <summary>
    /// Guesses the MIME content type from the file extension (falls back to application/octet-stream).
    /// </summary>
    private static string GetContentType(string filePath)
    {
        return Path.GetExtension(filePath)
            .ToLowerInvariant() switch
        {
            ".pdf" => "application/pdf",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".txt" => "text/plain",
            ".html" => "text/html",
            ".htm" => "text/html",
            ".csv" => "text/csv",
            _ => "application/octet-stream"
        };
    }

    #endregion
}
