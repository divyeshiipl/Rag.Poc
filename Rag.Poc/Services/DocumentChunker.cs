using System.Text.Json;

namespace Rag.Poc.Services;

public class DocumentChunker
{
    public List<DocumentChunk> Chunk(
        Guid documentId,
        ExtractedDocument document,
        int chunkSize = 600,
        int overlap = 100,
        int minChunkChars = 150,
        string ns = "default")
    {
        if (chunkSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(chunkSize));
        if (overlap < 0)
            throw new ArgumentOutOfRangeException(nameof(overlap));
        if (overlap >= chunkSize)
            throw new ArgumentException("Overlap must be smaller than chunk size.");
        if (minChunkChars < 0)
            throw new ArgumentOutOfRangeException(nameof(minChunkChars));

        var chunks = new List<DocumentChunk>();
        var chunkIndex = 0;

        foreach (var section in document.Sections)
        {
            var sectionChunks = ChunkSection(
                documentId, section, chunkSize, overlap, minChunkChars, ns, ref chunkIndex);
            chunks.AddRange(sectionChunks);
        }

        return chunks;
    }

    private static List<DocumentChunk> ChunkSection(
        Guid documentId,
        DocumentSection section,
        int chunkSize,
        int overlap,
        int minChunkChars,
        string ns,
        ref int chunkIndex)
    {
        var chunks = new List<DocumentChunk>();

        var sectionText = BuildSectionText(section);
        if (string.IsNullOrWhiteSpace(sectionText))
            return chunks;

        var blocks = SplitIntoLogicalBlocks(sectionText);

        var currentBlocks = new List<string>();
        var currentLength = 0;

        foreach (var block in blocks)
        {
            if (block.Length > chunkSize)
            {
                FlushCurrentChunk(chunks, documentId, section, currentBlocks,
                    minChunkChars, ns, ref currentLength, ref chunkIndex);

                var smallerBlocks = SplitLargeBlock(block, chunkSize);
                foreach (var smallerBlock in smallerBlocks)
                {
                    chunks.Add(CreateChunk(documentId, section, smallerBlock, ns, chunkIndex++));
                }
                continue;
            }

            var additionalLength = currentBlocks.Count == 0
                ? block.Length
                : block.Length + 2;

            if (currentLength + additionalLength > chunkSize)
            {
                var overlapBlocks = GetOverlapBlocks(currentBlocks, overlap);
                FlushCurrentChunk(chunks, documentId, section, currentBlocks,
                    minChunkChars, ns, ref currentLength, ref chunkIndex);

                currentBlocks = overlapBlocks;
                currentLength = CalculateLength(currentBlocks);

                if (currentLength + block.Length + 2 > chunkSize)
                {
                    currentBlocks.Clear();
                    currentLength = 0;
                }
            }

            currentBlocks.Add(block);
            currentLength = CalculateLength(currentBlocks);
        }

        FlushCurrentChunk(chunks, documentId, section, currentBlocks,
            minChunkChars, ns, ref currentLength, ref chunkIndex);

        return chunks;
    }

    private static string BuildSectionText(DocumentSection section)
    {
        var heading = section.Heading?.Trim();
        var content = section.Content?.Trim();

        if (string.IsNullOrWhiteSpace(heading)) return content ?? string.Empty;
        if (string.IsNullOrWhiteSpace(content)) return heading;

        return $"{heading}\n\n{content}";
    }

    private static List<string> SplitIntoLogicalBlocks(string text)
    {
        var paragraphs = Regex
            .Split(text.Trim(), @"\n\s*\n")
            .Select(x => x.TrimEnd())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();

        var result = new List<string>();
        foreach (var paragraph in paragraphs)
        {
            if (ContainsNumberedList(paragraph))
            {
                result.Add(paragraph);
                continue;
            }

            var sentences = SplitSentences(paragraph);
            result.AddRange(sentences);
        }
        return result;
    }

    private static bool ContainsNumberedList(string text)
        => Regex.IsMatch(text, @"(?m)^\d+[\.\)]\s+");

    private static List<string> SplitSentences(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        return Regex
            .Split(text.Trim(), @"(?<=[.!?])\s+(?=[\p{L}\p{N}""'“‘(\[])")
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();
    }

    private static List<string> SplitLargeBlock(string block, int chunkSize)
    {
        var words = block.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var chunks = new List<string>();
        var currentWords = new List<string>();
        var currentLength = 0;

        foreach (var word in words)
        {
            var requiredLength = currentWords.Count == 0 ? word.Length : word.Length + 1;
            if (currentWords.Count > 0 && currentLength + requiredLength > chunkSize)
            {
                chunks.Add(string.Join(" ", currentWords));
                currentWords.Clear();
                currentLength = 0;
            }
            currentWords.Add(word);
            currentLength = CalculateLength(currentWords);
        }
        if (currentWords.Count > 0)
            chunks.Add(string.Join(" ", currentWords));

        return chunks;
    }

    private static List<string> GetOverlapBlocks(List<string> blocks, int overlap)
    {
        if (blocks.Count == 0 || overlap <= 0) return [];

        var result = new List<string>();
        var length = 0;

        for (var i = blocks.Count - 1; i >= 0; i--)
        {
            var block = blocks[i];
            var requiredLength = result.Count == 0 ? block.Length : block.Length + 2;
            if (result.Count > 0 && length + requiredLength > overlap) break;

            result.Insert(0, block);
            length = CalculateLength(result);
        }
        return result;
    }

    private static void FlushCurrentChunk(
        List<DocumentChunk> chunks,
        Guid documentId,
        DocumentSection section,
        List<string> blocks,
        int minChunkChars,
        string ns,
        ref int currentLength,
        ref int chunkIndex)
    {
        if (blocks.Count == 0)
        {
            currentLength = 0;
            return;
        }

        var content = string.Join("\n\n", blocks).Trim();

        if (!string.IsNullOrWhiteSpace(content) && content.Length >= minChunkChars)
        {
            chunks.Add(CreateChunk(documentId, section, content, ns, chunkIndex++));
        }
        else if (!string.IsNullOrWhiteSpace(content))
        {
            chunks.Add(CreateChunk(documentId, section, content, ns, chunkIndex++));
        }

        blocks.Clear();
        currentLength = 0;
    }

    private static int CalculateLength(List<string> blocks)
    {
        if (blocks.Count == 0) return 0;
        return blocks.Sum(x => x.Length) + ((blocks.Count - 1) * 2);
    }

    private static DocumentChunk CreateChunk(
        Guid documentId,
        DocumentSection section,
        string content,
        string ns,
        int chunkIndex)
    {
        return new DocumentChunk
        {
            Id = Guid.NewGuid(),
            DocumentId = documentId,
            ChunkIndex = chunkIndex,
            Content = content,
            TokenCount = EstimateTokenCount(content),
            SectionTitle = section.Heading,
            SourceLocation = section.SourceLocation,
            Namespace = ns,
            MetadataJson = section.Metadata.Count == 0
                ? null
                : JsonSerializer.Serialize(section.Metadata)
        };
    }

    private static int EstimateTokenCount(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        return (int)Math.Ceiling(text.Length / 4.0);
    }
}