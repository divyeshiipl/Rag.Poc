namespace Rag.Poc.Models;

/// <summary>
/// Represents the response from the Ollama API.
/// </summary>
public sealed class OllamaResponse
{
    public string Response { get; set; } = string.Empty;

    [JsonPropertyName("total_duration")]
    public long TotalDuration { get; set; }

    [JsonPropertyName("load_duration")]
    public long LoadDuration { get; set; }

    [JsonPropertyName("prompt_eval_duration")]
    public long PromptEvalDuration { get; set; }

    [JsonPropertyName("eval_duration")]
    public long EvalDuration { get; set; }

    [JsonPropertyName("prompt_eval_count")]
    public int PromptEvalCount { get; set; }

    [JsonPropertyName("eval_count")]
    public int EvalCount { get; set; }
}
