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

    private static readonly Dictionary<string, (int Size, int Overlap, int MinChars)> ChunkConfig = new()
    {
        [Namespaces.Default] = (600, 100, 150),
        [Namespaces.Structural] = (400, 50, 100),
        [Namespaces.Large] = (900, 200, 200)
    };

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
        _embeddingService = embeddingService;
        _logger = logger;
    }

    public async Task<IngestionResult> ProcessProjectAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        var syncResult = await _syncService.SyncProjectAsync(projectId, cancellationToken);

        var result = new IngestionResult
        {
            NewDocuments = syncResult.NewDocuments.Count,
            ChangedDocuments = syncResult.ChangedDocuments.Count,
            UnchangedDocuments = syncResult.UnchangedDocuments.Count,
            DeletedDocuments = syncResult.DeletedDocuments.Count
        };

        var documentsToProcess = syncResult.NewDocuments
            .Concat(syncResult.ChangedDocuments)
            .ToList();

        foreach (var document in documentsToProcess)
        {
            try
            {
                await ProcessDocumentAsync(document, cancellationToken);
                result.ProcessedDocuments++;
            }
            catch (Exception ex)
            {
                result.FailedDocuments++;
                _logger.LogError(ex,
                    "Failed to process document {DocumentId} - {FileName}",
                    document.Id, document.FileName);
                document.Status = "Failed";
            }
        }

        await _db.SaveChangesAsync(cancellationToken);
        return result;
    }

    public async Task<int> RechunkProjectAsync(
        Guid projectId,
        int chunkSize = 600,
        int overlap = 100,
        int minChunkChars = 150,
        CancellationToken cancellationToken = default)
    {
        var documents = await _db.Documents
            .Where(x => x.ProjectId == projectId)
            .ToListAsync(cancellationToken);

        if (documents.Count == 0) return 0;

        var processedDocuments = 0;

        foreach (var document in documents)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                _logger.LogInformation(
                    "Re-chunking document {FileName} into namespaces [{Ns}]",
                    document.FileName, string.Join(", ", Namespaces.All));

                await _db.DocumentChunks
                    .Where(x => x.DocumentId == document.Id
                                && Namespaces.All.Contains(x.Namespace))
                    .ExecuteDeleteAsync(cancellationToken);

                var extractedDocument = await _textExtractor.ExtractAsync(
                    document.FilePath, cancellationToken);

                if (string.IsNullOrWhiteSpace(extractedDocument.Text))
                {
                    document.Status = "Completed";
                    document.ProcessedAtUtc = DateTime.UtcNow;
                    processedDocuments++;
                    continue;
                }

                var isExcel = IsExcel(document.FilePath);

                foreach (var targetNs in Namespaces.All)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var chunks = BuildChunksForNamespace(
                        document, extractedDocument, isExcel, targetNs,
                        chunkSize, overlap, minChunkChars);

                    await EmbedAndAddChunksAsync(chunks, cancellationToken);
                }

                document.Status = "Completed";
                document.ProcessedAtUtc = DateTime.UtcNow;
                processedDocuments++;
            }
            catch (Exception ex)
            {
                document.Status = "Failed";
                _logger.LogError(ex,
                    "Failed to re-chunk document {DocumentId} - {FileName}",
                    document.Id, document.FileName);
            }
        }

        await _db.SaveChangesAsync(cancellationToken);
        return processedDocuments;
    }

    private async Task ProcessDocumentAsync(
        Document document,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Processing document {FileName} into namespaces [{Ns}]",
            document.FileName, string.Join(", ", Namespaces.All));

        var extractedDocument = await _textExtractor.ExtractAsync(
            document.FilePath, cancellationToken);

        if (string.IsNullOrWhiteSpace(extractedDocument.Text))
        {
            document.Status = "Completed";
            document.ProcessedAtUtc = DateTime.UtcNow;
            return;
        }

        var isExcel = IsExcel(document.FilePath);

        foreach (var targetNs in Namespaces.All)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var chunks = BuildChunksForNamespace(
                document, extractedDocument, isExcel, targetNs);

            await EmbedAndAddChunksAsync(chunks, cancellationToken);

            _logger.LogInformation(
                "Processed {FileName} -> namespace {Ns}: {ChunkCount} chunks",
                document.FileName, targetNs, chunks.Count);
        }

        document.Status = "Completed";
        document.ProcessedAtUtc = DateTime.UtcNow;
    }

    private List<DocumentChunk> BuildChunksForNamespace(
        Document document,
        ExtractedDocument extractedDocument,
        bool isExcel,
        string ns,
        int chunkSize = 600,
        int overlap = 100,
        int minChunkChars = 150)
    {
        if (isExcel)
            return CreateExcelRowChunks(document.Id, extractedDocument, ns);

        // Destructure explicitly to avoid named-tuple inference issues.
        int size, ovl, min;
        if (ChunkConfig.TryGetValue(ns, out var cfg))
        {
            (size, ovl, min) = cfg;
        }
        else
        {
            size = chunkSize;
            ovl = overlap;
            min = minChunkChars;
        }

        return _chunker.Chunk(
            document.Id, extractedDocument, size, ovl, min, ns);
    }

    private async Task EmbedAndAddChunksAsync(
        List<DocumentChunk> chunks,
        CancellationToken cancellationToken)
    {
        foreach (var chunk in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var embedding = await _embeddingService
                .GenerateDocumentEmbeddingAsync(chunk.Content, cancellationToken);

            chunk.Embedding = new Pgvector.Vector(embedding);
        }

        await _db.DocumentChunks.AddRangeAsync(chunks, cancellationToken);
    }

    private static bool IsExcel(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        return ext.Equals(".xlsx", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".xls", StringComparison.OrdinalIgnoreCase);
    }

    private static List<DocumentChunk> CreateExcelRowChunks(
        Guid documentId,
        ExtractedDocument extractedDocument,
        string ns)
    {
        var sections = extractedDocument.Sections
            .Where(s => s.ContentType == "ExcelRow" ||
                        s.ContentType == "ExcelWorksheet");

        var chunks = new List<DocumentChunk>();
        var chunkIndex = 0;

        foreach (var section in sections)
        {
            if (string.IsNullOrWhiteSpace(section.Content))
                continue;

            var metadata = new Dictionary<string, string>(section.Metadata)
            {
                ["RecordType"] = section.ContentType
            };

            chunks.Add(new DocumentChunk
            {
                Id = Guid.NewGuid(),
                DocumentId = documentId,
                ChunkIndex = chunkIndex++,
                Content = section.Content,
                TokenCount = Math.Max(1, section.Content.Length / 4),
                SectionTitle = section.Heading,
                SourceLocation = section.SourceLocation,
                Namespace = ns,
                MetadataJson = System.Text.Json.JsonSerializer.Serialize(metadata)
            });
        }

        return chunks;
    }
}