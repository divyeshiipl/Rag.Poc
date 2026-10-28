namespace Rag.Poc.Models;

public class OllamaResponse
{
    public string Response { get; set; } = string.Empty;
    public long LoadDuration { get; set; }
    public long PromptEvalDuration { get; set; }
    public long EvalDuration { get; set; }
    public long TotalDuration { get; set; }
    public int PromptEvalCount { get; set; }
    public int EvalCount { get; set; }
}
