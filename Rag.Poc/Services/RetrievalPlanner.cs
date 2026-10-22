using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Rag.Poc.Services;

namespace Rag.Poc.Models;

public enum RetrievalMode
{
    Semantic,
    Structural,
    Exhaustive,
    Meta
}

public sealed record RetrievalPlan(
    string SearchQuery,
    int? TopK,
    List<string> Namespaces,
    RetrievalMode Mode,
    string? FilterText = null,
    double? MinScore = null,
    bool Rewritten = false);

public interface IRetrievalPlanner
{
    Task<RetrievalPlan> CreatePlanAsync(
        string question,
        CancellationToken cancellationToken = default);
}

public sealed class OllamaRetrievalPlanner : IRetrievalPlanner
{
    private const string Model = "qwen2.5:3b-instruct";

    private static readonly string[] AllowedNamespaces = Namespaces.All.ToArray();

    private const int DefaultTopK = 10;
    private const int MaxTopK = 25;
    private const int MaxSubQuestions = 8;

    private readonly HttpClient _httpClient;
    private readonly ILogger<OllamaRetrievalPlanner> _logger;

    public OllamaRetrievalPlanner(
        HttpClient httpClient,
        ILogger<OllamaRetrievalPlanner> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<RetrievalPlan> CreatePlanAsync(
        string question,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(question))
            throw new ArgumentException("Question cannot be empty.", nameof(question));

        var cleaned = StripDocumentReference(question.Trim());

        if (IsTrivial(cleaned))
            return MetaPlan(cleaned);

        var subQuestions = SplitSubQuestions(cleaned);

        if (subQuestions.Count == 1)
            return await PlanOneAsync(subQuestions[0], cancellationToken);

        _logger.LogInformation(
            "Multi-part question: {Count} sub-questions detected.", subQuestions.Count);

        var subPlans = new List<RetrievalPlan>(subQuestions.Count);
        foreach (var sub in subQuestions)
            subPlans.Add(await PlanOneAsync(sub, cancellationToken));

        return MergeSubPlans(cleaned, subQuestions, subPlans);
    }

