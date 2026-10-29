namespace Rag.Poc.Services;

/// <summary>
/// Provides functionality for extracting content from various document formats.
/// </summary>
public class DocumentExtractor
{
    /// <summary>
    /// Extracts content from a document file.
    /// </summary>
    /// <param name="filePath"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    /// <exception cref="FileNotFoundException"></exception>
    /// <exception cref="NotSupportedException"></exception>
    public async Task<ExtractedDocument> ExtractAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException("Document file was not found.", filePath);

        var extension = Path.GetExtension(filePath).ToLowerInvariant();

        return extension switch
        {
            ".txt" => await ExtractTextFileAsync(filePath, cancellationToken),
            ".docx" => await ExtractWordDocumentAsync(filePath, cancellationToken),
            ".pdf" => await ExtractPdfAsync(filePath, cancellationToken),
            ".xlsx" => await ExtractExcelAsync(filePath, cancellationToken),
            ".xls" => await ExtractExcelAsync(filePath, cancellationToken),

            _ => throw new NotSupportedException($"File type '{extension}' is not supported yet.")
        };
    }

    /// <summary>
    /// Extracts text from a text file.
    /// </summary>
    /// <param name="filePath"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    private static async Task<ExtractedDocument> ExtractTextFileAsync(string filePath, CancellationToken cancellationToken)
    {
        var text = await File.ReadAllTextAsync(filePath, cancellationToken);

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

    /// <summary>
    /// Extracts sections from the given text.
    /// </summary>
    /// <param name="text"></param>
    /// <returns></returns>
    private static List<DocumentSection> ExtractSections(string text)
    {
        var lines = text.Split('\n');
        var sections = new List<DocumentSection>();

        DocumentSection? currentSection = null;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();

            if (string.IsNullOrWhiteSpace(line))
            {
                AppendBlankLine(currentSection);
                continue;
            }

            if (IsTopLevelHeading(line))
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

            AppendLine(currentSection, line);
        }

        if (currentSection != null)
        {
            FinalizeSection(currentSection);

            if (HasUsefulContent(currentSection))
                sections.Add(currentSection);
        }

        return sections;
    }

    /// <summary>
    /// Determines whether a line represents a top-level heading.
    /// </summary>
    /// <param name="line"></param>
    /// <returns></returns>
    private static bool IsTopLevelHeading(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return false;

        line = line.Trim();

        if (!Regex.IsMatch(line, @"^\d+[\.\)]\s+\S+"))
            return false;

        if (Regex.IsMatch(line, @"[.!?;:]$"))
            return false;

        return true;
    }

    /// <summary>
    /// Appends a line to the specified document section.
    /// </summary>
    /// <param name="section"></param>
    /// <param name="line"></param>
    private static void AppendLine(DocumentSection section, string line)
    {
        if (string.IsNullOrEmpty(section.Content))
        {
            section.Content = line;
            return;
        }

        if (!section.Content.EndsWith('\n'))
            section.Content += "\n";

        section.Content += line;
    }

    /// <summary>
    /// Appends a blank line to the specified document section.
    /// </summary>
    /// <param name="section"></param>
    private static void AppendBlankLine(DocumentSection? section)
    {
        if (section == null)
            return;

        if (string.IsNullOrEmpty(section.Content))
            return;

        if (!section.Content.EndsWith("\n\n"))
            section.Content += "\n";
    }

    /// <summary>
    /// Finalizes a document section by normalizing its content and setting metadata.
    /// </summary>
    /// <param name="section"></param>
    private static void FinalizeSection(DocumentSection section)
    {
        section.Content = NormalizeSectionContent(section.Content);

        if (!string.IsNullOrWhiteSpace(section.Heading))
            section.Metadata["section"] = section.Heading;
    }

    /// <summary>
    /// Determines whether a document section contains useful content.
    /// </summary>
    /// <param name="section"></param>
    /// <returns></returns>
    private static bool HasUsefulContent(DocumentSection section)
    {
        return !string.IsNullOrWhiteSpace(section.Heading) || !string.IsNullOrWhiteSpace(section.Content);
    }

    /// <summary>
    /// Normalizes the content of a document section by replacing multiple spaces and tabs with a single space and trimming whitespace.
    /// </summary>
    /// <param name="content"></param>
    /// <returns></returns>
    private static string NormalizeSectionContent(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return string.Empty;

        return Regex.Replace(content, @"[ \t]+", " ").Trim();
    }

    /// <summary>
    /// Normalizes the text by replacing carriage returns and trimming whitespace.
    /// </summary>
    /// <param name="text"></param>
    /// <returns></returns>
    private static string NormalizeText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        return text.Replace("\r\n", "\n").Replace("\r", "\n").Trim();
    }

    /// <summary>
    /// Extracts text and sections from a Word document.
    /// </summary>
    /// <param name="filePath"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    private static Task<ExtractedDocument> ExtractWordDocumentAsync(string filePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var document = WordprocessingDocument.Open(filePath, false);

        var body = document.MainDocumentPart?.Document?.Body;

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
                ProcessParagraph(paragraph, ref currentSection, sections);
            }
            else if (element is Table table)
            {
                ProcessTable(table, ref currentSection, sections);
            }
        }

        if (currentSection != null)
        {
            FinalizeSection(currentSection);

            if (HasUsefulContent(currentSection))
                sections.Add(currentSection);
        }

        var text = string.Join("\n\n", sections.Select(BuildSectionText).Where(x => !string.IsNullOrWhiteSpace(x)));

        return Task.FromResult(
            new ExtractedDocument
            {
                Text = text,
                Sections = sections
            });
    }

    /// <summary>
    /// Processes a paragraph and appends its content to the current document section.
    /// </summary>
    /// <param name="paragraph"></param>
    /// <param name="currentSection"></param>
    /// <param name="sections"></param>
    private static void ProcessParagraph(Paragraph paragraph, ref DocumentSection? currentSection, List<DocumentSection> sections)
    {
        var text = paragraph.InnerText?.Trim();

        if (string.IsNullOrWhiteSpace(text))
            return;

        var style = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value;

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

        AppendContent(currentSection, text);
    }

    /// <summary>
    /// Determines whether a given style is a heading style.
    /// </summary>
    /// <param name="style"></param>
    /// <returns></returns>
    private static bool IsHeadingStyle(string? style)
    {
        if (string.IsNullOrWhiteSpace(style))
            return false;

        return
            style.Equals("Heading1", StringComparison.OrdinalIgnoreCase) || style.Equals("Heading2", StringComparison.OrdinalIgnoreCase)
            || style.Equals("Heading3", StringComparison.OrdinalIgnoreCase) || style.Equals("Heading4", StringComparison.OrdinalIgnoreCase)
            || style.Equals("Heading5", StringComparison.OrdinalIgnoreCase) || style.Equals("Heading6", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Processes a table and appends its content to the current document section.
    /// </summary>
    /// <param name="table"></param>
    /// <param name="currentSection"></param>
    /// <param name="sections"></param>
    private static void ProcessTable(Table table, ref DocumentSection? currentSection, List<DocumentSection> sections)
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

            AppendContent(currentSection, string.Join(" | ", cells));
        }
    }

    /// <summary>
    /// Appends content to a document section.
    /// </summary>
    /// <param name="section"></param>
    /// <param name="content"></param>
    private static void AppendContent(DocumentSection section, string content)
    {
        content = NormalizeWhitespace(content);

        if (string.IsNullOrWhiteSpace(content))
            return;

        if (string.IsNullOrWhiteSpace(section.Content))
        {
            section.Content = content;
            return;
        }

        section.Content += "\n" + content;
    }

    /// <summary>
    /// Normalizes the whitespace in a string.
    /// </summary>
    /// <param name="text"></param>
    /// <returns></returns>
    private static string NormalizeWhitespace(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        return Regex.Replace(text, @"\s+", " ").Trim();
    }

    /// <summary>
    /// Builds the text for a document section.
    /// </summary>
    /// <param name="section"></param>
    /// <returns></returns>
    private static string BuildSectionText(DocumentSection section)
    {
        if (string.IsNullOrWhiteSpace(section.Heading))
            return section.Content.Trim();

        if (string.IsNullOrWhiteSpace(section.Content))
            return section.Heading.Trim();

        return
            $"{section.Heading.Trim()}\n\n" +
            section.Content.Trim();
    }

    /// <summary>
    /// Extracts text and sections from a PDF document.
    /// </summary>
    /// <param name="filePath"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    private static Task<ExtractedDocument> ExtractPdfAsync(string filePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var sections = new List<DocumentSection>();

        using (var document = PdfDocument.Open(filePath))
        {
            foreach (var page in document.GetPages())
            {
                cancellationToken.ThrowIfCancellationRequested();

                var text = NormalizeText(page.Text ?? string.Empty);

                if (string.IsNullOrWhiteSpace(text))
                    continue;

                var pageNumber = page.Number;

                sections.Add(new DocumentSection
                {
                    Heading = $"Page {pageNumber}",
                    Content = text,
                    ContentType = "application/pdf",
                    SourceLocation = $"Page: {pageNumber}",
                    Metadata = new Dictionary<string, string>
                    {
                        ["PageNumber"] = pageNumber.ToString(
                            CultureInfo.InvariantCulture)
                    }
                });
            }
        }

        var extractedText = string.Join(Environment.NewLine + Environment.NewLine, sections.Select(BuildSectionText));

        return Task.FromResult(new ExtractedDocument
        {
            Text = extractedText,
            Sections = sections
        });
    }

    /// <summary>
    /// Extracts the content from an Excel workbook.
    /// </summary>
    /// <param name="filePath"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    /// <exception cref="FileNotFoundException"></exception>
    public async Task<ExtractedDocument> ExtractExcelAsync(string filePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("Excel document was not found.", filePath);
        }

        var sections = await Task.Run(() => ExtractWorkbook(filePath, cancellationToken), cancellationToken);

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

    /// <summary>
    /// Extracts the content from an Excel workbook.
    /// </summary>
    /// <param name="filePath"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    private static List<DocumentSection> ExtractWorkbook(string filePath, CancellationToken cancellationToken)
    {
        var sections = new List<DocumentSection>();

        using var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

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

                    values.Add(
                        value == null || value == DBNull.Value
                            ? string.Empty
                            : Convert.ToString(
                                value,
                                CultureInfo.InvariantCulture)?.Trim()
                                ?? string.Empty);
                }

                if (values.Any(value => !string.IsNullOrWhiteSpace(value)))
                    rows.Add((rowNumber, values));
            }

            if (rows.Count == 0)
                continue;

            var headerRow = rows[0];
            var headers = BuildHeaders(headerRow.Values);

            var contentLines = new List<string>();

            for (var i = 1; i < rows.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var (excelRowNumber, values) = rows[i];
                var fields = new List<string>();

                for (var column = 0; column < values.Count; column++)
                {
                    var value = values[column];

                    if (string.IsNullOrWhiteSpace(value))
                        continue;

                    var header = column < headers.Count ? headers[column] : $"Column {column + 1}";

                    fields.Add($"{header}: {value}");
                }

                if (fields.Count == 0)
                    continue;

                sections.Add(new DocumentSection
                {
                    Heading = sheetName,
                    Content = string.Join(" | ", fields),
                    SourceLocation = $"Worksheet: {sheetName}, Row: {excelRowNumber}",
                    ContentType = "ExcelRow",
                    Metadata = new Dictionary<string, string>
                    {
                        ["WorksheetName"] = sheetName,
                        ["ExcelRowNumber"] =
                            excelRowNumber.ToString(
                                CultureInfo.InvariantCulture),
                        ["RecordType"] = "ExcelRow"
                    }
                });
            }

            if (contentLines.Count == 0)
                contentLines.Add("Columns: " + string.Join(" | ", headers));

            sections.Add(new DocumentSection
            {
                Heading = sheetName,
                Content = string.Join(Environment.NewLine, contentLines),
                SourceLocation = $"Worksheet: {sheetName}",
                ContentType = "ExcelWorksheet",
                Metadata = new Dictionary<string, string>
                {
                    ["WorksheetName"] = sheetName,
                    ["HeaderRow"] = headerRow.Number.ToString(
                        CultureInfo.InvariantCulture)
                }
            });
        }
        while (reader.NextResult());

        return sections;
    }

    /// <summary>
    /// Builds a list of unique header names from the provided values.
    /// </summary>
    /// <param name="headerValues"></param>
    /// <returns></returns>
    private static List<string> BuildHeaders(List<string> headerValues)
    {
        var headers = new List<string>();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < headerValues.Count; i++)
        {
            var header = string.IsNullOrWhiteSpace(headerValues[i]) ? $"Column {i + 1}" : headerValues[i];

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
}