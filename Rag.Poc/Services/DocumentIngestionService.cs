namespace Rag.Poc.Services;

/// <summary>
/// Ingests documents: syncs files, extracts text, chunks, and embeds them.
/// </summary>
public class DocumentIngestionService
{
    private readonly RagDbContext _db;
    private readonly DocumentSyncService _syncService;
    private readonly DocumentExtractor _textExtractor;
    private readonly DocumentChunker _chunker;
    private readonly IEmbeddingService _embeddingService;
    private readonly ILogger<DocumentIngestionService> _logger;

    /// <summary>
    /// Chunk settings (size, overlap, min chars) per namespace.
    /// </summary>
    private static readonly Dictionary<string, (int Size, int Overlap, int MinChars)> ChunkConfig = new()
    {
        [Namespaces.Default] = (2000, 400, 150),
        [Namespaces.Structural] = (400, 50, 100),
        [Namespaces.Large] = (2500, 500, 200)
    };

    /// <summary>
    /// Picks the target namespace based on file extension (tabular, long-form, or prose).
    /// </summary>
    private static IReadOnlyList<string> TargetNamespaces(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".xlsx" or ".xls" or ".csv" => new[] { Namespaces.Structural },
            ".sql" or ".log" => new[] { Namespaces.Large },
            _ => new[] { Namespaces.Default }
        };
    }

    /// <summary>
    /// Number of worksheet rows per Excel row-block chunk, per namespace.
    /// </summary>
    private static readonly Dictionary<string, int> ExcelRowsPerBlock = new()
    {
        [Namespaces.Default] = 5,
        [Namespaces.Structural] = 5,
        [Namespaces.Large] = 5
    };

    /// <summary>
    /// Creates the service with its dependencies.
    /// </summary>
    public DocumentIngestionService(RagDbContext db, DocumentSyncService syncService, DocumentExtractor textExtractor, DocumentChunker chunker, IEmbeddingService embeddingService, ILogger<DocumentIngestionService> logger)
    {
        _db = db;
        _syncService = syncService;
        _textExtractor = textExtractor;
        _chunker = chunker;
        _embeddingService = embeddingService;
        _logger = logger;
    }

    #region Public Methods

    /// <summary>
    /// Syncs a project's files and processes new/changed documents (chunk + embed).
    /// </summary>
    public async Task<IngestionResult> ProcessProjectAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var syncResult = await _syncService.SyncProjectAsync(projectId, cancellationToken);

        var result = new IngestionResult
        {
            NewDocuments = syncResult.NewDocuments.Count,
            ChangedDocuments = syncResult.ChangedDocuments.Count,
            UnchangedDocuments = syncResult.UnchangedDocuments.Count,
            DeletedDocuments = syncResult.DeletedDocuments.Count
        };

        var documentsToProcess = syncResult.NewDocuments.Concat(syncResult.ChangedDocuments).ToList();

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
                _logger.LogError(ex, "Failed to process document {DocumentId} - {FileName}",  document.Id, document.FileName);
                document.Status = "Failed";
            }
        }

        await _db.SaveChangesAsync(cancellationToken);
        return result;
    }

    /// <summary>
    /// Re-chunks all of a project's documents, replacing existing chunks.
    /// </summary>
    public async Task<int> RechunkProjectAsync(Guid projectId,  int chunkSize = 600, int overlap = 100, int minChunkChars = 150, CancellationToken cancellationToken = default)
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
                _logger.LogInformation("Re-chunking document {FileName} into namespaces [{Ns}]", document.FileName, string.Join(", ", Namespaces.All));

                await _db.DocumentChunks
                    .Where(x => x.DocumentId == document.Id
                                && Namespaces.All.Contains(x.Namespace))
                    .ExecuteDeleteAsync(cancellationToken);

                var extractedDocument = await _textExtractor.ExtractAsync(document.FilePath, cancellationToken);

                if (string.IsNullOrWhiteSpace(extractedDocument.Text))
                {
                    document.Status = "Completed";
                    document.ProcessedAtUtc = DateTime.UtcNow;
                    processedDocuments++;
                    continue;
                }

                var isExcel = IsExcel(document.FilePath);

                foreach (var targetNs in TargetNamespaces(document.FilePath))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var chunks = BuildChunksForNamespace(document, extractedDocument, isExcel, targetNs, chunkSize, overlap, minChunkChars);
                    await EmbedAndAddChunksAsync(chunks, cancellationToken);
                }

                document.Status = "Completed";
                document.ProcessedAtUtc = DateTime.UtcNow;
                processedDocuments++;
            }
            catch (Exception ex)
            {
                document.Status = "Failed";
                _logger.LogError(ex,"Failed to re-chunk document {DocumentId} - {FileName}", document.Id, document.FileName);
            }
        }

        await _db.SaveChangesAsync(cancellationToken);
        return processedDocuments;
    }

    #endregion

    #region Private Methods

    /// <summary>
    /// Extracts, chunks, and embeds one document across its target namespaces.
    /// </summary>
    private async Task ProcessDocumentAsync(Models.Document document, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Processing document {FileName} into namespaces [{Ns}]", document.FileName, string.Join(", ", Namespaces.All));

        var extractedDocument = await _textExtractor.ExtractAsync(document.FilePath, cancellationToken);

        if (string.IsNullOrWhiteSpace(extractedDocument.Text))
        {
            document.Status = "Completed";
            document.ProcessedAtUtc = DateTime.UtcNow;
            return;
        }

        var isExcel = IsExcel(document.FilePath);

        foreach (var targetNs in TargetNamespaces(document.FilePath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunks = BuildChunksForNamespace(document, extractedDocument, isExcel, targetNs);
            await EmbedAndAddChunksAsync(chunks, cancellationToken);
            _logger.LogInformation("Processed {FileName} -> namespace {Ns}: {ChunkCount} chunks", document.FileName, targetNs, chunks.Count);
        }

        document.Status = "Completed";
        document.ProcessedAtUtc = DateTime.UtcNow;
    }

    /// <summary>
    /// Builds chunks for a namespace: Excel gets its own chunker, others use the configured chunker.
    /// </summary>
    private List<DocumentChunk> BuildChunksForNamespace(Models.Document document, ExtractedDocument extractedDocument, bool isExcel, string ns, int chunkSize = 600, int overlap = 100, int minChunkChars = 150)
    {
        if (isExcel)
            return CreateExcelChunks(document.Id, extractedDocument, ns);

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

    /// <summary>
    /// Generates embeddings for the chunks and stages them for saving.
    /// </summary>
    private async Task EmbedAndAddChunksAsync(List<DocumentChunk> chunks, CancellationToken cancellationToken)
    {
        foreach (var chunk in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var embedding = await _embeddingService.GenerateDocumentEmbeddingAsync(chunk.Content, cancellationToken);
            chunk.Embedding = new Pgvector.Vector(embedding);
        }

        await _db.DocumentChunks.AddRangeAsync(chunks, cancellationToken);
    }

    /// <summary>
    /// Checks whether the file is .xlsx or .xls.
    /// </summary>
    private static bool IsExcel(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        return ext.Equals(".xlsx", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".xls", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Builds chunks from an Excel document's extracted sections.
    /// </summary>
    private static List<DocumentChunk> CreateExcelChunks(Guid documentId, ExtractedDocument extractedDocument, string ns)
    {
        var rowsPerBlock = ExcelRowsPerBlock.TryGetValue(ns, out var r) ? r : 10;

        var schemaSections = extractedDocument.Sections
            .Where(s => s.ContentType is "ExcelWorksheet"
                            or "ExcelAggregates"
                            or "ExcelColumn"
                            or "ExcelNote")
            .ToList();

        var rowSections = extractedDocument.Sections
            .Where(s => s.ContentType == "ExcelRows")
            .ToList();

        var chunks = new List<DocumentChunk>();
        var chunkIndex = 0;

        foreach (var section in schemaSections)
        {
            if (string.IsNullOrWhiteSpace(section.Content)) continue;
            chunks.Add(BuildExcelChunk(documentId, ns, chunkIndex++, section));
        }

        foreach (var section in rowSections)
        {
            if (string.IsNullOrWhiteSpace(section.Content)) continue;

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
                MetadataJson = JsonSerializer.Serialize(new Dictionary<string, string>(section.Metadata)
                    {
                        ["RecordType"] = "ExcelRows"
                    })
            });
        }
        return chunks;
    }

    /// <summary>
    /// Builds a chunk from an Excel schema/aggregate/column/note section.
    /// </summary>
    private static DocumentChunk BuildExcelChunk(Guid documentId, string ns, int chunkIndex, DocumentSection section)
    {
        var metadata = new Dictionary<string, string>(section.Metadata)
        {
            ["RecordType"] = section.ContentType
        };

        return new DocumentChunk
        {
            Id = Guid.NewGuid(),
            DocumentId = documentId,
            ChunkIndex = chunkIndex,
            Content = section.Content,
            TokenCount = Math.Max(1, section.Content.Length / 4),
            SectionTitle = section.Heading,
            SourceLocation = section.SourceLocation,
            Namespace = ns,
            MetadataJson = JsonSerializer.Serialize(metadata)
        };
    }

    #endregion
}