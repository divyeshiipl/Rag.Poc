namespace Rag.Poc.Services;

public class DocumentExtractor
{
    #region Public Methods

    /// <summary>
    /// To Extract the documents
    /// </summary>
    /// <param name="filePath"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    /// <exception cref="FileNotFoundException"></exception>
    /// <exception cref="NotSupportedException"></exception>
    public async Task<ExtractedDocument> ExtractAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException(
                "Document file was not found.",
                filePath);
        }

        var extension =
            Path.GetExtension(filePath)
                .ToLowerInvariant();

        return extension switch
        {
            ".txt" =>
                await ExtractTextFileAsync(
                    filePath,
                    cancellationToken),

            ".docx" =>
                await ExtractWordDocumentAsync(
                   filePath,
                   cancellationToken),

            ".xlsx" =>
                 await ExtractExcelAsync(
                    filePath,
                    cancellationToken),

            ".xls" =>
                 await ExtractExcelAsync(
                    filePath,
                    cancellationToken),

            _ =>
                throw new NotSupportedException(
                    $"File type '{extension}' is not supported yet.")
        };
    }

    /// <summary>
    /// To extract the excel/csv files
    /// </summary>
    /// <param name="filePath"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    /// <exception cref="FileNotFoundException"></exception>
    public async Task<ExtractedDocument> ExtractExcelAsync(
    string filePath,
    CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException(
                "Excel document was not found.",
                filePath);
        }

        var sections = await Task.Run(
            () => ExtractWorkbook(filePath, cancellationToken),
            cancellationToken);

        return new ExtractedDocument
        {
            Sections = sections,
            Text = string.Join(
                Environment.NewLine + Environment.NewLine,
                sections.Select(section =>
                    $"Worksheet: {section.Heading}" +
                    Environment.NewLine +
                    section.Content))
        };
    }

    #endregion

    #region Private Methods
    private static async Task<ExtractedDocument> ExtractTextFileAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        var text =
            await File.ReadAllTextAsync(
                filePath,
                cancellationToken);

        text = NormalizeText(text);

        if (string.IsNullOrWhiteSpace(text))
        {
            return new ExtractedDocument
            {
                Text = string.Empty,
                Sections = []
            };
        }

        var sections = ExtractSections(text);

        return new ExtractedDocument
        {
            Text = text,
            Sections = sections
        };
    }

    private static List<DocumentSection> ExtractSections(
        string text)
    {
        var lines =
            text.Split('\n');

        var sections =
            new List<DocumentSection>();

        DocumentSection? currentSection = null;

        foreach (var rawLine in lines)
        {
            var line =
                rawLine.Trim();

            if (string.IsNullOrWhiteSpace(line))
            {
                AppendBlankLine(
                    currentSection);

                continue;
            }

            if (IsTopLevelHeading(line))
            {
                if (currentSection != null)
                {
                    FinalizeSection(currentSection);

                    if (HasUsefulContent(currentSection))
                    {
                        sections.Add(currentSection);
                    }
                }

                currentSection =
                    new DocumentSection
                    {
                        Heading = line,
                        Content = string.Empty,
                        ContentType = "text/plain"
                    };

                continue;
            }

            if (currentSection == null)
            {
                currentSection =
                    new DocumentSection
                    {
                        Heading = null,
                        Content = string.Empty,
                        ContentType = "text/plain"
                    };
            }

            AppendLine(
                currentSection,
                line);
        }

        if (currentSection != null)
        {
            FinalizeSection(currentSection);

            if (HasUsefulContent(currentSection))
            {
                sections.Add(currentSection);
            }
        }

        return sections;
    }

    private static bool IsTopLevelHeading(
        string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return false;

        line = line.Trim();

        if (!Regex.IsMatch(
            line,
            @"^\d+[\.\)]\s+\S+"))
        {
            return false;
        }

        if (Regex.IsMatch(
            line,
            @"[.!?;:]$"))
        {
            return false;
        }

        return true;
    }

    private static void AppendLine(
        DocumentSection section,
        string line)
    {
        if (string.IsNullOrEmpty(section.Content))
        {
            section.Content = line;
            return;
        }

        if (!section.Content.EndsWith('\n'))
        {
            section.Content += "\n";
        }

        section.Content += line;
    }

    private static void AppendBlankLine(
        DocumentSection? section)
    {
        if (section == null)
            return;

        if (string.IsNullOrEmpty(section.Content))
            return;

        if (!section.Content.EndsWith("\n\n"))
        {
            section.Content += "\n";
        }
    }

    private static void FinalizeSection(
        DocumentSection section)
    {
        section.Content =
            NormalizeSectionContent(
                section.Content);

        if (!string.IsNullOrWhiteSpace(
                section.Heading))
        {
            section.Metadata["section"] =
                section.Heading;
        }
    }

    private static bool HasUsefulContent(
        DocumentSection section)
    {
        return
            !string.IsNullOrWhiteSpace(section.Heading) ||
            !string.IsNullOrWhiteSpace(section.Content);
    }

    private static string NormalizeSectionContent(
        string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return string.Empty;

        return Regex
            .Replace(
                content,
                @"[ \t]+",
                " ")
            .Trim();
    }

    private static string NormalizeText(
        string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        return text
            .Replace("\r\n", "\n")
            .Replace("\r", "\n")
            .Trim();
    }

    private static Task<ExtractedDocument> ExtractWordDocumentAsync(
    string filePath,
    CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var document =
            WordprocessingDocument.Open(
                filePath,
                false);

        var body =
            document.MainDocumentPart?
                .Document
                .Body;

        if (body == null)
        {
            return Task.FromResult(
                new ExtractedDocument
                {
                    Text = string.Empty,
                    Sections = []
                });
        }

        var sections = new List<DocumentSection>();

        DocumentSection? currentSection = null;

        foreach (var element in body.Elements())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (element is Paragraph paragraph)
            {
                ProcessParagraph(
                    paragraph,
                    ref currentSection,
                    sections);
            }
            else if (element is Table table)
            {
                ProcessTable(
                    table,
                    ref currentSection,
                    sections);
            }
        }

        if (currentSection != null)
        {
            FinalizeSection(currentSection);

            if (HasUsefulContent(currentSection))
                sections.Add(currentSection);
        }

        var text =
            string.Join(
                "\n\n",
                sections
                    .Select(BuildSectionText)
                    .Where(x => !string.IsNullOrWhiteSpace(x)));

        return Task.FromResult(
            new ExtractedDocument
            {
                Text = text,
                Sections = sections
            });
    }

    private static void ProcessParagraph(
    Paragraph paragraph,
    ref DocumentSection? currentSection,
    List<DocumentSection> sections)
    {
        var text =
            paragraph.InnerText?.Trim();

        if (string.IsNullOrWhiteSpace(text))
            return;

        var style =
            paragraph.ParagraphProperties?
                .ParagraphStyleId?
                .Val?
                .Value;

        if (IsHeadingStyle(style))
        {
            if (currentSection != null)
            {
                FinalizeSection(currentSection);

                if (HasUsefulContent(currentSection))
                    sections.Add(currentSection);
            }

            currentSection =
                new DocumentSection
                {
                    Heading = text,
                    Content = string.Empty,
                    ContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                    SourceLocation = $"Paragraph:{sections.Count + 1}"
                };

            currentSection.Metadata["style"] =
                style ?? string.Empty;

            return;
        }

        if (currentSection == null)
        {
            currentSection =
                new DocumentSection
                {
                    Heading = null,
                    Content = string.Empty,
                    ContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document"
                };
        }

        AppendContent(
            currentSection,
            text);
    }

    private static bool IsHeadingStyle(
    string? style)
    {
        if (string.IsNullOrWhiteSpace(style))
            return false;

        return
            style.Equals(
                "Heading1",
                StringComparison.OrdinalIgnoreCase)
            ||
            style.Equals(
                "Heading2",
                StringComparison.OrdinalIgnoreCase)
            ||
            style.Equals(
                "Heading3",
                StringComparison.OrdinalIgnoreCase)
            ||
            style.Equals(
                "Heading4",
                StringComparison.OrdinalIgnoreCase)
            ||
            style.Equals(
                "Heading5",
                StringComparison.OrdinalIgnoreCase)
            ||
            style.Equals(
                "Heading6",
                StringComparison.OrdinalIgnoreCase);
    }

    private static void ProcessTable(
    Table table,
    ref DocumentSection? currentSection,
    List<DocumentSection> sections)
    {
        if (currentSection == null)
        {
            currentSection =
                new DocumentSection
                {
                    Heading = null,
                    Content = string.Empty,
                    ContentType =
                        "application/vnd.openxmlformats-officedocument.wordprocessingml.document"
                };
        }

        var rows = table.Elements<TableRow>();

        foreach (var row in rows)
        {
            var cells =
                row.Elements<TableCell>()
                    .Select(cell =>
                        NormalizeWhitespace(
                            cell.InnerText))
                    .Where(x =>
                        !string.IsNullOrWhiteSpace(x))
                    .ToList();

            if (cells.Count == 0)
                continue;

            AppendContent(
                currentSection,
                string.Join(
                    " | ",
                    cells));
        }
    }

    private static void AppendContent(
    DocumentSection section,
    string content)
    {
        content =
            NormalizeWhitespace(content);

        if (string.IsNullOrWhiteSpace(content))
            return;

        if (string.IsNullOrWhiteSpace(section.Content))
        {
            section.Content = content;
            return;
        }

        section.Content +=
            "\n" + content;
    }

    private static string NormalizeWhitespace(
    string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        return Regex
            .Replace(
                text,
                @"\s+",
                " ")
            .Trim();
    }

    private static string BuildSectionText(
    DocumentSection section)
    {
        if (string.IsNullOrWhiteSpace(section.Heading))
            return section.Content.Trim();

        if (string.IsNullOrWhiteSpace(section.Content))
            return section.Heading.Trim();

        return
            $"{section.Heading.Trim()}\n\n" +
            section.Content.Trim();
    }

    private static List<DocumentSection> ExtractWorkbook(
        string filePath,
        CancellationToken cancellationToken)
    {
        var sections = new List<DocumentSection>();

        using var stream = File.Open(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite);

        using var reader = ExcelReaderFactory.CreateReader(stream);

        do
        {
            cancellationToken.ThrowIfCancellationRequested();

            var sheetName = reader.Name;
            var rows = new List<(int Number, List<string> Values)>();
            var rowNumber = 0;

            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                rowNumber++;

                var values = new List<string>();

                for (var column = 0; column < reader.FieldCount; column++)
                {
                    var value = reader.GetValue(column);

                    values.Add(FormatCellValue(value));
                }

                rows.Add((rowNumber, values));
            }

            if (rows.Count == 0)
                continue;

            var tables = SplitIntoTables(rows);

            for (var t = 0; t < tables.Count; t++)
            {
                var tableRows = tables[t];
                if (tableRows.Count == 0) continue;

                var tableSheetName = tables.Count > 1
                    ? $"{sheetName} (Table {t + 1})"
                    : sheetName;

                var headerRow = tableRows[0];
                var headers = BuildHeaders(headerRow.Values);
                var headerLine = $"Columns: {string.Join(" | ", headers)}";

                sections.Add(new DocumentSection
                {
                    Heading = $"{tableSheetName} — schema",
                    Content = $"Worksheet: {tableSheetName}\n{headerLine}",
                    SourceLocation = $"Worksheet: {tableSheetName}",
                    ContentType = "ExcelWorksheet",
                    Metadata = new Dictionary<string, string>
                    {
                        ["WorksheetName"] = tableSheetName,
                        ["HeaderRow"] = headerRow.Number.ToString(CultureInfo.InvariantCulture),
                        ["ColumnCount"] = headers.Count.ToString(CultureInfo.InvariantCulture),
                        ["RecordType"] = "ExcelWorksheet"
                    }
                });

                var dataRows = tableRows.Skip(1).ToList();
                if (dataRows.Count == 0) continue;

                sections.Add(BuildAggregateSection(tableSheetName, headers, dataRows));

                foreach (var colSection in BuildColumnIndexSections(tableSheetName, headers, dataRows))
                    sections.Add(colSection);

                const int ExtractorRowsPerBlock = 5;
                for (var blockStart = 0; blockStart < dataRows.Count; blockStart += ExtractorRowsPerBlock)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var blockRows = dataRows
                        .Skip(blockStart)
                        .Take(ExtractorRowsPerBlock)
                        .ToList();

                    if (blockRows.Count == 0) continue;

                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine($"Worksheet: {tableSheetName}");
                    sb.AppendLine(headerLine);

                    foreach (var (excelRowNumber, values) in blockRows)
                    {
                        sb.AppendLine(FormatRowLine(excelRowNumber, headers, values));
                    }

                    var firstRow = blockRows[0].Number;
                    var lastRow = blockRows[^1].Number;

                    sections.Add(new DocumentSection
                    {
                        Heading = $"{tableSheetName} — rows {firstRow}-{lastRow}",
                        Content = sb.ToString().TrimEnd(),
                        SourceLocation = $"Worksheet: {tableSheetName}, Rows: {firstRow}-{lastRow}",
                        ContentType = "ExcelRows",
                        Metadata = new Dictionary<string, string>
                        {
                            ["WorksheetName"] = tableSheetName,
                            ["FirstRow"] = firstRow.ToString(CultureInfo.InvariantCulture),
                            ["LastRow"] = lastRow.ToString(CultureInfo.InvariantCulture),
                            ["RowCount"] = blockRows.Count.ToString(CultureInfo.InvariantCulture),
                            ["RecordType"] = "ExcelRows"
                        }
                    });
                }
            }
        }
        while (reader.NextResult());

        return sections;
    }

    private static List<string> BuildHeaders(
        List<string> headerValues)
    {
        var headers = new List<string>();
        var usedNames = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < headerValues.Count; i++)
        {
            var header = string.IsNullOrWhiteSpace(headerValues[i])
                ? $"Column {i + 1}"
                : headerValues[i];

            var uniqueHeader = header;
            var suffix = 2;

            while (!usedNames.Add(uniqueHeader))
            {
                uniqueHeader = $"{header} ({suffix})";
                suffix++;
            }

            headers.Add(uniqueHeader);
        }

        return headers;
    }

    private static string FormatCellValue(object? value)
    {
        if (value is null || value is DBNull)
            return string.Empty;

        if (value is DateTime dt)
            return dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        if (value is double d)
        {
            if (Math.Abs(d) < 1e-10) return "0";
            return d.ToString("0.##########", CultureInfo.InvariantCulture);
        }

        if (value is float f)
            return f.ToString("0.##########", CultureInfo.InvariantCulture);

        if (value is decimal m)
            return m.ToString("0.##########", CultureInfo.InvariantCulture);

        return Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
    }

    private static List<List<(int Number, List<string> Values)>> SplitIntoTables(
    List<(int Number, List<string> Values)> rows)
    {
        var tables = new List<List<(int Number, List<string> Values)>>();
        var current = new List<(int Number, List<string> Values)>();

        foreach (var row in rows)
        {
            var isBlank = row.Values.All(string.IsNullOrWhiteSpace);

            if (isBlank && current.Count > 0)
            {
                tables.Add(current);
                current = new List<(int Number, List<string> Values)>();
            }
            else if (!isBlank)
            {
                current.Add(row);
            }
        }

        if (current.Count > 0)
            tables.Add(current);

        if (tables.Count == 0 && rows.Count > 0)
            tables.Add(rows);

        return tables;
    }

    private static string FormatRowLine(
    int rowNum,
    List<string> headers,
    List<string> values)
    {
        var fields = new List<string>();

        for (var c = 0; c < values.Count; c++)
        {
            var header = c < headers.Count ? headers[c] : $"Column {c + 1}";
            var display = string.IsNullOrWhiteSpace(values[c]) ? "(empty)" : values[c];
            fields.Add($"{header}: {display}");
        }

        return $"Row {rowNum}: {string.Join(" | ", fields)}";
    }

    private static DocumentSection BuildAggregateSection(
    string sheetName,
    List<string> headers,
    List<(int Number, List<string> Values)> dataRows)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Worksheet: {sheetName}");
        sb.AppendLine($"Aggregates: {dataRows.Count} data rows");
        sb.AppendLine("Columns: " + string.Join(" | ", headers));
        sb.AppendLine();

        for (var c = 0; c < headers.Count; c++)
        {
            var nums = new List<decimal>();
            var dates = new List<DateTime>();
            var texts = new List<string>();

            foreach (var (_, values) in dataRows)
            {
                if (c >= values.Count) continue;
                var v = values[c];
                if (string.IsNullOrWhiteSpace(v)) continue;

                var cleaned = v.Replace(",", "")
                               .Replace("$", "")
                               .Replace("€", "")
                               .Replace("£", "")
                               .Trim();

                if (decimal.TryParse(cleaned, NumberStyles.Any,
                        CultureInfo.InvariantCulture, out var d))
                    nums.Add(d);
                else if (DateTime.TryParse(v, CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var dt))
                    dates.Add(dt);
                else
                    texts.Add(v);
            }

            sb.Append($"{headers[c]}: Count={nums.Count + dates.Count + texts.Count}");

            if (nums.Count > 0)
            {
                sb.Append($", Sum={nums.Sum().ToString("0.####", CultureInfo.InvariantCulture)}");
                sb.Append($", Avg={(nums.Sum() / nums.Count).ToString("0.####", CultureInfo.InvariantCulture)}");
                sb.Append($", Min={nums.Min().ToString("0.####", CultureInfo.InvariantCulture)}");
                sb.Append($", Max={nums.Max().ToString("0.####", CultureInfo.InvariantCulture)}");
                sb.AppendLine();

                var sorted = dataRows
                    .Select(r => (
                        Row: r.Number,
                        Clean: c < r.Values.Count
                            ? r.Values[c].Replace(",", "").Replace("$", "").Replace("€", "").Replace("£", "").Trim()
                            : ""))
                    .Where(r => decimal.TryParse(r.Clean, NumberStyles.Any,
                        CultureInfo.InvariantCulture, out _))
                    .Select(r => (r.Row, Value: decimal.Parse(r.Clean,
                        NumberStyles.Any, CultureInfo.InvariantCulture)))
                    .ToList();

                sb.Append($"  {headers[c]} > TOP 10: ");
                sb.AppendLine(string.Join(", ",
                    sorted.OrderByDescending(x => x.Value).Take(10)
                          .Select(x => $"Row {x.Row}={x.Value.ToString("0.####", CultureInfo.InvariantCulture)}")));

                sb.Append($"  {headers[c]} > BOTTOM 10: ");
                sb.AppendLine(string.Join(", ",
                    sorted.OrderBy(x => x.Value).Take(10)
                          .Select(x => $"Row {x.Row}={x.Value.ToString("0.####", CultureInfo.InvariantCulture)}")));
            }
            else if (dates.Count > 0)
            {
                sb.Append($", Min={dates.Min():yyyy-MM-dd}");
                sb.Append($", Max={dates.Max():yyyy-MM-dd}");
                sb.AppendLine();

                var dateRows = dataRows
                    .Select(r => (Row: r.Number, Raw: c < r.Values.Count ? r.Values[c] : ""))
                    .Where(r => DateTime.TryParse(r.Raw, CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out _))
                    .Select(r => (r.Row, Date: DateTime.Parse(r.Raw, CultureInfo.InvariantCulture)))
                    .ToList();

                sb.Append($"  {headers[c]} > MOST RECENT 5: ");
                sb.AppendLine(string.Join(", ",
                    dateRows.OrderByDescending(x => x.Date).Take(5)
                            .Select(x => $"Row {x.Row}={x.Date:yyyy-MM-dd}")));

                sb.Append($"  {headers[c]} > OLDEST 5: ");
                sb.AppendLine(string.Join(", ",
                    dateRows.OrderBy(x => x.Date).Take(5)
                            .Select(x => $"Row {x.Row}={x.Date:yyyy-MM-dd}")));
            }
            else if (texts.Count > 0)
            {
                var distinct = texts.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                sb.Append($", Distinct={distinct.Count}");
                if (distinct.Count <= 20)
                    sb.Append($", Values=[{string.Join(", ", distinct)}]");
                sb.AppendLine();
            }
            else
            {
                sb.AppendLine();
            }
        }

        sb.AppendLine();

        var categoricalColumns = new List<int>();
        for (var c = 0; c < headers.Count; c++)
        {
            var distinctValues = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (_, values) in dataRows)
            {
                if (c < values.Count && !string.IsNullOrWhiteSpace(values[c]))
                    distinctValues.Add(values[c].Trim());
            }
            if (distinctValues.Count >= 2 && distinctValues.Count <= 20)
                categoricalColumns.Add(c);
        }

        foreach (var catCol in categoricalColumns)
        {
            sb.AppendLine($"--- Breakdown by [{headers[catCol]}] ---");

            var groups = dataRows
                .Where(r => catCol < r.Values.Count && !string.IsNullOrWhiteSpace(r.Values[catCol]))
                .GroupBy(r => r.Values[catCol].Trim(), StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key);

            foreach (var group in groups)
            {
                sb.AppendLine($"[{headers[catCol]}={group.Key}] Count={group.Count()}");

                for (var c = 0; c < headers.Count; c++)
                {
                    if (c == catCol) continue;

                    var nums = new List<decimal>();
                    foreach (var row in group)
                    {
                        if (c >= row.Values.Count) continue;
                        var v = row.Values[c];
                        if (string.IsNullOrWhiteSpace(v)) continue;
                        var cleaned = v.Replace(",", "").Replace("$", "")
                                       .Replace("€", "").Replace("£", "").Trim();
                        if (decimal.TryParse(cleaned, NumberStyles.Any,
                            CultureInfo.InvariantCulture, out var d))
                            nums.Add(d);
                    }

                    if (nums.Count > 0)
                    {
                        sb.AppendLine($"  {headers[c]}: Count={nums.Count}, " +
                            $"Sum={nums.Sum().ToString("0.####", CultureInfo.InvariantCulture)}, " +
                            $"Avg={(nums.Sum() / nums.Count).ToString("0.####", CultureInfo.InvariantCulture)}, " +
                            $"Min={nums.Min().ToString("0.####", CultureInfo.InvariantCulture)}, " +
                            $"Max={nums.Max().ToString("0.####", CultureInfo.InvariantCulture)}");
                    }
                }
            }

            sb.AppendLine();
        }

        return new DocumentSection
        {
            Heading = $"{sheetName} — aggregates",
            Content = sb.ToString().TrimEnd(),
            SourceLocation = $"Worksheet: {sheetName}, Aggregates",
            ContentType = "ExcelAggregates",
            Metadata = new Dictionary<string, string>
            {
                ["WorksheetName"] = sheetName,
                ["RecordType"] = "ExcelAggregates",
                ["RowCount"] = dataRows.Count.ToString(CultureInfo.InvariantCulture)
            }
        };
    }

    private static IEnumerable<DocumentSection> BuildColumnIndexSections(
        string sheetName,
        List<string> headers,
        List<(int Number, List<string> Values)> dataRows)
    {
        for (var c = 0; c < headers.Count; c++)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Worksheet: {sheetName}");
            sb.AppendLine($"Column: {headers[c]}");

            var count = 0;
            foreach (var (rowNum, values) in dataRows)
            {
                if (c >= values.Count) continue;
                var v = values[c];
                if (string.IsNullOrWhiteSpace(v)) continue;

                sb.AppendLine($"Row {rowNum}: {v}");
                count++;

                if (count >= 200)
                {
                    sb.AppendLine("... (truncated)");
                    break;
                }
            }

            yield return new DocumentSection
            {
                Heading = $"{sheetName} — column {headers[c]}",
                Content = sb.ToString().TrimEnd(),
                SourceLocation = $"Worksheet: {sheetName}, Column: {headers[c]}",
                ContentType = "ExcelColumn",
                Metadata = new Dictionary<string, string>
                {
                    ["WorksheetName"] = sheetName,
                    ["ColumnName"] = headers[c],
                    ["RecordType"] = "ExcelColumn",
                    ["ValueCount"] = count.ToString(CultureInfo.InvariantCulture)
                }
            };
        }
    }

    #endregion
}