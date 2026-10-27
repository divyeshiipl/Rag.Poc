namespace Rag.Poc.Models;

/// <summary>
/// To query filter
/// </summary>
/// <param name="Column"></param>
/// <param name="Operator"></param>
/// <param name="Value"></param>
/// <param name="UpperBound"></param>
public sealed record QueryFilter(string Column, string Operator, string Value, string? UpperBound = null);

/// <summary>
/// To retrive plan
/// </summary>
/// <param name="SearchQuery"></param>
/// <param name="TopK"></param>
/// <param name="Namespaces"></param>
/// <param name="Mode"></param>
/// <param name="FilterText"></param>
/// <param name="MinScore"></param>
/// <param name="Rewritten"></param>
/// <param name="Filters"></param>
public sealed record RetrievalPlan(string SearchQuery, int? TopK, List<string> Namespaces, RetrievalMode Mode, string? FilterText = null,double? MinScore = null, bool Rewritten = false, IReadOnlyList<QueryFilter>? Filters = null);

public sealed class OllamaRetrievalPlanner : IRetrievalPlanner
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<OllamaRetrievalPlanner> _logger;

    public OllamaRetrievalPlanner(HttpClient httpClient, ILogger<OllamaRetrievalPlanner> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    #region Public Methods
    /// <summary>
    /// To Create plan to retrive the chunks
    /// </summary>
    /// <param name="question"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentException"></exception>
    public async Task<RetrievalPlan> CreatePlanAsync(string question, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(question))
            throw new ArgumentException("Question cannot be empty.", nameof(question));

        var cleaned = StripDocumentReference(question.Trim());

        if (IsTrivial(cleaned))
            return MetaPlan(cleaned);

        var subQuestions = SplitSubQuestions(cleaned);

        if (subQuestions.Count == 1)
            return await PlanOneAsync(subQuestions[0], cancellationToken);

        _logger.LogInformation("Multi-part question: {Count} sub-questions detected.", subQuestions.Count);

        var subPlans = new List<RetrievalPlan>(subQuestions.Count);
        foreach (var sub in subQuestions)
            subPlans.Add(await PlanOneAsync(sub, cancellationToken));

        return MergeSubPlans(cleaned, subQuestions, subPlans);
    }

    #endregion

    #region Private Methods

    /// <summary>
    /// To strip document references
    /// </summary>
    /// <param name="question"></param>
    /// <returns></returns>
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
            var m = Regex.Match(question, p, RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (m.Success)
            {
                var q = m.Groups["q"].Value.Trim();
                if (q.Length >= 8)
                    return q;
            }
        }

        return question;
    }

    /// <summary>
    /// To split the sub questions
    /// </summary>
    /// <param name="input"></param>
    /// <returns></returns>
    private static List<string> SplitSubQuestions(string input)
    {
        var qmParts = input
            .Split('?', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToList();

        if (qmParts.Count >= 2 && qmParts.Any(p => p.Length >= 8))
            return qmParts.Take(RagConstants.MaxSubQuestions).ToList();

        var markerPositions = FindListMarkerPositions(input);
        if (markerPositions.Count >= 2)
        {
            var parts = new List<string>();
            for (int i = 0; i < markerPositions.Count; i++)
            {
                var start = markerPositions[i];
                var end = i + 1 < markerPositions.Count ? markerPositions[i + 1] : input.Length;

                var piece = input[start..end].Trim().TrimEnd('.', '?', '!', ':', ';');
                if (piece.Length > 0) parts.Add(piece);
            }
            if (parts.Count >= 2)
                return parts.Take(RagConstants.MaxSubQuestions).ToList();
        }

        foreach (var connector in new[] { " and ", " or " })
        {
            var idx = input.IndexOf(connector, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;

            var secondIdx = input.IndexOf(connector, idx + connector.Length, StringComparison.OrdinalIgnoreCase);
            if (secondIdx >= 0) continue;

            var left = input[..idx].Trim();
            var right = input[(idx + connector.Length)..].Trim();

            if (left.Length >= 8 && right.Length >= 8 && left.Any(char.IsLetter) && right.Any(char.IsLetter))
            {
                return new List<string> { TrimTrailing(left), TrimTrailing(right) };
            }
        }

        return new List<string> { input };
    }

    /// <summary>
    /// To find list maker positions
    /// </summary>
    /// <param name="input"></param>
    /// <returns></returns>
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

    /// <summary>
    /// To check marker punctuation
    /// </summary>
    /// <param name="c"></param>
    /// <returns></returns>
    private static bool IsMarkerPunctuation(char c) =>  c == '.' || c == ')' || c == ']' || c == ':' || c == '•' || c == '*' || c == '-';

    /// <summary>
    /// To check bare bullet
    /// </summary>
    /// <param name="c"></param>
    /// <returns></returns>
    private static bool IsBareBullet(char c) => c == '•' || c == '*' || c == '-';

    /// <summary>
    /// To trim trailing
    /// </summary>
    /// <param name="s"></param>
    /// <returns></returns>
    private static string TrimTrailing(string s) => s.TrimEnd('.', '?', '!', ':', ';', ' ', '\t', '\n', '\r');

    /// <summary>
    /// 
    /// </summary>
    /// <param name="subQuestion"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    private async Task<RetrievalPlan> PlanOneAsync(string subQuestion, CancellationToken cancellationToken)
    {
        var prompt = BuildPrompt(subQuestion) + "\n/no_think";

        var request = new
        {
            model = RagConstants.RetriveModel,
            prompt,
            stream = false,
            format = "json",
            keep_alive = "10m",
            think = false,
            options = new
            {
                temperature = 0,
                num_predict = 400,
                top_p = 0.1
            }
        };

        using var response = await _httpClient.PostAsJsonAsync("/api/generate", request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var ollama = await response.Content.ReadFromJsonAsync<OllamaResponse>(cancellationToken: cancellationToken);

        if (string.IsNullOrWhiteSpace(ollama?.Response))
            return Fallback(subQuestion);

        PlannerJson? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<PlannerJson>(ollama.Response,
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

    /// <summary>
    /// To create plan
    /// </summary>
    /// <param name="parsed"></param>
    /// <param name="original"></param>
    /// <returns></returns>
    private RetrievalPlan ToPlan(PlannerJson parsed, string original)
    {
        var mode = parsed.Mode ?? RetrievalMode.Semantic;
        var confidence = parsed.Confidence ?? 0.5;

        if (mode == RetrievalMode.Meta)
            return MetaPlan(original);

        if (mode == RetrievalMode.Aggregate)
        {
            var aggQuery = string.IsNullOrWhiteSpace(parsed.SearchQuery) ? original : parsed.SearchQuery.Trim();

            return new RetrievalPlan(
                SearchQuery: aggQuery,
                TopK: Math.Clamp(parsed.TopK ?? 8, 3, 20),
                Namespaces: new List<string>
                {
                    Namespaces.Structural,
                    Namespaces.Default,
                    Namespaces.Large
                },
                Mode: RetrievalMode.Aggregate,
                FilterText: parsed.FilterText,
                MinScore: 0,
                Rewritten: false,
                Filters: ConvertFilters(parsed.Filters));
        }

        var namespaces = (parsed.Namespaces ?? Array.Empty<string>())
            .Where(n => !string.IsNullOrWhiteSpace(n) && RagConstants.AllowedNamespaces.Contains(n))
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

        var filterText = string.IsNullOrWhiteSpace(parsed.FilterText) ? null : parsed.FilterText.Trim();
        var hasStructuredFilter = parsed.Filters is { Count: > 0 };

        if (mode == RetrievalMode.Exhaustive && filterText is null && !hasStructuredFilter)
        {
            _logger.LogWarning("Exhaustive without filterText or structured filter; downgrading to Semantic.");
            mode = RetrievalMode.Semantic;
        }

        int? topK = mode == RetrievalMode.Exhaustive ? null : Math.Clamp(parsed.TopK is > 0 ? parsed.TopK.Value : RagConstants.DefaultTopK, 3, RagConstants.MaxTopK);

        var searchQuery = string.IsNullOrWhiteSpace(parsed.SearchQuery) ? original : parsed.SearchQuery.Trim();

        if (searchQuery.Length < 15 && original.Length > searchQuery.Length * 2)
            searchQuery = original;

        return new RetrievalPlan(
            SearchQuery: searchQuery,
            TopK: topK,
            Namespaces: namespaces,
            Mode: mode,
            FilterText: filterText,
            MinScore: mode == RetrievalMode.Exhaustive ? 0 : 0.10,
            Rewritten: !string.Equals(searchQuery, original, StringComparison.Ordinal),
            Filters: ConvertFilters(parsed.Filters));
    }

    /// <summary>
    /// To merge sub plans
    /// </summary>
    /// <param name="original"></param>
    /// <param name="subQuestions"></param>
    /// <param name="subPlans"></param>
    /// <returns></returns>
    private static RetrievalPlan MergeSubPlans(string original, List<string> subQuestions, List<RetrievalPlan> subPlans)
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

        var topK = Math.Clamp(nonMeta.Where(p => p.TopK.HasValue)
                .Select(p => p.TopK!.Value)
                .DefaultIfEmpty(RagConstants.DefaultTopK)
                .Max(), 3, RagConstants.MaxTopK);

        var filters = nonMeta
            .Select(p => p.FilterText)
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        string? filterText = filters.Count == 1 ? filters[0] : null;
        if (mode == RetrievalMode.Exhaustive && filterText is null)
            mode = RetrievalMode.Semantic;

        var combinedQuery = string.Join(" | ", subQuestions);

        var mergedFilters = nonMeta
            .SelectMany(p => p.Filters ?? Enumerable.Empty<QueryFilter>())
            .ToList();

        return new RetrievalPlan(
            SearchQuery: combinedQuery,
            TopK: topK,
            Namespaces: namespaces,
            Mode: mode,
            FilterText: filterText,
            MinScore: mode == RetrievalMode.Exhaustive ? 0 : 0.10,
            Rewritten: !string.Equals(combinedQuery, original, StringComparison.Ordinal),
            Filters: mergedFilters.Count == 0 ? null : mergedFilters);
    }

    /// <summary>
    /// To create the priority for mode
    /// </summary>
    /// <param name="m"></param>
    /// <returns></returns>
    private static int ModePriority(RetrievalMode m) => m switch
    {
        RetrievalMode.Exhaustive => 4,
        RetrievalMode.Structural => 3,
        RetrievalMode.Semantic => 2,
        RetrievalMode.Meta => 1,
        _ => 0
    };

    /// <summary>
    /// To check trivial
    /// </summary>
    /// <param name="s"></param>
    /// <returns></returns>
    private static bool IsTrivial(string s)
    {
        var words = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length <= 1 && !s.Contains('?') && !s.Any(char.IsDigit);
    }

    /// <summary>
    /// To create meta plan
    /// </summary>
    /// <param name="q"></param>
    /// <returns></returns>
    private static RetrievalPlan MetaPlan(string q) => new(
        SearchQuery: q,
        TopK: null,
        Namespaces: new List<string>(),
        Mode: RetrievalMode.Meta,
        FilterText: null,
        MinScore: null,
        Rewritten: false);

    /// <summary>
    /// To create fall back plan
    /// </summary>
    /// <param name="question"></param>
    /// <returns></returns>
    private static RetrievalPlan Fallback(string question) => new(
        SearchQuery: question,
        TopK: RagConstants.DefaultTopK,
        Namespaces: new List<string> { Namespaces.Default, Namespaces.Large },
        Mode: RetrievalMode.Semantic,
        MinScore: 0.10,
        Rewritten: false);

    /// <summary>
    /// To build prompt
    /// </summary>
    /// <param name="subQuestion"></param>
    /// <returns></returns>
    private static string BuildPrompt(string subQuestion) => $$"""
        You are the retrieval planner for an enterprise document search system.
        Given ONE user question, produce ONE JSON object describing how to search.
        Never answer the question. Never add prose.

        OUTPUT SCHEMA:
        {
          "searchQuery": string,
          "topK": number | null,
          "namespaces": string[],
          "mode": "Semantic" | "Structural" | "Exhaustive" | "Aggregate" | "FullTable" | "Meta",
          "filterText": string | null,
          "filters": [
            {
              "column": string,
              "operator": "=" | "!=" | ">" | "<" | ">=" | "<=" | "between" | "contains",
              "value": string,
              "upperBound": string | null
            }
          ] | null,
          "confidence": number
        }

        ============================================================
        DECISION PROCEDURE — apply in order, stop at first match.
        ============================================================

        Step 1 — GREETING / CHIT-CHAT / META
                 ("hi", "thanks", "who are you", "what can you do")
                 -> mode = "Meta", namespaces = [], topK = null, filters = null.

        Step 2 — SPECIFIC IDENTIFIER
                 A literal token that must be matched verbatim: an ID like
                 "AST-1000", "MNT-2001", "user_id=42"; an email, a URL, a
                 file path, a ticket number, a serial number, an order ID.
                 -> mode = "Exhaustive",
                    filterText = that exact token,
                    namespaces = ["{{Namespaces.Structural}}","{{Namespaces.Default}}"],
                    topK = null,
                    filters = null.

        Step 3 — EXPLICIT ROW FILTER
                 A question that is CLEARLY about rows in a data table.
                 Evidence that this step applies:
                   - The question says "list all", "which", "how many",
                     "show me rows", "filter", "find all", "give me all"
                   - AND it names a value that must match an exact column
                     (a category, status, department, location, date range,
                     numeric range, employee, project)
                   - AND the column name is a real column that would exist
                     in a spreadsheet (e.g. "Category", "Status", "Department",
                     "Location", "Purchase Date", "Cost ($)", "Assigned To")

                 Examples that ARE row filters:
                   "list all assets in the IT Hardware category"
                   "which assets are Under Maintenance"
                   "how many employees are in Finance"
                   "orders over $5000 in March"
                   "active assets in the HR department"

                 Examples that are NOT row filters (do not emit filters):
                   "does a deployment to QA require formal approval"
                   "what is the process for emergency changes"
                   "what does the SOP say about issuance"
                   "who approves a production change"
                   "what are the four environments"
                   "is business approval required for UAT"
                   "how do we roll back a deployment"

                 -> mode = "Exhaustive",
                    topK = null,
                    namespaces = ["{{Namespaces.Structural}}","{{Namespaces.Default}}","{{Namespaces.Large}}"],
                    filterText = the FIRST filter's value,
                    filters = the full list of filter conditions.

        Step 3b — PROCEDURE / SEQUENCE / WORKFLOW / PROCESS
                  ("what are the steps", "how do we deploy", "walk me through",
                   "what is the procedure for", "what is the sequence",
                   "what happens during", "how does X work")
                  -> mode = "Semantic", topK = 10,
                     namespaces = ["{{Namespaces.Default}}"],
                     filters = null.

        Step 3c — NARROW SUBSET / EXCEPTION / "beyond X" / "additional X" /
                  "unlike X" / "specifically for X"
                  These are hard to retrieve with a single embedding — decompose.
                  -> mode = "Semantic", topK = 12,
                     namespaces = ["{{Namespaces.Default}}"],
                     filters = null,
                     searchQuery MUST contain " | " and 2-3 distinct sub-queries.

        Step 3d — AGGREGATION / RANKING / COUNTING
                  ("how many", "total", "sum", "average", "minimum", "maximum",
                   "top N", "bottom N", "highest", "lowest", "rank", "most",
                   "least", "oldest", "newest")
                  -> mode = "Aggregate", topK = 8,
                     namespaces = ["{{Namespaces.Structural}}","{{Namespaces.Default}}","{{Namespaces.Large}}"],
                     filters = null.

        Step 3e — SHAPE / SCHEMA / METADATA
                  ("what columns", "what fields", "what tables", "what data types",
                   "primary key", "row count", "sheet layout", "what is the schema")
                  -> mode = "Structural", topK = 10,
                     namespaces = ["{{Namespaces.Structural}}","{{Namespaces.Default}}"],
                     filters = null.

        Step 4 — NUMERIC MEASURE / COMPARISON / TREND
                  ("cost", "price", "amount", "revenue", "rate", "ratio",
                   "compare X and Y", "difference between", "trend")
                  -> mode = "Semantic", topK = 10,
                     namespaces = ["{{Namespaces.Structural}}","{{Namespaces.Default}}"],
                     filters = null.

        Step 5 — EVERYTHING ELSE — PROSE / POLICY / DEFINITION / "DOES X REQUIRE Y"
                  Any question about what a document says, what a policy states,
                  whether something is required, what the rules are, how the
                  system behaves, what is documented.
                  -> mode = "Semantic", topK = 15,
                     namespaces = ["{{Namespaces.Default}}","{{Namespaces.Large}}"],
                     filters = null.

        ============================================================
        CRITICAL RULES FOR FILTERS
        ============================================================
        A filter is ONLY valid when ALL of these are true:
          (a) The question asks for rows or records ("list", "which", "how many",
              "show me", "find", "give me all")
          (b) The question names a value that must be matched against a specific
              column that exists in a data table
          (c) The column name comes from a real schema — you must not invent
              a column name from words in the question

        DO NOT emit filters when:
          - The question asks "does X require Y?" or "is X true?" — PROSE
          - The question asks "what is the policy / procedure / rule for X" — PROSE
          - The question asks "what does the document say about X" — PROSE
          - The question asks "how do we do X" — PROCEDURE
          - The question asks "who approves X" — PROSE
          - The question asks "what are the N things" — PROSE
          - You would have to invent a column name to make the filter work —
            if the column is not already implied by the question, do not filter

        WHEN IN DOUBT: choose mode = "Semantic" with filters = null.
        A wrong filter returns zero rows and the user gets no answer.
        A Semantic search returns relevant prose and the user gets an answer.
        Prefer Semantic over Exhaustive whenever the question is ambiguous.

        ============================================================
        NAMESPACES
        ============================================================
          "{{Namespaces.Default}}"        prose, .txt and .docx documents, policies,
                                        procedures, narratives, FAQs, guides
          "{{Namespaces.Structural}}"  spreadsheets, rows, columns, schemas,
                                        any tabular data from .xlsx/.xls/.csv
          "{{Namespaces.Large}}"       long reports, cross-document summaries

        Every namespace must contain at least one value except for Meta.

        ============================================================
        SEARCHQUERY
        ============================================================
        - Copy the question verbatim unless an obvious typo or acronym needs fixing.
        - Never truncate. Never paraphrase. Never invent content.
        - NEVER include a document / guide / spec / workbook title in searchQuery.
        - If the question begins with "According to X", "In the Y guide",
          "From the Z doc", drop that phrase — the searchQuery must describe
          WHAT is being asked, not which document to look in.

        WHEN TO DECOMPOSE (Step 3c):
        If the question contains "beyond", "additional", "extra",
        "specifically for", "only for", "unlike", "different from",
        decompose searchQuery into 2-3 alternative phrasings joined by " | ".
        Each sub-query must be a complete standalone phrase.

        ============================================================
        FILTERTEXT
        ============================================================
        The first filter's value, verbatim. Null when no filter is emitted.
        Do not invent a filterText. Do not copy the whole question.

        ============================================================
        CONFIDENCE
        ============================================================
        0.9+ — obvious / single clear intent
        0.6–0.9 — minor ambiguity
        <0.6 — genuinely ambiguous

        ============================================================
        RULES
        ============================================================
        R1. Output JSON only. No markdown, no prose, no explanation.
        R2. Exact key names. Exact enum strings for mode and namespace.
        R3. Exhaustive requires EITHER a non-empty filterText
            OR a non-empty filters list.
        R4. Meta requires namespaces = [] and topK = null.
        R5. Semantic and Structural require numeric topK.
        R6. Never empty namespaces for non-Meta modes.
        R7. Never invent a filterText.
        R8. NEVER put a document title in searchQuery.
        R9. For Step 3c questions, searchQuery MUST contain " | ".
        R10. Filters only when the question is explicitly about rows in a table.
             When in doubt, prefer Semantic with filters = null.

        SUB-QUESTION:
        {{subQuestion}}
        """;

    /// <summary>
    /// To convert filters
    /// </summary>
    /// <param name="raw"></param>
    /// <returns></returns>
    private static List<QueryFilter>? ConvertFilters(List<FilterJson>? raw)
    {
        if (raw is null) return null;

        var result = new List<QueryFilter>();
        foreach (var f in raw)
        {
            if (string.IsNullOrWhiteSpace(f.Column) || string.IsNullOrWhiteSpace(f.Operator) || string.IsNullOrWhiteSpace(f.Value))
                continue;

            result.Add(new QueryFilter(
                Column: f.Column.Trim(),
                Operator: f.Operator.Trim(),
                Value: f.Value.Trim(),
                UpperBound: string.IsNullOrWhiteSpace(f.UpperBound) ? null : f.UpperBound.Trim()));
        }

        return result.Count == 0 ? null : result;
    }

    #endregion
}