namespace Rag.Poc.Services;

/// <summary>
/// Provides embedding functionality using the BGE-small model.
/// </summary>
public sealed class EmbeddingService : IEmbeddingService, IDisposable
{
    private readonly InferenceSession _session;
    private readonly BertTokenizer _tokenizer;
    private readonly BgeSmallOptions _options;

    #region Public Methods
    /// <summary>
    /// Initializes a new instance of the <see cref="EmbeddingService"/> class.
    /// </summary>
    /// <param name="options"></param>
    /// <exception cref="FileNotFoundException"></exception>
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
    /// Generates an embedding for the given document text.
    /// </summary>
    /// <param name="text"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public Task<float[]> GenerateDocumentEmbeddingAsync(string text, CancellationToken cancellationToken = default)
    {
        return GenerateEmbeddingAsync(text, cancellationToken);
    }

    /// <summary>
    /// Generates an embedding for the given query text.
    /// </summary>
    /// <param name="text"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentException"></exception>
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
    /// Generates an embedding for the given text.
    /// </summary>
    /// <param name="text"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentException"></exception>
    /// <exception cref="InvalidOperationException"></exception>
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
    /// To normalize the emdbeddings
    /// </summary>
    /// <param name="embedding"></param>
    /// <exception cref="InvalidOperationException"></exception>
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
    /// To dispose the session
    /// </summary>
    public void Dispose()
    {
        _session.Dispose();
    }

    #endregion
}