    private static string StripDocumentReference(string question)
    {
        var patterns = new[]
        {
            @"^According to (?:the )?[^,]+?,\s*(?<q>.+)$",
            @"^In (?:the )?[^,]+?,\s*(?<q>.+)$",
            @"^From (?:the )?[^,]+?,\s*(?<q>.+)$",
            @"^Per (?:the )?[^,]+?,\s*(?<q>.+)$",
        };

        foreach (var p in patterns)
        {
            var m = Regex.Match(question, p,
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (m.Success)
            {
                var q = m.Groups["q"].Value.Trim();
                if (q.Length >= 8)
                    return q;
            }
        }

        return question;
    }

    private static List<string> SplitSubQuestions(string input)
    {
        var qmParts = input
            .Split('?', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToList();

        if (qmParts.Count >= 2 && qmParts.Any(p => p.Length >= 8))
            return qmParts.Take(MaxSubQuestions).ToList();

        var markerPositions = FindListMarkerPositions(input);
        if (markerPositions.Count >= 2)
        {
            var parts = new List<string>();
            for (int i = 0; i < markerPositions.Count; i++)
            {
                var start = markerPositions[i];
                var end = i + 1 < markerPositions.Count
                    ? markerPositions[i + 1]
                    : input.Length;

                var piece = input[start..end].Trim().TrimEnd('.', '?', '!', ':', ';');
                if (piece.Length > 0) parts.Add(piece);
            }
            if (parts.Count >= 2)
                return parts.Take(MaxSubQuestions).ToList();
        }

        foreach (var connector in new[] { " and ", " or " })
        {
            var idx = input.IndexOf(connector, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;

            var secondIdx = input.IndexOf(
                connector, idx + connector.Length, StringComparison.OrdinalIgnoreCase);
            if (secondIdx >= 0) continue;

            var left = input[..idx].Trim();
            var right = input[(idx + connector.Length)..].Trim();

            if (left.Length >= 8 && right.Length >= 8
                && left.Any(char.IsLetter) && right.Any(char.IsLetter))
            {
                return new List<string> { TrimTrailing(left), TrimTrailing(right) };
            }
        }

        return new List<string> { input };
    }

    private static List<int> FindListMarkerPositions(string input)
    {
        var positions = new List<int>();
        int i = 0;

        while (i < input.Length)
        {
            bool atBoundary = i == 0 || char.IsWhiteSpace(input[i - 1]);
            if (!atBoundary) { i++; continue; }

            int prefixLen = 0;
            while (i < input.Length && prefixLen < 2 && char.IsLetterOrDigit(input[i]))
            {
                i++;
                prefixLen++;
            }

            if (i < input.Length && IsMarkerPunctuation(input[i]))
            {
                int afterMarker = i + 1;
                if (afterMarker < input.Length && char.IsWhiteSpace(input[afterMarker]))
                {
                    int j = afterMarker;
                    while (j < input.Length && char.IsWhiteSpace(input[j])) j++;
                    positions.Add(j);
                    i = j;
                    continue;
                }
            }

            if (prefixLen == 0 && i < input.Length && IsBareBullet(input[i]))
            {
                int afterBullet = i + 1;
                if (afterBullet < input.Length && char.IsWhiteSpace(input[afterBullet]))
                {
                    int j = afterBullet;
                    while (j < input.Length && char.IsWhiteSpace(input[j])) j++;
                    positions.Add(j);
                    i = j;
                    continue;
                }
            }

            if (prefixLen == 0) i++;
        }

        return positions;
    }

    private static bool IsMarkerPunctuation(char c) =>
        c == '.' || c == ')' || c == ']' || c == ':' || c == '•' || c == '*' || c == '-';

    private static bool IsBareBullet(char c) =>
        c == '•' || c == '*' || c == '-';

    private static string TrimTrailing(string s) =>
        s.TrimEnd('.', '?', '!', ':', ';', ' ', '\t', '\n', '\r');

    private async Task<RetrievalPlan> PlanOneAsync(
        string subQuestion,
        CancellationToken cancellationToken)
    {
        var prompt = BuildPrompt(subQuestion);

        var request = new
        {
            model = Model,
            prompt,
            stream = false,
            format = "json",
            keep_alive = "10m",
            options = new
            {
                temperature = 0,
                num_predict = 220,
                top_p = 0.1
            }
        };

        using var response = await _httpClient.PostAsJsonAsync(
            "/api/generate", request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var ollama = await response.Content.ReadFromJsonAsync<OllamaResponse>(
            cancellationToken: cancellationToken);

        if (string.IsNullOrWhiteSpace(ollama?.Response))
            return Fallback(subQuestion);

        PlannerJson? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<PlannerJson>(
                ollama.Response,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    Converters = { new JsonStringEnumConverter() }
                });
        }
        catch (JsonException)
        {
            _logger.LogWarning("Planner returned malformed JSON; using fallback.");
            return Fallback(subQuestion);
        }

        if (parsed is null) return Fallback(subQuestion);

        return ToPlan(parsed, subQuestion);
    }

    private RetrievalPlan ToPlan(PlannerJson parsed, string original)
    {
        var mode = parsed.Mode ?? RetrievalMode.Semantic;
        var confidence = parsed.Confidence ?? 0.5;

        if (mode == RetrievalMode.Meta)
            return MetaPlan(original);

        var namespaces = (parsed.Namespaces ?? Array.Empty<string>())
            .Where(n => !string.IsNullOrWhiteSpace(n) && AllowedNamespaces.Contains(n))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (namespaces.Count == 0)
            namespaces = mode switch
            {
                RetrievalMode.Structural => new List<string> { Namespaces.Structural, Namespaces.Default },
                RetrievalMode.Exhaustive => new List<string> { Namespaces.Structural, Namespaces.Default },
                _ => new List<string> { Namespaces.Default }
            };

        if (confidence < 0.6 && namespaces.Count == 1 && mode != RetrievalMode.Exhaustive)
        {
            var extra = namespaces[0] switch
            {
                Namespaces.Default => Namespaces.Large,
                Namespaces.Structural => Namespaces.Default,
                Namespaces.Large => Namespaces.Default,
                _ => null
            };
            if (extra is not null && !namespaces.Contains(extra))
                namespaces.Add(extra);
        }

        var filterText = string.IsNullOrWhiteSpace(parsed.FilterText)
            ? null
            : parsed.FilterText.Trim();

        if (mode == RetrievalMode.Exhaustive && filterText is null)
        {
            _logger.LogWarning("Exhaustive without filterText; downgrading to Semantic.");
            mode = RetrievalMode.Semantic;
        }

        int? topK = mode == RetrievalMode.Exhaustive
            ? null
            : Math.Clamp(parsed.TopK is > 0 ? parsed.TopK.Value : DefaultTopK, 3, MaxTopK);

        // searchQuery may be pipe-joined sub-queries. Keep as-is if the
        // planner produced a decomposition.
        var searchQuery = string.IsNullOrWhiteSpace(parsed.SearchQuery)
            ? original
            : parsed.SearchQuery.Trim();

        if (searchQuery.Length < 15 && original.Length > searchQuery.Length * 2)
            searchQuery = original;

        return new RetrievalPlan(
            SearchQuery: searchQuery,
            TopK: topK,
            Namespaces: namespaces,
            Mode: mode,
            FilterText: filterText,
            MinScore: mode == RetrievalMode.Exhaustive ? 0 : 0.10,
            Rewritten: !string.Equals(searchQuery, original, StringComparison.Ordinal));
    }

    private static RetrievalPlan MergeSubPlans(
        string original,
        List<string> subQuestions,
        List<RetrievalPlan> subPlans)
    {
        if (subPlans.Count == 1) return subPlans[0];

        var nonMeta = subPlans.Where(p => p.Mode != RetrievalMode.Meta).ToList();
        if (nonMeta.Count == 0) return MetaPlan(original);

        var mode = nonMeta
            .Select(p => p.Mode)
            .OrderByDescending(ModePriority)
            .First();

        var namespaces = nonMeta
            .SelectMany(p => p.Namespaces)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (namespaces.Count == 0)
            namespaces = new List<string> { Namespaces.Default };

        var topK = Math.Clamp(
            nonMeta.Where(p => p.TopK.HasValue)
                .Select(p => p.TopK!.Value)
                .DefaultIfEmpty(DefaultTopK)
                .Max(),
            3, MaxTopK);

        var filters = nonMeta
            .Select(p => p.FilterText)
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        string? filterText = filters.Count == 1 ? filters[0] : null;
        if (mode == RetrievalMode.Exhaustive && filterText is null)
            mode = RetrievalMode.Semantic;

        var combinedQuery = string.Join(" | ", subQuestions);

        return new RetrievalPlan(
            SearchQuery: combinedQuery,
            TopK: topK,
            Namespaces: namespaces,
            Mode: mode,
            FilterText: filterText,
            MinScore: mode == RetrievalMode.Exhaustive ? 0 : 0.10,
            Rewritten: !string.Equals(combinedQuery, original, StringComparison.Ordinal));
    }

    private static int ModePriority(RetrievalMode m) => m switch
    {
        RetrievalMode.Exhaustive => 4,
        RetrievalMode.Structural => 3,
        RetrievalMode.Semantic => 2,
        RetrievalMode.Meta => 1,
        _ => 0
    };

    private static bool IsTrivial(string s)
    {
        var words = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length <= 1 && !s.Contains('?') && !s.Any(char.IsDigit);
    }

    private static RetrievalPlan MetaPlan(string q) => new(
        SearchQuery: q,
        TopK: null,
        Namespaces: new List<string>(),
        Mode: RetrievalMode.Meta,
        FilterText: null,
        MinScore: null,
        Rewritten: false);

    private static RetrievalPlan Fallback(string question) => new(
        SearchQuery: question,
        TopK: DefaultTopK,
        Namespaces: new List<string> { Namespaces.Default, Namespaces.Large },
        Mode: RetrievalMode.Semantic,
        MinScore: 0.10,
        Rewritten: false);

    private static string BuildPrompt(string subQuestion) => $$"""
        You are the retrieval planner for an enterprise document search system.
        Given ONE user question, produce ONE JSON object describing how to search.
        Never answer the question. Never add prose.

        OUTPUT:
        {
          "searchQuery": string,
          "topK": number | null,
          "namespaces": string[],
          "mode": "Semantic" | "Structural" | "Exhaustive" | "Meta",
          "filterText": string | null,
          "confidence": number
        }

        DECISION PROCEDURE — apply in order, stop at first match.

        Step 1 — greeting, thanks, chit-chat, or about the assistant itself?
                 -> mode = "Meta", namespaces = [], topK = null.

        Step 2 — a SPECIFIC VALUE that must be matched verbatim (letters mixed
                 with digits, a symbol inside a word, a date/time/currency/
                 phone/email, a non-Latin proper noun, a product code, a URL,
                 a file path)?
                 -> mode = "Exhaustive", filterText = that exact token,
                    namespaces = ["{{Namespaces.Structural}}","{{Namespaces.Default}}"], topK = null.

        Step 3 — a SUBSET of records defined by a category or value
                 ("customers in Germany", "tickets assigned to Alice",
                 "assets over $5000", "purchases in March")?
                 -> mode = "Exhaustive", filterText = the literal value.

        Step 3b — PROCEDURE / SEQUENCE / ORDER OF STEPS / PROCESS / WORKFLOW
                  ("what is the sequence", "how do we deploy", "what are the
                  steps", "walk me through", "procedure for")?
                 -> mode = "Semantic", topK = 10,
                    namespaces = ["{{Namespaces.Default}}"].

        Step 3c — NARROW SUBSET OF POLICY / EXCEPTION / "beyond" / "additional" /
                  "specifically for X" / "only for Y" / "unlike Z"?
                  Questions that ask for something EXTRA or DIFFERENT from a
                  baseline. These are hard to retrieve with a single embedding
                  — you MUST decompose searchQuery into 2-3 alternative
                  phrasings joined by " | ".
                 -> mode = "Semantic", topK = 12,
                    namespaces = ["{{Namespaces.Default}}"].
                 -> searchQuery MUST contain " | " and 2-3 distinct sub-queries.

        Step 4 — SHAPE of data (columns, fields, tables, data types,
                 primary key, row count, sheet layout)?
                 -> mode = "Structural", topK = 10,
                    namespaces = ["{{Namespaces.Structural}}","{{Namespaces.Default}}"].

        Step 5 — NUMERIC MEASURE (cost, price, amount, revenue, count, rate,
                 average, total, difference, ratio, industry metric)?
                 -> mode = "Semantic", topK = 10,
                    namespaces = ["{{Namespaces.Structural}}","{{Namespaces.Default}}"].

        Step 6 — COMPARE, AGGREGATE, or TREND?
                 -> mode = "Semantic", topK = 12,
                    namespaces = ["{{Namespaces.Large}}","{{Namespaces.Structural}}","{{Namespaces.Default}}"].

        Step 7 — anything else (prose, policies, procedures, definitions).
                 -> mode = "Semantic", topK = 6, namespaces = ["{{Namespaces.Default}}"].

        NAMESPACES
          "{{Namespaces.Default}}"        prose, narrative, policies, procedures, FAQs.
          "{{Namespaces.Structural}}"  tables, rows, schemas, any row/column data.
          "{{Namespaces.Large}}"       long reports, cross-document summaries.
        Always return at least one namespace except for Meta.

        SEARCHQUERY
          Copy verbatim unless an obvious typo or acronym needs fixing.
          Never truncate. Never paraphrase. Never invent.
          NEVER include a document/guide/spec title in searchQuery.
          If the question begins with "According to X", "In the Y guide",
          "From the Z doc", drop that phrase entirely.
          The searchQuery MUST describe WHAT IS BEING ASKED, not which
          document to look in.

          WHEN TO DECOMPOSE (Step 3c):
          If the question contains "beyond", "additional", "extra",
          "specifically for", "only for", "unlike", "different from",
          OR asks for a subset of items that differ from a baseline,
          you MUST decompose searchQuery into 2-3 alternative phrasings
          joined by " | ". Each sub-query must be a complete standalone
          phrase (not a fragment). Example:
            "what additional verification for Production deployments |
             Production-specific deployment checks beyond the standard checklist |
             extra Production deployment requirements"

        FILTERTEXT
          The literal value being filtered on, exactly as written.
          Null when no specific value is being filtered.

        CONFIDENCE
          0.9+ obvious / 0.6-0.9 minor ambiguity / <0.6 ambiguous.

        RULES
          R1. JSON only. No markdown, no prose.
          R2. Exact key names, exact mode/namespace strings.
          R3. Exhaustive requires non-empty filterText.
          R4. Meta requires namespaces = [] and topK = null.
          R5. Semantic and Structural require numeric topK.
          R6. Never empty namespaces for non-Meta.
          R7. Never invent a filterText.
          R8. NEVER put a document title in searchQuery.
          R9. For Step 3c questions, searchQuery MUST contain " | ".

        SUB-QUESTION:
        {{subQuestion}}
        """;

    private sealed class OllamaResponse
    {
        public string Response { get; set; } = string.Empty;
    }

    private sealed class PlannerJson
    {
        public string? SearchQuery { get; set; }
        public int? TopK { get; set; }
        public string[]? Namespaces { get; set; }
        public RetrievalMode? Mode { get; set; }
        public string? FilterText { get; set; }
        public double? Confidence { get; set; }
    }
}