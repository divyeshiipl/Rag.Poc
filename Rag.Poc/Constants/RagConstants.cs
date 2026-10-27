public class RagConstants
{
    public const string RetriveModel = "qwen2.5:3b-instruct";
    public const string Model = "qwen2.5:3b-instruct";

    public static readonly string[] AllowedNamespaces = Namespaces.All.ToArray();

    public const int DefaultTopK = 20;
    public const int MaxTopK = 25;
    public const int MaxSubQuestions = 8;
    public const int RrfK = 60;
    public const int MaxRerankCandidates = 20;
    public const int EmbeddingDimension = 384;
    public const string QueryInstruction =  "Represent this sentence for searching relevant passages: ";

    public const int MaxCharsPerChunk = 3_000;
    public const int MaxTotalContextChars = 16_000;
    public const int NumCtx = 8192;
    public const int NumPredict = 2000;

    public const string RerankModel = "llama3.2:latest";
    public const int MaxPassageChars = 500;
}