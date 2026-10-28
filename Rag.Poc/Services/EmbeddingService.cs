namespace Rag.Poc.Services;

/// <summary>
/// Creates embeddings with the BGE-small ONNX model for documents and queries.
/// </summary>
public sealed class EmbeddingService : IEmbeddingService, IDisposable
{
    private readonly InferenceSession _session;
    private readonly BertTokenizer _tokenizer;
    private readonly BgeSmallOptions _options;

    #region Public Methods

    /// <summary>
    /// Loads the BGE-small ONNX model and its vocabulary.
    /// </summary>
    public EmbeddingService(BgeSmallOptions options)
    {
        _options = options;

        if (!File.Exists(options.ModelPath))
            throw new FileNotFoundException("BGE-small ONNX model was not found.", options.ModelPath);

        if (!File.Exists(options.VocabularyPath))
            throw new FileNotFoundException("BGE-small vocabulary was not found.", options.VocabularyPath);

        _session = new InferenceSession(options.ModelPath);
        _tokenizer = BertTokenizer.Create(options.VocabularyPath);
    }

    /// <summary>
    /// Embeds document text as-is (no query prefix).
    /// </summary>
    public Task<float[]> GenerateDocumentEmbeddingAsync(string text, CancellationToken cancellationToken = default)
    {
        return GenerateEmbeddingAsync(text, cancellationToken);
    }

    /// <summary>
    /// Embeds query text with the BGE query instruction prefix.
    /// </summary>
    public Task<float[]> GenerateQueryEmbeddingAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Query cannot be empty.", nameof(text));

        var query = RagConstants.QueryInstruction + text;

        return GenerateEmbeddingAsync(query, cancellationToken);
    }

    #endregion

    #region Private Methods

    /// <summary>
    /// Tokenizes text, runs the ONNX model, takes the CLS vector, L2-normalizes it, and checks its size.
    /// </summary>
    private Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Text cannot be empty.", nameof(text));

        var tokenIds = _tokenizer.EncodeToIds(text);

        if (tokenIds.Count == 0)
            throw new InvalidOperationException("Tokenizer produced no tokens.");

        if (tokenIds.Count > _options.MaxTokens)
            tokenIds = tokenIds.Take(_options.MaxTokens).ToList();

        var inputIds = new DenseTensor<long>(
                new[]
                {
                    1,
                    tokenIds.Count
                });

        var attentionMask = new DenseTensor<long>(
                new[]
                {
                    1,
                    tokenIds.Count
                });

        var tokenTypeIds = new DenseTensor<long>(
                new[]
                {
                    1,
                    tokenIds.Count
                });

        for (var i = 0; i < tokenIds.Count; i++)
        {
            inputIds[0, i] = tokenIds[i];
            attentionMask[0, i] = 1;
            tokenTypeIds[0, i] = 0;
        }

        var inputs = new[]
            {
                NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
                NamedOnnxValue.CreateFromTensor("attention_mask", attentionMask),
                NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypeIds)
            };

        using var results = _session.Run(inputs);

        var lastHiddenState = results.FirstOrDefault(x => x.Name == "last_hidden_state");

        if (lastHiddenState == null)
            throw new InvalidOperationException("BGE-small ONNX model did not return " + "'last_hidden_state'.");

        var tensor = lastHiddenState.AsTensor<float>();
        var embedding = new float[tensor.Dimensions[2]];

        for (var dimension = 0; dimension < embedding.Length; dimension++)
            embedding[dimension] = tensor[0, 0, dimension];

        Normalize(embedding);

        if (embedding.Length != RagConstants.EmbeddingDimension)
            throw new InvalidOperationException($"Expected {RagConstants.EmbeddingDimension} dimensions " + $"but received {embedding.Length}.");

        return Task.FromResult(embedding);
    }

    /// <summary>
    /// Scales the vector in place to unit length (L2 norm = 1).
    /// </summary>
    private static void Normalize(float[] embedding)
    {
        double sum = 0;

        foreach (var value in embedding)
            sum += value * value;

        var magnitude = Math.Sqrt(sum);

        if (magnitude == 0)
            throw new InvalidOperationException("Cannot normalize a zero embedding.");

        for (var i = 0; i < embedding.Length; i++)
            embedding[i] = (float)(embedding[i] / magnitude);
    }

    /// <summary>
    /// Frees the ONNX inference session.
    /// </summary>
    public void Dispose()
    {
        _session.Dispose();
    }

    #endregion
}