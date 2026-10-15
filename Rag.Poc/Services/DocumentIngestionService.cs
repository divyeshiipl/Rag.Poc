using Microsoft.EntityFrameworkCore;
using Rag.Poc.Data;
using Rag.Poc.Models;

namespace Rag.Poc.Services;

public class DocumentIngestionService
{
    private readonly RagDbContext _db;
    private readonly DocumentSyncService _syncService;
    private readonly DocumentExtractor _textExtractor;
    private readonly DocumentChunker _chunker;
    private readonly IEmbeddingService _embeddingService;
    private readonly ILogger<DocumentIngestionService> _logger;

    public DocumentIngestionService(
        RagDbContext db,
        DocumentSyncService syncService,
        DocumentExtractor textExtractor,
        DocumentChunker chunker,
        IEmbeddingService embeddingService,
        ILogger<DocumentIngestionService> logger)
    {
        _db = db;
        _syncService = syncService;
        _textExtractor = textExtractor;
        _chunker = chunker;
        _logger = logger;
        _embeddingService = embeddingService;
    }

    public async Task<int> RechunkProjectAsync(
    Guid projectId,
    CancellationToken cancellationToken = default)
    {
        var documents = await _db.Documents
            .Where(x => x.ProjectId == projectId)
            .ToListAsync(cancellationToken);

        if (documents.Count == 0)
        {
            return 0;
        }

        var processedDocuments = 0;

        foreach (var document in documents)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                _logger.LogInformation(
                    "Re-chunking document {FileName}",
                    document.FileName);

                // Delete existing chunks.
                await _db.DocumentChunks
                    .Where(x => x.DocumentId == document.Id)
                    .ExecuteDeleteAsync(cancellationToken);

                // Extract the document again.
                var extractedDocument = await _textExtractor.ExtractAsync(
                    document.FilePath,
                    cancellationToken);

                if (string.IsNullOrWhiteSpace(
                        extractedDocument.Text))
                {
                    document.Status = "Completed";
                    document.ProcessedAtUtc =
                        DateTime.UtcNow;

                    processedDocuments++;
                    continue;
                }

                var chunks =
                    _chunker.Chunk(
                        document.Id,
                        extractedDocument);

                foreach (var chunk in chunks)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var embedding =
                        await _embeddingService
                            .GenerateDocumentEmbeddingAsync(
                                chunk.Content,
                                cancellationToken);

                    chunk.Embedding =
                        new Pgvector.Vector(embedding);
                }

                await _db.DocumentChunks.AddRangeAsync(
                    chunks,
                    cancellationToken);

                document.Status = "Completed";
                document.ProcessedAtUtc = DateTime.UtcNow;

                processedDocuments++;
            }
            catch (Exception ex)
            {
                document.Status = "Failed";

                _logger.LogError(
                    ex,
                    "Failed to re-chunk document {DocumentId} - {FileName}",
                    document.Id,
                    document.FileName);
            }
        }

        await _db.SaveChangesAsync(cancellationToken);

        return processedDocuments;
    }

    public async Task<IngestionResult> ProcessProjectAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        var syncResult =
            await _syncService.SyncProjectAsync(
                projectId,
                cancellationToken);

        var result = new IngestionResult
        {
            NewDocuments = syncResult.NewDocuments.Count,
            ChangedDocuments = syncResult.ChangedDocuments.Count,
            UnchangedDocuments = syncResult.UnchangedDocuments.Count,
            DeletedDocuments = syncResult.DeletedDocuments.Count
        };

        var documentsToProcess =
            syncResult.NewDocuments
                .Concat(syncResult.ChangedDocuments)
                .ToList();

        foreach (var document in documentsToProcess)
        {
            try
            {
                await ProcessDocumentAsync(
                    document,
                    cancellationToken);

                result.ProcessedDocuments++;
            }
            catch (Exception ex)
            {
                result.FailedDocuments++;

                _logger.LogError(
                    ex,
                    "Failed to process document {DocumentId} - {FileName}",
                    document.Id,
                    document.FileName);

                document.Status = "Failed";
            }
        }

        await _db.SaveChangesAsync(cancellationToken);

        return result;
    }

    private async Task ProcessDocumentAsync(
        Document document,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Processing document {FileName}",
            document.FileName);

        var extractedDocument =
            await _textExtractor.ExtractAsync(
                document.FilePath,
                cancellationToken);

        if (string.IsNullOrWhiteSpace(
            extractedDocument.Text))
        {
            document.Status = "Completed";
            document.ProcessedAtUtc = DateTime.UtcNow;

            return;
        }

        var isExcel = Path.GetExtension(document.FilePath)
            .Equals(".xlsx", StringComparison.OrdinalIgnoreCase)
            || Path.GetExtension(document.FilePath)
            .Equals(".xls", StringComparison.OrdinalIgnoreCase);

        var chunks = isExcel
            ? CreateExcelRowChunks(document.Id, extractedDocument)
            : _chunker.Chunk(
                document.Id,
                extractedDocument,
                chunkSize: 1000,
                overlap: 200);

        foreach (var chunk in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var embedding =
                await _embeddingService
                    .GenerateDocumentEmbeddingAsync(
                        chunk.Content,
                        cancellationToken);

            chunk.Embedding =
                new Pgvector.Vector(embedding);
        }

        await _db.DocumentChunks.AddRangeAsync(
            chunks,
            cancellationToken);

        document.Status = "Completed";
        document.ProcessedAtUtc =
            DateTime.UtcNow;

        _logger.LogInformation(
            "Processed {FileName}: {ChunkCount} chunks",
            document.FileName,
            chunks.Count);
    }

    private static List<DocumentChunk> CreateExcelRowChunks(
    Guid documentId,
    ExtractedDocument extractedDocument)
    {
        var chunks = new List<DocumentChunk>();

        foreach (var section in extractedDocument.Sections)
        {
            if (string.IsNullOrWhiteSpace(section.Content))
                continue;

            var metadata = new Dictionary<string, string>(
                section.Metadata)
            {
                ["RecordType"] = "ExcelRow"
            };

            chunks.Add(new DocumentChunk
            {
                Id = Guid.NewGuid(),
                DocumentId = documentId,
                ChunkIndex = chunks.Count,
                Content = section.Content,
                TokenCount = Math.Max(
                    1,
                    section.Content.Length / 4),
                SectionTitle = section.Heading,
                SourceLocation = section.SourceLocation,
                MetadataJson =
                    System.Text.Json.JsonSerializer.Serialize(metadata)
            });
        }

        return chunks;
    }
}