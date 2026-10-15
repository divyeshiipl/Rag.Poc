using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;
using Rag.Poc.Models;

namespace Rag.Poc.Services;

public sealed class BgeSmallEmbeddingService :
    IEmbeddingService,
    IDisposable
{
    private const int EmbeddingDimension = 384;

    private const string QueryInstruction =
        "Represent this sentence for searching relevant passages: ";

    private readonly InferenceSession _session;
    private readonly BertTokenizer _tokenizer;
    private readonly BgeSmallOptions _options;

    public BgeSmallEmbeddingService(
        BgeSmallOptions options)
    {
        _options = options;

        if (!File.Exists(options.ModelPath))
        {
            throw new FileNotFoundException(
                "BGE-small ONNX model was not found.",
                options.ModelPath);
        }

        if (!File.Exists(options.VocabularyPath))
        {
            throw new FileNotFoundException(
                "BGE-small vocabulary was not found.",
                options.VocabularyPath);
        }

        _session =
            new InferenceSession(
                options.ModelPath);

        _tokenizer =
            BertTokenizer.Create(
                options.VocabularyPath);
    }

    public Task<float[]> GenerateDocumentEmbeddingAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        return GenerateEmbeddingAsync(
            text,
            cancellationToken);
    }

    public Task<float[]> GenerateQueryEmbeddingAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException(
                "Query cannot be empty.",
                nameof(text));
        }

        var query =
            QueryInstruction + text;

        return GenerateEmbeddingAsync(
            query,
            cancellationToken);
    }

    private Task<float[]> GenerateEmbeddingAsync(
        string text,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException(
                "Text cannot be empty.",
                nameof(text));
        }

        var tokenIds = _tokenizer.EncodeToIds(text);

        if (tokenIds.Count == 0)
        {
            throw new InvalidOperationException(
                "Tokenizer produced no tokens.");
        }

        if (tokenIds.Count > _options.MaxTokens)
        {
            tokenIds =
                tokenIds
                    .Take(_options.MaxTokens)
                    .ToList();
        }

        var inputIds =
            new DenseTensor<long>(
                new[]
                {
                    1,
                    tokenIds.Count
                });

        var attentionMask =
            new DenseTensor<long>(
                new[]
                {
                    1,
                    tokenIds.Count
                });

        var tokenTypeIds =
            new DenseTensor<long>(
                new[]
                {
                    1,
                    tokenIds.Count
                });

        for (var i = 0; i < tokenIds.Count; i++)
        {
            inputIds[0, i] =
                tokenIds[i];

            attentionMask[0, i] = 1;

            tokenTypeIds[0, i] = 0;
        }

        var inputs =
            new[]
            {
                NamedOnnxValue.CreateFromTensor(
                    "input_ids",
                    inputIds),

                NamedOnnxValue.CreateFromTensor(
                    "attention_mask",
                    attentionMask),

                NamedOnnxValue.CreateFromTensor(
                    "token_type_ids",
                    tokenTypeIds)
            };

        using var results =
            _session.Run(inputs);

        var lastHiddenState =
            results.FirstOrDefault(
                x => x.Name == "last_hidden_state");

        if (lastHiddenState == null)
        {
            throw new InvalidOperationException(
                "BGE-small ONNX model did not return " +
                "'last_hidden_state'.");
        }

        var tensor =
            lastHiddenState.AsTensor<float>();

        var embedding =
            MeanPool(
                tensor,
                attentionMask);

        Normalize(embedding);

        if (embedding.Length != EmbeddingDimension)
        {
            throw new InvalidOperationException(
                $"Expected {EmbeddingDimension} dimensions " +
                $"but received {embedding.Length}.");
        }

        return Task.FromResult(embedding);
    }

    private static float[] MeanPool(
        Tensor<float> hiddenState,
        Tensor<long> attentionMask)
    {
        var dimensions =
            hiddenState.Dimensions.ToArray();

        if (dimensions.Length != 3)
        {
            throw new InvalidOperationException(
                "Expected last_hidden_state to have " +
                "shape [batch, sequence, hidden].");
        }

        var sequenceLength =
            dimensions[1];

        var hiddenSize =
            dimensions[2];

        var embedding =
            new float[hiddenSize];

        var tokenCount = 0;

        for (var token = 0;
             token < sequenceLength;
             token++)
        {
            if (attentionMask[0, token] == 0)
                continue;

            tokenCount++;

            for (var dimension = 0;
                 dimension < hiddenSize;
                 dimension++)
            {
                embedding[dimension] +=
                    hiddenState[
                        0,
                        token,
                        dimension];
            }
        }

        if (tokenCount == 0)
        {
            throw new InvalidOperationException(
                "No active tokens were found.");
        }

        for (var dimension = 0;
             dimension < hiddenSize;
             dimension++)
        {
            embedding[dimension] /=
                tokenCount;
        }

        return embedding;
    }

    private static void Normalize(
        float[] embedding)
    {
        double sum = 0;

        foreach (var value in embedding)
        {
            sum += value * value;
        }

        var magnitude =
            Math.Sqrt(sum);

        if (magnitude == 0)
        {
            throw new InvalidOperationException(
                "Cannot normalize a zero embedding.");
        }

        for (var i = 0;
             i < embedding.Length;
             i++)
        {
            embedding[i] =
                (float)(
                    embedding[i] /
                    magnitude);
        }
    }

    public void Dispose()
    {
        _session.Dispose();
    }
}

public interface IEmbeddingService
{
    Task<float[]> GenerateDocumentEmbeddingAsync(
        string text,
        CancellationToken cancellationToken = default);

    Task<float[]> GenerateQueryEmbeddingAsync(
        string text,
        CancellationToken cancellationToken = default);
}
