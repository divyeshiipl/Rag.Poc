using System.Text.Json;

namespace Rag.Poc.Services;

/// <summary>
/// Provides functionality for chunking documents into smaller pieces.
/// </summary>
public class DocumentChunker
{
    /// <summary>
    /// Chunks a document into smaller pieces.
    /// </summary>
    /// <param name="documentId"></param>
    /// <param name="document"></param>
    /// <param name="chunkSize"></param>
    /// <param name="overlap"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentOutOfRangeException"></exception>
    /// <exception cref="ArgumentException"></exception>
    public List<DocumentChunk> Chunk(Guid documentId, ExtractedDocument document, int chunkSize = 1000, int overlap = 200)
    {
        if (chunkSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(chunkSize));

        if (overlap < 0)
            throw new ArgumentOutOfRangeException(nameof(overlap));

        if (overlap >= chunkSize)
            throw new ArgumentException("Overlap must be smaller than chunk size.");

        var chunks = new List<DocumentChunk>();

        var chunkIndex = 0;

        foreach (var section in document.Sections)
        {
            var sectionChunks = ChunkSection(documentId, section, chunkSize, overlap, ref chunkIndex);
            chunks.AddRange(sectionChunks);
        }

        return chunks;
    }

    #region Private Helper Methods

    /// <summary>
    /// Chunks a document section into smaller pieces.
    /// </summary>
    /// <param name="documentId"></param>
    /// <param name="section"></param>
    /// <param name="chunkSize"></param>
    /// <param name="overlap"></param>
    /// <param name="chunkIndex"></param>
    /// <returns></returns>
    private static List<DocumentChunk> ChunkSection(Guid documentId, DocumentSection section, int chunkSize, int overlap, ref int chunkIndex)
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
                FlushCurrentChunk(chunks, documentId, section, currentBlocks, ref currentLength, ref chunkIndex);

                var heading = section.Heading?.Trim();
                var headingPrefix = string.IsNullOrWhiteSpace(heading) ? string.Empty : heading + "\n\n";
                var availableSize = chunkSize - headingPrefix.Length;

                if (availableSize <= 0)
                {
                    headingPrefix = string.Empty;
                    availableSize = chunkSize;
                }

                var smallerBlocks = SplitLargeBlock(block, availableSize);

                foreach (var smallerBlock in smallerBlocks)
                {
                    var content = headingPrefix + smallerBlock;

                    chunks.Add(CreateChunk(documentId, section, content, chunkIndex++));
                }

                continue;
            }

            var additionalLength = currentBlocks.Count == 0 ? block.Length : block.Length + 2;

            if (currentLength + additionalLength > chunkSize)
            {
                var overlapBlocks = GetOverlapBlocks(currentBlocks, overlap);

                FlushCurrentChunk(chunks, documentId, section, currentBlocks, ref currentLength, ref chunkIndex);

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

        FlushCurrentChunk(chunks, documentId, section, currentBlocks, ref currentLength, ref chunkIndex);

        return chunks;
    }

    /// <summary>
    /// Builds the text for a document section.
    /// </summary>
    /// <param name="section"></param>
    /// <returns></returns>
    private static string BuildSectionText(DocumentSection section)
    {
        var heading = section.Heading?.Trim();
        var content = section.Content?.Trim();

        if (string.IsNullOrWhiteSpace(heading))
            return content ?? string.Empty;

        if (string.IsNullOrWhiteSpace(content))
            return heading;

        return $"{heading}\n\n{content}";
    }

    /// <summary>
    /// Splits the text into logical blocks.
    /// </summary>
    /// <param name="text"></param>
    /// <returns></returns>
    private static List<string> SplitIntoLogicalBlocks(string text)
    {
        var paragraphs = Regex.Split(text.Trim(), @"\n\s*\n").Select(x => x.Trim()).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
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

    /// <summary>
    /// Determines whether the text contains a numbered list.
    /// </summary>
    /// <param name="text"></param>
    /// <returns></returns>
    private static bool ContainsNumberedList(string text)
    {
        return Regex.IsMatch(text, @"(?m)^\d+[\.\)]\s+");
    }

    /// <summary>
    /// Splits the text into sentences.
    /// </summary>
    /// <param name="text"></param>
    /// <returns></returns>
    private static List<string> SplitSentences(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        return Regex
            .Split(
                text.Trim(),
                @"(?<=[.!?])\s+(?=[\p{L}\p{N}""'“‘(\[])")
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();
    }

    /// <summary>
    /// Splits a large text block into smaller chunks.
    /// </summary>
    /// <param name="block"></param>
    /// <param name="chunkSize"></param>
    /// <returns></returns>
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

    /// <summary>
    /// Gets the overlapping blocks from a list of text blocks.
    /// </summary>
    /// <param name="blocks"></param>
    /// <param name="overlap"></param>
    /// <returns></returns>
    private static List<string> GetOverlapBlocks(List<string> blocks, int overlap)
    {
        if (blocks.Count == 0 || overlap <= 0)
            return [];

        var result = new List<string>();
        var length = 0;

        for (var i = blocks.Count - 1; i >= 0; i--)
        {
            var block = blocks[i];
            var requiredLength = result.Count == 0 ? block.Length : block.Length + 2;

            if (result.Count > 0 && length + requiredLength > overlap)
                break;

            result.Insert(0, block);

            length = CalculateLength(result);
        }

        return result;
    }

    /// <summary>
    /// Flushes the current chunk to the list of chunks.
    /// </summary>
    /// <param name="chunks"></param>
    /// <param name="documentId"></param>
    /// <param name="section"></param>
    /// <param name="blocks"></param>
    /// <param name="currentLength"></param>
    /// <param name="chunkIndex"></param>
    private static void FlushCurrentChunk(List<DocumentChunk> chunks, Guid documentId, DocumentSection section, List<string> blocks, ref int currentLength, ref int chunkIndex)
    {
        if (blocks.Count == 0)
        {
            currentLength = 0;
            return;
        }

        var content = string.Join("\n\n", blocks).Trim();

        if (!string.IsNullOrWhiteSpace(content))
            chunks.Add(CreateChunk(documentId, section, content, chunkIndex++));

        blocks.Clear();

        currentLength = 0;
    }

    /// <summary>
    /// Calculates the total length of the text blocks.
    /// </summary>
    /// <param name="blocks"></param>
    /// <returns></returns>
    private static int CalculateLength(List<string> blocks)
    {
        if (blocks.Count == 0)
            return 0;

        return blocks.Sum(x => x.Length) + ((blocks.Count - 1) * 2);
    }

    /// <summary>
    /// Creates a new document chunk.
    /// </summary>
    /// <param name="documentId"></param>
    /// <param name="section"></param>
    /// <param name="content"></param>
    /// <param name="chunkIndex"></param>
    /// <returns></returns>
    private static DocumentChunk CreateChunk(Guid documentId, DocumentSection section, string content, int chunkIndex)
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
            MetadataJson = section.Metadata.Count == 0 ? null : JsonSerializer.Serialize(section.Metadata)
        };
    }

    /// <summary>
    /// Estimates the number of tokens in a string.
    /// </summary>
    /// <param name="text"></param>
    /// <returns></returns>
    private static int EstimateTokenCount(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        return (int)Math.Ceiling(text.Length / 4.0);
    }

    #endregion
}