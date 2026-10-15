using Microsoft.EntityFrameworkCore;
using Pgvector;
using Pgvector.EntityFrameworkCore;
using Rag.Poc.Data;
using Rag.Poc.Models;
using System.Text.RegularExpressions;

namespace Rag.Poc.Services;

public sealed class DocumentSearchService
{
    private readonly RagDbContext _db;
    private readonly IEmbeddingService _embeddingService;

    public DocumentSearchService(
        RagDbContext db,
        IEmbeddingService embeddingService)
    {
        _db = db;
        _embeddingService = embeddingService;
    }

    public async Task<List<DocumentChunk>> SearchAsync(
        string query,
        int topK = 5,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new ArgumentException(
                "Query cannot be empty.",
                nameof(query));

        if (topK <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(topK));

        var identifierMatch = Regex.Match(
            query,
            @"\b[A-Z]{2,}-\d+\b",
            RegexOptions.IgnoreCase);

        if (identifierMatch.Success)
        {
            var identifier = identifierMatch.Value.ToUpperInvariant();

            var exactMatches = await _db.DocumentChunks
                .AsNoTracking()
                .Include(x => x.Document)
                .Where(x =>
                    x.MetadataJson != null &&
                    x.MetadataJson.Contains("\"RecordType\":\"ExcelRow\"") &&
                    x.Content.Contains(identifier))
                .OrderBy(x => x.ChunkIndex)
                .Take(topK)
                .ToListAsync(cancellationToken);

            if (exactMatches.Count > 0)
                return exactMatches;
        }

        var embedding =
            await _embeddingService
                .GenerateQueryEmbeddingAsync(
                    query,
                    cancellationToken);

        var queryVector =
            new Vector(embedding);

        return await _db.DocumentChunks
            .AsNoTracking()
            .Include(x => x.Document)
            .Where(x => x.Embedding != null)
            .OrderBy(
                x => x.Embedding!
                    .CosineDistance(queryVector))
            .Take(topK)
            .ToListAsync(cancellationToken);
    }
}
