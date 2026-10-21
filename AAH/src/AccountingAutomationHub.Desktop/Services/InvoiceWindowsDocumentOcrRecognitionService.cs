using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using DrawingBitmap = System.Drawing.Bitmap;
using DrawingGraphics = System.Drawing.Graphics;
using DrawingImageLockMode = System.Drawing.Imaging.ImageLockMode;
using DrawingPixelFormat = System.Drawing.Imaging.PixelFormat;
using DrawingRectangle = System.Drawing.Rectangle;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;

namespace AccountingAutomationHub.Desktop.Services;

/// <summary>
/// R46 R37 self-contained Windows invoice OCR + pixel-grid authority.
///
/// This service does not use Bank OCR, PaddleOCR, Python, llama.cpp, GGUF models, an OCR worker,
/// or any external OCR folder. It uses the Windows OCR engine only to turn pixels into words with
/// bounding rectangles, then AAH itself detects printed table rules from pixels and reconstructs invoice header bands, item-table cells and
/// GST-summary columns from geometry. The result is emitted in the same neutral layout-page shape
/// consumed by the invoice accounting verifier.
/// </summary>
internal sealed class InvoiceWindowsDocumentOcrRecognitionService
{
    private static readonly SemaphoreSlim GlobalInvoiceOcrGate = new(1, 1);

    public async Task<BankOcrRecognitionBatch> RecognizeRawPagesAsync(
        IReadOnlyList<string> sourcePaths,
        string runRoot,
        Action<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var sources = sourcePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Directory.CreateDirectory(runRoot);
        var logPath = Path.Combine(runRoot, "invoice-windows-ocr.log");
        var statusPath = Path.Combine(runRoot, "invoice-windows-ocr-status.json");
        var resultRoot = Path.Combine(runRoot, "pages");
        Directory.CreateDirectory(resultRoot);

        if (sources.Length == 0)
            return BankOcrRecognitionBatch.Failed("No captured invoice image is available for AAH Windows Invoice OCR.", logPath, statusPath, resultRoot);
        if (!OperatingSystem.IsWindows())
            return BankOcrRecognitionBatch.Failed("AAH Windows Invoice OCR is available only on Windows.", logPath, statusPath, resultRoot);

        foreach (var source in sources)
        {
            var extension = Path.GetExtension(source);
            if (!BankOcrExcelConversionService.IsSupportedOcrSource(source) || extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
                return BankOcrRecognitionBatch.Failed($"AAH Windows Invoice OCR does not support '{extension}' for this mobile scan.", logPath, statusPath, resultRoot);
        }

        var engine = OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine is null)
        {
            var language = OcrEngine.AvailableRecognizerLanguages.FirstOrDefault();
            if (language is not null) engine = OcrEngine.TryCreateFromLanguage(language);
        }
        if (engine is null)
            return BankOcrRecognitionBatch.Failed("Windows OCR is not available. Install at least one Windows OCR language capability and retry the invoice scan.", logPath, statusPath, resultRoot);

        if (GlobalInvoiceOcrGate.CurrentCount == 0)
            progress?.Invoke("AAH Invoice OCR • another invoice is being recognized; waiting for the local Windows OCR engine");
        await GlobalInvoiceOcrGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var pages = new List<BankOcrRecognitionPage>();
            var errors = new List<string>();
            await WriteStatusAsync(statusPath, "RUNNING", "AAH Windows Invoice OCR started.", cancellationToken).ConfigureAwait(false);
            await AppendLogAsync(logPath, $"AAH Invoice OCR started for {sources.Length} image(s).", cancellationToken).ConfigureAwait(false);

            for (var index = 0; index < sources.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = sources[index];
                progress?.Invoke($"AAH Invoice OCR • recognizing page {index + 1}/{sources.Length} with Windows OCR + AAH geometry");
                try
                {
                    var page = await RecognizePageAsync(engine, source, index + 1, resultRoot, cancellationToken).ConfigureAwait(false);
                    pages.Add(page);
                    await AppendLogAsync(logPath, $"Page {index + 1}: {Path.GetFileName(source)} -> {page.JsonPath}", cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    var message = $"{Path.GetFileName(source)}: {exception.Message}";
                    errors.Add(message);
                    await AppendLogAsync(logPath, "ERROR " + message, cancellationToken).ConfigureAwait(false);
                }
            }

            if (pages.Count == 0)
            {
                await WriteStatusAsync(statusPath, "ERROR", errors.FirstOrDefault() ?? "Windows OCR returned no invoice pages.", cancellationToken).ConfigureAwait(false);
                return BankOcrRecognitionBatch.Failed(errors.FirstOrDefault() ?? "AAH Windows Invoice OCR returned no usable page.", logPath, statusPath, resultRoot);
            }

            await WriteStatusAsync(statusPath, "READY", $"Recognized {pages.Count} invoice page(s).", cancellationToken).ConfigureAwait(false);
            progress?.Invoke($"AAH Invoice OCR • {pages.Count:N0} page(s) recognized; reconstructing invoice boxes and tables");
            return new BankOcrRecognitionBatch
            {
                WasSuccessful = true,
                Pages = pages,
                SourceErrors = errors,
                LogPath = logPath,
                StatusPath = statusPath,
                ResultPath = resultRoot
            };
        }
        finally
        {
            GlobalInvoiceOcrGate.Release();
        }
    }

    private static async Task<BankOcrRecognitionPage> RecognizePageAsync(
        OcrEngine engine,
        string sourcePath,
        int pageIndex,
        string resultRoot,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var file = await StorageFile.GetFileFromPathAsync(sourcePath);
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = await file.OpenReadAsync();
        cancellationToken.ThrowIfCancellationRequested();
        var decoder = await BitmapDecoder.CreateAsync(stream);

        if (Math.Max(decoder.PixelWidth, decoder.PixelHeight) > (uint)OcrEngine.MaxImageDimension)
            throw new InvalidOperationException($"Invoice image is {decoder.PixelWidth}x{decoder.PixelHeight}; Windows OCR supports up to {OcrEngine.MaxImageDimension}px on the longest edge. Mobile Link should upload its reduced document image.");

        cancellationToken.ThrowIfCancellationRequested();
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        cancellationToken.ThrowIfCancellationRequested();
        var ocr = await engine.RecognizeAsync(bitmap);
        var words = ocr.Lines
            .SelectMany(line => line.Words)
            .Where(word => !string.IsNullOrWhiteSpace(word.Text))
            .Select(word => new OcrWordBox(
                word.Text.Trim(),
                (int)Math.Round(word.BoundingRect.X),
                (int)Math.Round(word.BoundingRect.Y),
                Math.Max(1, (int)Math.Round(word.BoundingRect.Width)),
                Math.Max(1, (int)Math.Round(word.BoundingRect.Height))))
            .ToArray();
        if (words.Length == 0) throw new InvalidOperationException("Windows OCR found no text on the captured invoice image.");

        var rows = BuildRows(words);
        var ruledGrid = DetectRuledGrid(sourcePath, (int)decoder.PixelWidth, (int)decoder.PixelHeight);
        var blocks = BuildLayoutBlocks(rows, words, ruledGrid, (int)decoder.PixelWidth, (int)decoder.PixelHeight);
        var pageJson = new
        {
            width = (int)decoder.PixelWidth,
            height = (int)decoder.PixelHeight,
            engine = "AAH-WINDOWS-INVOICE-OCR-V3-DOCUMENT-UNDERSTANDING",
            // R46 R38: retain atomic OCR tokens with their 2-D geometry.  The document-understanding
            // layer must never be forced to reconstruct spatial relationships from flattened text.
            word_res_list = words.Select((word, order) => new
            {
                text = word.Text,
                bbox = new[] { word.X, word.Y, word.Right, word.Bottom },
                order = order + 1
            }).ToArray(),
            parsing_res_list = blocks.Select((block, order) => new
            {
                block_label = block.Label,
                block_content = block.Content,
                block_bbox = new[] { block.X1, block.Y1, block.X2, block.Y2 },
                block_order = order + 1
            }).ToArray()
        };

        var jsonPath = Path.Combine(resultRoot, $"page_{pageIndex:000}.json");
        await File.WriteAllTextAsync(
            jsonPath,
            JsonSerializer.Serialize(pageJson, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false),
            cancellationToken).ConfigureAwait(false);

        return new BankOcrRecognitionPage
        {
            Id = $"invoice-page-{pageIndex:000}",
            SourcePath = sourcePath,
            SourceIndex = pageIndex - 1,
            SourcePage = pageIndex,
            JsonPath = jsonPath
        };
    }

    private static IReadOnlyList<OcrVisualRow> BuildRows(IReadOnlyList<OcrWordBox> words)
    {
        var medianHeight = words.Select(x => x.Height).OrderBy(x => x).ElementAt(words.Count / 2);
        var tolerance = Math.Max(5d, medianHeight * 0.70d);
        var rows = new List<OcrVisualRow>();

        foreach (var word in words.OrderBy(x => x.CenterY).ThenBy(x => x.X))
        {
            OcrVisualRow? best = null;
            var bestDistance = double.MaxValue;
            for (var index = Math.Max(0, rows.Count - 6); index < rows.Count; index++)
            {
                var candidate = rows[index];
                var distance = Math.Abs(candidate.CenterY - word.CenterY);
                var overlap = Math.Max(0, Math.Min(candidate.Bottom, word.Bottom) - Math.Max(candidate.Top, word.Y));
                var overlapRatio = overlap / (double)Math.Max(1, Math.Min(candidate.Height, word.Height));
                if (distance <= tolerance || overlapRatio >= 0.45d)
                {
                    if (distance < bestDistance) { best = candidate; bestDistance = distance; }
                }
            }
            if (best is null)
            {
                best = new OcrVisualRow();
                rows.Add(best);
            }
            best.Words.Add(word);
            best.Recalculate();
        }

        foreach (var row in rows) row.Words.Sort((a, b) => a.X.CompareTo(b.X));
        return rows.OrderBy(x => x.Top).ThenBy(x => x.Left).ToArray();
    }

    private static IReadOnlyList<LayoutOutputBlock> BuildLayoutBlocks(
        IReadOnlyList<OcrVisualRow> rows,
        IReadOnlyList<OcrWordBox> words,
        RuledGridDetection ruledGrid,
        int width,
        int height)
    {
        var blocks = new List<LayoutOutputBlock>();

        // R46 R37: printed grid lines are the first table-layout authority. OCR header spelling is
        // deliberately not required to discover a ruled invoice table. The grid detector finds row
        // bands and persistent vertical separators directly from pixels, then accounting arithmetic
        // identifies Taxable/GST/CGST/SGST columns.
        var gridCandidates = BuildGridTableCandidates(words, ruledGrid, width, height);
        var gridItem = gridCandidates
            .Where(x => x.Kind == GridTableKind.Item)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Block.Y1)
            .FirstOrDefault();
        var gridSummary = gridCandidates
            .Where(x => x.Kind == GridTableKind.Summary)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Block.Y1)
            .FirstOrDefault();

        TableRange? itemRange = null;
        TableRange? summaryRange = null;
        LayoutOutputBlock? itemTable = gridItem?.Block;
        LayoutOutputBlock? summaryTable = gridSummary?.Block;

        // Keep the old OCR-text table reconstruction only as a fallback for invoices that do not
        // contain printed rules. A detected pixel grid always wins over damaged labels such as
        // ESN/GSz/CST/S-Urcs.
        if (itemTable is null || summaryTable is null)
        {
            var itemHeader = FindBestHeader(rows, ItemHeaderScore, 6, 4, 0, rows.Count);
            var summarySearchStart = itemHeader is null ? 0 : itemHeader.End + 1;
            var summaryHeader = FindBestHeader(rows, SummaryHeaderScore, 5, 4, summarySearchStart, rows.Count);

            if (itemTable is null && itemHeader is not null)
            {
                itemRange = FindTableBodyRange(rows, itemHeader, summaryHeader?.Start ?? rows.Count, isSummary: false);
                itemTable = BuildItemTable(rows, itemRange, width);
            }
            if (summaryTable is null && summaryHeader is not null)
            {
                summaryRange = FindTableBodyRange(rows, summaryHeader, rows.Count, isSummary: true);
                summaryTable = BuildSummaryTable(rows, summaryRange, width);
            }
        }

        // R46 R38 borderless/spatial fallback.  A photographed invoice can contain a perfectly
        // readable item table even when rule detection is incomplete or headings OCR as ESN/GSz/CST.
        // Locate HSN-led row baselines, then infer Taxable/GST/tax/net columns from accounting
        // arithmetic.  Prefer this reconstruction whenever it recovers more proved item rows.
        var spatialItem = BuildSpatialItemTableFallback(words, width, height);
        (int Top, int Bottom)? spatialItemRange = null;
        if (spatialItem is not null && (itemTable is null || CountTableDataRows(spatialItem.Block.Content) > CountTableDataRows(itemTable.Content)))
        {
            itemTable = spatialItem.Block;
            spatialItemRange = (spatialItem.Top, spatialItem.Bottom);
        }

        var excluded = new List<(int Top, int Bottom)>();
        if (spatialItemRange is not null) excluded.Add(spatialItemRange.Value);
        else if (gridItem is not null) excluded.Add((gridItem.Region.Top, gridItem.Region.Bottom));
        else if (itemRange is not null) excluded.Add((rows[itemRange.Start].Top, rows[itemRange.End].Bottom));
        if (gridSummary is not null) excluded.Add((gridSummary.Region.Top, gridSummary.Region.Bottom));
        else if (summaryRange is not null) excluded.Add((rows[summaryRange.Start].Top, rows[summaryRange.End].Bottom));

        foreach (var row in rows)
        {
            var center = row.CenterY;
            if (excluded.Any(range => center >= range.Top && center <= range.Bottom)) continue;
            foreach (var segment in SplitRowByHorizontalGaps(row, width))
            {
                if (segment.Text.Length == 0) continue;
                blocks.Add(new LayoutOutputBlock("text", segment.Text, segment.Left, segment.Top, segment.Right, segment.Bottom));
            }
        }

        if (itemTable is not null) blocks.Add(itemTable);
        if (summaryTable is not null) blocks.Add(summaryTable);

        // Keep visual reading order; tables are placed at their physical Y coordinate.
        return blocks.OrderBy(x => x.Y1).ThenBy(x => x.X1).ToArray();
    }

    private static IReadOnlyList<OcrVisualRow> SplitRowByHorizontalGaps(OcrVisualRow row, int pageWidth)
    {
        var words = row.Words.OrderBy(x => x.X).ToArray();
        if (words.Length <= 1) return [row];
        var gapThreshold = Math.Max(44d, pageWidth * 0.045d);
        var result = new List<OcrVisualRow>();
        var current = new OcrVisualRow();
        current.Words.Add(words[0]);
        current.Recalculate();
        for (var index = 1; index < words.Length; index++)
        {
            var gap = words[index].X - words[index - 1].Right;
            if (gap > gapThreshold)
            {
                current.Recalculate();
                result.Add(current);
                current = new OcrVisualRow();
            }
            current.Words.Add(words[index]);
            current.Recalculate();
        }
        if (current.Words.Count > 0) result.Add(current);
        return result;
    }

    private static HeaderWindow? FindBestHeader(
        IReadOnlyList<OcrVisualRow> rows,
        Func<string, int> scoreFunction,
        int minimumScore,
        int maxRows,
        int start,
        int end)
    {
        HeaderWindow? best = null;
        for (var index = Math.Max(0, start); index < Math.Min(rows.Count, end); index++)
        {
            for (var length = 1; length <= maxRows && index + length <= Math.Min(rows.Count, end); length++)
            {
                var text = Normalize(string.Join(" ", rows.Skip(index).Take(length).Select(x => x.Text)));
                var score = scoreFunction(text);
                if (score < minimumScore) continue;
                if (best is null || score > best.Score || (score == best.Score && length < best.Length))
                    best = new HeaderWindow(index, index + length - 1, score);
            }
        }
        return best;
    }

    private static int ItemHeaderScore(string text)
    {
        var score = 0;
        if (Has(text, "HSN") || Has(text, "SAC")) score += 2;
        if (Has(text, "PRODUCT") || Has(text, "DESCRIPTION") || Has(text, "ITEM")) score += 2;
        if (Has(text, "TAXABLE")) score += 2;
        if (Has(text, "GST")) score += 1;
        if (Has(text, "CGST")) score += 2;
        if (Has(text, "SGST") || Has(text, "UTGST") || Has(text, "UIGST")) score += 2;
        if (Has(text, "NET")) score += 1;
        return score;
    }

    private static int SummaryHeaderScore(string text)
    {
        var score = 0;
        if (Has(text, "PARTICULAR")) score += 2;
        if (Has(text, "GROSS")) score += 1;
        if (Has(text, "TAXABLE")) score += 2;
        if (Has(text, "TAX AMT") || Has(text, "TAX AMOUNT")) score += 2;
        if (Has(text, "NET PAYABLE") || (Has(text, "NET") && Has(text, "PAYABLE"))) score += 2;
        if (Has(text, "CGST") || Has(text, "SGST")) score += 1;
        return score;
    }

    private static TableRange FindTableBodyRange(
        IReadOnlyList<OcrVisualRow> rows,
        HeaderWindow header,
        int hardEnd,
        bool isSummary)
    {
        var end = header.End;
        var seenData = false;
        var blankLike = 0;
        for (var index = header.End + 1; index < Math.Min(rows.Count, hardEnd); index++)
        {
            var text = Normalize(rows[index].Text);
            var dataLike = isSummary
                ? Has(text, "CGST") || Has(text, "SGST") || Has(text, "IGST") || text.StartsWith("TOTAL", StringComparison.Ordinal) || CountNumbers(text) >= 3
                : LooksLikeItemRow(text) || text.StartsWith("TOTAL", StringComparison.Ordinal);
            if (dataLike)
            {
                seenData = true;
                blankLike = 0;
                end = index;
                if (isSummary && text.StartsWith("TOTAL", StringComparison.Ordinal)) break;
                continue;
            }

            if (!seenData) { if (index - header.End <= 2) { end = index; continue; } break; }
            blankLike++;
            if (blankLike > 1) break;
        }
        return new TableRange(header.Start, Math.Max(header.End, end), header);
    }

    private static LayoutOutputBlock? BuildItemTable(IReadOnlyList<OcrVisualRow> rows, TableRange range, int pageWidth)
    {
        var headerWords = rows.Skip(range.Header.Start).Take(range.Header.Length).SelectMany(x => x.Words).ToArray();
        var tableRows = rows.Skip(range.Header.End + 1).Take(Math.Max(0, range.End - range.Header.End)).ToArray();
        if (tableRows.Length == 0) return null;

        var allWords = headerWords.Concat(tableRows.SelectMany(x => x.Words)).ToArray();
        var left = allWords.Min(x => x.X);
        var right = allWords.Max(x => x.Right);
        var span = Math.Max(1, right - left);
        var columns = new List<ColumnAnchor>
        {
            Anchor("HSN/SAC", headerWords, x => TokenHas(x, "HSN") || TokenHas(x, "SAC"), left + span * 0.08),
            Anchor("Product / Description", headerWords, x => TokenHas(x, "PRODUCT") || TokenHas(x, "DESCRIPTION") || TokenHas(x, "ITEM"), left + span * 0.24),
            Anchor("Qty", headerWords, x => TokenHas(x, "QTY") || TokenHas(x, "PCS") || TokenHas(x, "QUANTITY"), left + span * 0.43),
            Anchor("Unit", headerWords, x => TokenHas(x, "UOM") || Normalize(x.Text) == "UNIT", left + span * 0.48),
            Anchor("Rate", headerWords, x => TokenHas(x, "RATE"), left + span * 0.57),
            Anchor("Discount", headerWords, x => TokenHas(x, "DISC") || TokenHas(x, "DISCOUNT") || TokenHas(x, "SCH"), left + span * 0.64),
            Anchor("Taxable", headerWords, x => TokenHas(x, "TAXABLE"), left + span * 0.72),
            Anchor("GST %", headerWords, x => Normalize(x.Text) == "GST" || Normalize(x.Text) == "GST%", left + span * 0.77),
            Anchor("CGST", headerWords, x => TokenHas(x, "CGST"), left + span * 0.82),
            Anchor("SGST / UTGST", headerWords, x => TokenHas(x, "SGST") || TokenHas(x, "UTGST") || TokenHas(x, "UIGST"), left + span * 0.87),
            Anchor("IGST", headerWords, x => TokenHas(x, "IGST"), left + span * 0.90, optional: true),
            Anchor("Cess", headerWords, x => TokenHas(x, "CESS"), left + span * 0.92, optional: true),
            Anchor("Net Amt", headerWords, x => TokenHas(x, "NET"), left + span * 0.96)
        };
        NormalizeAnchors(columns, left, right);

        var htmlRows = new List<IReadOnlyList<string>> { columns.Select(x => x.Name).ToArray() };

        // Invoice cells are often vertically misaligned inside a ruled row. Instead of trusting
        // OCR line grouping, use each HSN cell as the row baseline and assign every other cell
        // to the nearest HSN baseline. This is what keeps Taxable/GST/CGST/SGST in the same row
        // even when OCR reports them several pixels above or below the product description.
        var hsnAnchor = columns.First(x => x.Name == "HSN/SAC").X;
        var dataWords = tableRows.SelectMany(x => x.Words).ToArray();
        var hsnSeeds = dataWords
            .Where(x => Math.Abs(x.CenterX - hsnAnchor) <= span * 0.10d)
            .Where(x => Regex.IsMatch(x.Text, @"\d{4,8}", RegexOptions.IgnoreCase))
            .OrderBy(x => x.CenterY)
            .ToArray();
        var baselines = new List<double>();
        foreach (var seed in hsnSeeds)
        {
            if (baselines.Count == 0 || Math.Abs(baselines[^1] - seed.CenterY) > Math.Max(7d, seed.Height * 0.70d))
                baselines.Add(seed.CenterY);
        }

        if (baselines.Count >= 2)
        {
            for (var rowIndex = 0; rowIndex < baselines.Count; rowIndex++)
            {
                var low = rowIndex == 0 ? double.MinValue : (baselines[rowIndex - 1] + baselines[rowIndex]) / 2d;
                var high = rowIndex == baselines.Count - 1 ? double.MaxValue : (baselines[rowIndex] + baselines[rowIndex + 1]) / 2d;
                var logicalWords = dataWords.Where(x => x.CenterY > low && x.CenterY <= high).OrderBy(x => x.X).ToArray();
                if (logicalWords.Length == 0) continue;
                var logicalText = Normalize(string.Join(" ", logicalWords.Select(x => x.Text)));
                if (!LooksLikeItemRow(logicalText)) continue;
                htmlRows.Add(AssignCells(logicalWords, columns));
            }
        }
        else
        {
            foreach (var row in tableRows)
            {
                var text = Normalize(row.Text);
                if (!LooksLikeItemRow(text)) continue;
                htmlRows.Add(AssignCells(row.Words, columns));
            }
        }

        var totalRow = tableRows.LastOrDefault(row => Normalize(row.Text).StartsWith("TOTAL", StringComparison.Ordinal));
        if (totalRow is not null) htmlRows.Add(AssignCells(totalRow.Words, columns));
        if (htmlRows.Count <= 1) return null;
        var y1 = rows[range.Start].Top;
        var y2 = rows[range.End].Bottom;
        return new LayoutOutputBlock("table", ToHtmlTable(htmlRows), left, y1, right, y2);
    }

    private static LayoutOutputBlock? BuildSummaryTable(IReadOnlyList<OcrVisualRow> rows, TableRange range, int pageWidth)
    {
        var headerWords = rows.Skip(range.Header.Start).Take(range.Header.Length).SelectMany(x => x.Words).ToArray();
        var tableRows = rows.Skip(range.Header.End + 1).Take(Math.Max(0, range.End - range.Header.End)).ToArray();
        if (tableRows.Length == 0) return null;
        var allWords = headerWords.Concat(tableRows.SelectMany(x => x.Words)).ToArray();
        var left = allWords.Min(x => x.X);
        var right = allWords.Max(x => x.Right);
        var span = Math.Max(1, right - left);
        var columns = new List<ColumnAnchor>
        {
            Anchor("Particulars", headerWords, x => TokenHas(x, "PARTICULAR"), left + span * 0.10),
            Anchor("Gross Amt", headerWords, x => TokenHas(x, "GROSS"), left + span * 0.31),
            Anchor("Sch Amt", headerWords, x => TokenHas(x, "SCH"), left + span * 0.42),
            Anchor("RS Disc", headerWords, x => TokenHas(x, "DISC") || TokenHas(x, "DISCOUNT"), left + span * 0.50),
            Anchor("Taxable Amt", headerWords, x => TokenHas(x, "TAXABLE"), left + span * 0.61),
            Anchor("Tax Amt", headerWords, x => Normalize(x.Text) == "TAX" || TokenHas(x, "TAXAMT"), left + span * 0.72),
            Anchor("Net Payable Amt", headerWords, x => TokenHas(x, "NET") || TokenHas(x, "PAYABLE"), left + span * 0.94)
        };
        NormalizeAnchors(columns, left, right);

        var htmlRows = new List<IReadOnlyList<string>> { columns.Select(x => x.Name).ToArray() };
        foreach (var row in tableRows)
        {
            var text = Normalize(row.Text);
            if (!(Has(text, "CGST") || Has(text, "SGST") || Has(text, "IGST") || text.StartsWith("TOTAL", StringComparison.Ordinal) || CountNumbers(text) >= 3)) continue;
            htmlRows.Add(AssignCells(row.Words, columns));
        }
        if (htmlRows.Count <= 1) return null;
        return new LayoutOutputBlock("table", ToHtmlTable(htmlRows), left, rows[range.Start].Top, right, rows[range.End].Bottom);
    }

    private static IReadOnlyList<string> AssignCells(IReadOnlyList<OcrWordBox> words, IReadOnlyList<ColumnAnchor> columns)
    {
        var buckets = Enumerable.Range(0, columns.Count).Select(_ => new List<OcrWordBox>()).ToArray();
        foreach (var word in words.OrderBy(x => x.X))
        {
            var center = word.CenterX;
            var selected = 0;
            var best = double.MaxValue;
            for (var index = 0; index < columns.Count; index++)
            {
                var distance = Math.Abs(columns[index].X - center);
                if (distance < best) { best = distance; selected = index; }
            }
            buckets[selected].Add(word);
        }
        return buckets.Select(bucket => Collapse(string.Join(" ", bucket.OrderBy(x => x.X).Select(x => x.Text)))).ToArray();
    }

    private static ColumnAnchor Anchor(string name, IReadOnlyList<OcrWordBox> words, Func<OcrWordBox, bool> predicate, double fallback, bool optional = false)
    {
        var matches = words.Where(predicate).ToArray();
        if (matches.Length == 0) return new ColumnAnchor(name, fallback, optional, false);
        return new ColumnAnchor(name, matches.Average(x => x.CenterX), optional, true);
    }

    private static void NormalizeAnchors(IList<ColumnAnchor> columns, int left, int right)
    {
        // Drop optional columns that were not actually printed. Required columns keep a proportional fallback.
        for (var index = columns.Count - 1; index >= 0; index--)
            if (columns[index].Optional && !columns[index].Observed) columns.RemoveAt(index);

        var orderedColumns = columns.OrderBy(column => column.X).ToArray();
        for (var index = 0; index < orderedColumns.Length; index++)
            columns[index] = orderedColumns[index];
        var minimumGap = Math.Max(4d, (right - left) / 100d);
        for (var index = 1; index < columns.Count; index++)
        {
            if (columns[index].X <= columns[index - 1].X + minimumGap)
                columns[index] = columns[index] with { X = columns[index - 1].X + minimumGap };
        }
    }

    private static RuledGridDetection DetectRuledGrid(string sourcePath, int expectedWidth, int expectedHeight)
    {
        try
        {
            using var source = new DrawingBitmap(sourcePath);
            if (source.Width <= 0 || source.Height <= 0) return RuledGridDetection.Empty;
            using var bitmap = new DrawingBitmap(source.Width, source.Height, DrawingPixelFormat.Format24bppRgb);
            using (var graphics = DrawingGraphics.FromImage(bitmap))
                graphics.DrawImage(source, 0, 0, bitmap.Width, bitmap.Height);

            var rectangle = new DrawingRectangle(0, 0, bitmap.Width, bitmap.Height);
            var data = bitmap.LockBits(rectangle, DrawingImageLockMode.ReadOnly, DrawingPixelFormat.Format24bppRgb);
            byte[] buffer;
            try
            {
                var stride = Math.Abs(data.Stride);
                buffer = new byte[stride * bitmap.Height];
                Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);
                var dark = new byte[bitmap.Width * bitmap.Height];
                const int darkThreshold = 205;
                for (var y = 0; y < bitmap.Height; y++)
                {
                    var sourceRow = data.Stride >= 0 ? y : bitmap.Height - 1 - y;
                    var offset = sourceRow * stride;
                    for (var x = 0; x < bitmap.Width; x++)
                    {
                        var pixel = offset + x * 3;
                        var b = buffer[pixel];
                        var g = buffer[pixel + 1];
                        var r = buffer[pixel + 2];
                        var gray = (r * 77 + g * 150 + b * 29) >> 8;
                        dark[y * bitmap.Width + x] = gray < darkThreshold ? (byte)1 : (byte)0;
                    }
                }
                var detected = DetectRuledGridFromDarkPixels(dark, bitmap.Width, bitmap.Height);
                if (bitmap.Width == expectedWidth && bitmap.Height == expectedHeight) return detected;
                if (expectedWidth <= 0 || expectedHeight <= 0) return detected;
                var scaleX = expectedWidth / (double)bitmap.Width;
                var scaleY = expectedHeight / (double)bitmap.Height;
                return detected.Scale(scaleX, scaleY);
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
        }
        catch
        {
            // Pixel-grid detection is an enhancement. Windows word OCR remains the fallback and
            // the caller stays fail-closed if invoice accounting cannot be reconstructed.
            return RuledGridDetection.Empty;
        }
    }

    private static RuledGridDetection DetectRuledGridFromDarkPixels(byte[] dark, int width, int height)
    {
        if (width < 200 || height < 200 || dark.Length < width * height) return RuledGridDetection.Empty;
        var margin = Math.Max(3, width / 120);
        var innerWidth = Math.Max(1, width - margin * 2);
        var horizontalThreshold = Math.Max(40, (int)Math.Round(innerWidth * 0.40d));
        var horizontalCandidates = new List<(int Y, int Count)>();
        for (var y = 0; y < height; y++)
        {
            var count = 0;
            var offset = y * width;
            for (var x = margin; x < width - margin; x++) count += dark[offset + x];
            if (count >= horizontalThreshold) horizontalCandidates.Add((y, count));
        }

        var horizontalLines = MergeHorizontalLines(horizontalCandidates);
        if (horizontalLines.Count < 4) return RuledGridDetection.Empty;

        var bands = new List<GridBand>();
        var maximumBandHeight = Math.Max(28, (int)Math.Round(height * 0.065d));
        var minimumTableWidth = width * 0.42d;
        for (var index = 0; index + 1 < horizontalLines.Count; index++)
        {
            var top = horizontalLines[index];
            var bottom = horizontalLines[index + 1];
            var bandHeight = bottom - top;
            if (bandHeight < 6 || bandHeight > maximumBandHeight) continue;
            var boundaries = DetectVerticalBoundariesForBand(dark, width, height, top, bottom, margin);
            if (boundaries.Count < 4) continue;
            if (boundaries[^1] - boundaries[0] < minimumTableWidth) continue;
            bands.Add(new GridBand(top, bottom, boundaries));
        }
        if (bands.Count < 2) return RuledGridDetection.Empty;

        var regions = new List<GridRegion>();
        var current = new List<GridBand> { bands[0] };
        for (var index = 1; index < bands.Count; index++)
        {
            var previous = current[^1];
            var next = bands[index];
            var continuityGap = next.Top - previous.Bottom;
            var similarity = BoundarySimilarity(previous.Boundaries, next.Boundaries, Math.Max(4, width / 350));
            if (continuityGap <= Math.Max(4, height / 120) && similarity >= 0.42d)
            {
                current.Add(next);
            }
            else
            {
                AddGridRegion(regions, current, width);
                current = [next];
            }
        }
        AddGridRegion(regions, current, width);
        return new RuledGridDetection(regions);
    }

    private static List<int> MergeHorizontalLines(IReadOnlyList<(int Y, int Count)> candidates)
    {
        var result = new List<int>();
        if (candidates.Count == 0) return result;
        var group = new List<(int Y, int Count)> { candidates[0] };
        for (var index = 1; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            if (candidate.Y <= group[^1].Y + 2) group.Add(candidate);
            else
            {
                result.Add(group.OrderByDescending(x => x.Count).First().Y);
                group = [candidate];
            }
        }
        result.Add(group.OrderByDescending(x => x.Count).First().Y);
        result.Sort();
        return result;
    }

    private static List<int> DetectVerticalBoundariesForBand(byte[] dark, int width, int height, int top, int bottom, int margin)
    {
        var bandHeight = Math.Max(1, bottom - top + 1);
        var required = Math.Max(3, (int)Math.Ceiling(bandHeight * 0.60d));
        var candidates = new List<int>();
        for (var x = margin; x < width - margin; x++)
        {
            var count = 0;
            for (var y = Math.Max(0, top); y <= Math.Min(height - 1, bottom); y++) count += dark[y * width + x];
            if (count >= required) candidates.Add(x);
        }
        if (candidates.Count == 0) return [];
        var result = new List<int>();
        var group = new List<int> { candidates[0] };
        for (var index = 1; index < candidates.Count; index++)
        {
            if (candidates[index] <= group[^1] + 2) group.Add(candidates[index]);
            else
            {
                result.Add((int)Math.Round(group.Average()));
                group = [candidates[index]];
            }
        }
        result.Add((int)Math.Round(group.Average()));
        return result;
    }

    private static double BoundarySimilarity(IReadOnlyList<int> first, IReadOnlyList<int> second, int tolerance)
    {
        if (first.Count == 0 || second.Count == 0) return 0d;
        var firstMatches = first.Count(x => second.Any(y => Math.Abs(x - y) <= tolerance));
        var secondMatches = second.Count(y => first.Any(x => Math.Abs(x - y) <= tolerance));
        return Math.Min(firstMatches / (double)first.Count, secondMatches / (double)second.Count);
    }

    private static void AddGridRegion(ICollection<GridRegion> regions, IReadOnlyList<GridBand> bands, int pageWidth)
    {
        if (bands.Count < 3) return;
        var tolerance = Math.Max(4, pageWidth / 350);
        var clusters = new List<GridBoundaryCluster>();
        for (var bandIndex = 0; bandIndex < bands.Count; bandIndex++)
        {
            foreach (var x in bands[bandIndex].Boundaries)
            {
                GridBoundaryCluster? selected = null;
                var bestDistance = double.MaxValue;
                foreach (var cluster in clusters)
                {
                    var distance = Math.Abs(cluster.Mean - x);
                    if (distance <= tolerance && distance < bestDistance)
                    {
                        selected = cluster;
                        bestDistance = distance;
                    }
                }
                if (selected is null)
                {
                    selected = new GridBoundaryCluster();
                    clusters.Add(selected);
                }
                selected.Values.Add(x);
                selected.Bands.Add(bandIndex);
            }
        }
        var minimumSupport = Math.Max(2, (int)Math.Ceiling(bands.Count * 0.55d));
        var boundaries = clusters
            .Where(x => x.Bands.Count >= minimumSupport)
            .Select(x => (int)Math.Round(x.Mean))
            .Distinct()
            .OrderBy(x => x)
            .ToArray();
        if (boundaries.Length < 4 || boundaries[^1] - boundaries[0] < pageWidth * 0.42d) return;
        regions.Add(new GridRegion(bands[0].Top, bands[^1].Bottom, boundaries, bands.ToArray()));
    }

    private static IReadOnlyList<GridTableCandidate> BuildGridTableCandidates(
        IReadOnlyList<OcrWordBox> words,
        RuledGridDetection detection,
        int pageWidth,
        int pageHeight)
    {
        var result = new List<GridTableCandidate>();
        foreach (var region in detection.Regions)
        {
            var rows = ExtractGridRows(words, region);
            if (rows.Count == 0) continue;
            var item = BuildCanonicalGridItemTable(rows, region, pageWidth, pageHeight);
            if (item is not null) result.Add(item);
            var summary = BuildCanonicalGridSummaryTable(rows, region, pageWidth, pageHeight);
            if (summary is not null) result.Add(summary);
        }
        return result;
    }

    private static List<List<string>> ExtractGridRows(IReadOnlyList<OcrWordBox> words, GridRegion region)
    {
        var result = new List<List<string>>();
        foreach (var band in region.Bands)
        {
            var cells = new List<string>();
            for (var index = 0; index + 1 < region.Boundaries.Count; index++)
            {
                var left = region.Boundaries[index];
                var right = region.Boundaries[index + 1];
                var cellWords = words
                    .Where(x => x.CenterY > band.Top && x.CenterY <= band.Bottom)
                    .Where(x => x.CenterX >= left && x.CenterX < right)
                    .OrderBy(x => x.Y).ThenBy(x => x.X)
                    .Select(x => x.Text);
                cells.Add(Collapse(string.Join(" ", cellWords)));
            }
            result.Add(cells);
        }
        return result;
    }

    private static GridTableCandidate? BuildCanonicalGridItemTable(
        IReadOnlyList<List<string>> rows,
        GridRegion region,
        int pageWidth,
        int pageHeight)
    {
        if (rows.Count < 4 || region.Boundaries.Count < 8) return null;
        var columnCount = rows.Max(x => x.Count);
        var hsnScores = new int[columnCount];
        for (var column = 0; column < columnCount; column++)
            hsnScores[column] = rows.Count(row => column < row.Count && Regex.IsMatch(row[column], @"\b\d{4,8}[A-Z]*\b", RegexOptions.IgnoreCase));
        var hsnColumn = Enumerable.Range(0, columnCount).OrderByDescending(x => hsnScores[x]).ThenBy(x => x).FirstOrDefault();
        if (hsnScores[hsnColumn] < Math.Max(2, rows.Count / 4)) return null;

        var dataRows = rows.Where(row => hsnColumn < row.Count && Regex.IsMatch(row[hsnColumn], @"\b\d{4,8}", RegexOptions.IgnoreCase)).ToArray();
        if (dataRows.Length < 2) return null;

        var best = FindTaxColumnTuple(dataRows, hsnColumn, columnCount);
        if (best is null || best.Score < Math.Max(3d, dataRows.Length * 0.70d)) return null;

        var descriptionColumn = FindDescriptionColumn(dataRows, hsnColumn, best.GstColumn);
        var netColumn = FindNetColumn(dataRows, best, columnCount);
        var htmlRows = new List<IReadOnlyList<string>>
        {
            new[] { "HSN/SAC", "Product / Description", "Qty", "Unit", "Rate", "Discount", "Taxable", "GST %", "CGST", "SGST / UTGST", "IGST", "Cess", "Net Amt" }
        };
        foreach (var row in dataRows)
        {
            var hsn = GridCell(row, hsnColumn);
            var description = descriptionColumn >= 0 ? GridCell(row, descriptionColumn) : string.Empty;
            if (description.Length == 0)
            {
                var sameCellDescription = Regex.Replace(hsn, @"^\s*\d{4,8}[A-Z]*\s*", string.Empty, RegexOptions.IgnoreCase).Trim();
                if (sameCellDescription.Any(char.IsLetter)) description = sameCellDescription;
            }
            if (description.Length == 0 && hsnColumn + 1 < best.TaxableColumn)
                description = Collapse(string.Join(" ", Enumerable.Range(hsnColumn + 1, Math.Max(0, best.TaxableColumn - hsnColumn - 1)).Select(index => GridCell(row, index)).Where(x => x.Length > 0)));
            htmlRows.Add(new[]
            {
                hsn,
                description,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                GridCell(row, best.TaxableColumn),
                GridCell(row, best.GstColumn),
                GridCell(row, best.CgstColumn),
                GridCell(row, best.SgstColumn),
                string.Empty,
                string.Empty,
                netColumn >= 0 ? GridCell(row, netColumn) : string.Empty
            });
        }
        if (htmlRows.Count < 3) return null;
        var yPad = Math.Max(12, (int)Math.Round(pageHeight * 0.025d));
        var block = new LayoutOutputBlock("table", ToHtmlTable(htmlRows), region.Left, Math.Max(0, region.Top - yPad), region.Right, region.Bottom);
        return new GridTableCandidate(GridTableKind.Item, best.Score + dataRows.Length, block, region);
    }

    private static TaxColumnTuple? FindTaxColumnTuple(IReadOnlyList<List<string>> rows, int hsnColumn, int columnCount)
    {
        TaxColumnTuple? best = null;
        for (var gst = Math.Max(hsnColumn + 2, columnCount / 3); gst < columnCount - 2; gst++)
        {
            var supportedRateRows = rows.Count(row => TryGridAmount(GridCell(row, gst), out var rate) && IsLikelyGstRate(rate));
            if (supportedRateRows < Math.Max(2, rows.Count / 2)) continue;
            for (var taxable = hsnColumn + 1; taxable < gst; taxable++)
            {
                for (var cgst = gst + 1; cgst < Math.Min(columnCount, gst + 5); cgst++)
                {
                    for (var sgst = cgst + 1; sgst < Math.Min(columnCount, gst + 6); sgst++)
                    {
                        var score = 0d;
                        foreach (var row in rows)
                        {
                            if (!TryGridAmount(GridCell(row, gst), out var rate) || !IsLikelyGstRate(rate)) continue;
                            if (!TryGridAmount(GridCell(row, taxable), out var taxableValue) || taxableValue <= 0m) continue;
                            if (!TryGridAmount(GridCell(row, cgst), out var cgstValue) || cgstValue < 0m) continue;
                            if (!TryGridAmount(GridCell(row, sgst), out var sgstValue) || sgstValue < 0m) continue;
                            var expected = taxableValue * rate / 200m;
                            var tolerance = Math.Max(0.12m, Math.Abs(expected) * 0.008m);
                            if (Math.Abs(cgstValue - sgstValue) <= tolerance * 1.5m) score += 0.75d;
                            if (Math.Abs(cgstValue - expected) <= tolerance) score += 1.25d;
                            if (Math.Abs(sgstValue - expected) <= tolerance) score += 1.25d;
                        }
                        if (best is null || score > best.Score)
                            best = new TaxColumnTuple(taxable, gst, cgst, sgst, score);
                    }
                }
            }
        }
        return best;
    }

    private static int FindDescriptionColumn(IReadOnlyList<List<string>> rows, int hsnColumn, int gstColumn)
    {
        var bestColumn = -1;
        var bestScore = 0;
        for (var column = hsnColumn + 1; column < gstColumn; column++)
        {
            var score = rows.Count(row => GridCell(row, column).Any(char.IsLetter));
            if (score > bestScore) { bestScore = score; bestColumn = column; }
        }
        return bestColumn;
    }

    private static int FindNetColumn(IReadOnlyList<List<string>> rows, TaxColumnTuple tax, int columnCount)
    {
        var bestColumn = -1;
        var bestScore = 0d;
        for (var column = tax.SgstColumn + 1; column < columnCount; column++)
        {
            var score = 0d;
            foreach (var row in rows)
            {
                if (!TryGridAmount(GridCell(row, tax.TaxableColumn), out var taxable)) continue;
                if (!TryGridAmount(GridCell(row, tax.CgstColumn), out var cgst)) continue;
                if (!TryGridAmount(GridCell(row, tax.SgstColumn), out var sgst)) continue;
                if (!TryGridAmount(GridCell(row, column), out var net) || net <= 0m) continue;
                var expected = taxable + cgst + sgst;
                var tolerance = Math.Max(1.00m, expected * 0.03m);
                if (Math.Abs(net - expected) <= tolerance) score += 1d;
            }
            if (score >= bestScore) { bestScore = score; bestColumn = column; }
        }
        return bestColumn;
    }

    private static GridTableCandidate? BuildCanonicalGridSummaryTable(
        IReadOnlyList<List<string>> rows,
        GridRegion region,
        int pageWidth,
        int pageHeight)
    {
        if (rows.Count < 3 || rows.Count > 8 || region.Boundaries.Count < 7) return null;
        var labelColumn = FindSummaryLabelColumn(rows);
        if (labelColumn < 0) return null;
        var slabRows = rows.Where(row =>
        {
            var text = Normalize(string.Join(" ", row));
            return Has(text, "CGST") || Has(text, "SGST") || Has(text, "IGST");
        }).ToArray();
        if (slabRows.Length < 2) return null;

        var htmlRows = new List<IReadOnlyList<string>>
        {
            new[] { "Particulars", "Pcs", "Gross Amt", "Sch Amt", "RS Disc", "Taxable Amt", "Tax Amt", "Net Payable Amt" }
        };
        foreach (var row in slabRows)
            htmlRows.Add(CanonicalizeSummaryRow(row, labelColumn, forceTotal: false));

        var total = rows.LastOrDefault(row => Normalize(string.Join(" ", row)).StartsWith("TOTAL", StringComparison.Ordinal));
        if (total is null)
        {
            var lastSlab = -1;
            for (var index = 0; index < rows.Count; index++)
            {
                var text = Normalize(string.Join(" ", rows[index]));
                if (Has(text, "CGST") || Has(text, "SGST") || Has(text, "IGST")) lastSlab = index;
            }
            if (lastSlab >= 0 && lastSlab + 1 < rows.Count) total = rows[lastSlab + 1];
        }
        if (total is not null) htmlRows.Add(CanonicalizeSummaryRow(total, labelColumn, forceTotal: true));
        var score = 8d + slabRows.Length * 2d;
        var block = new LayoutOutputBlock("table", ToHtmlTable(htmlRows), region.Left, region.Top, region.Right, region.Bottom);
        return new GridTableCandidate(GridTableKind.Summary, score, block, region);
    }

    private static int FindSummaryLabelColumn(IReadOnlyList<List<string>> rows)
    {
        var columnCount = rows.Max(x => x.Count);
        var bestColumn = -1;
        var bestScore = 0;
        for (var column = 0; column < Math.Min(columnCount, 4); column++)
        {
            var score = rows.Count(row =>
            {
                var value = Normalize(GridCell(row, column));
                return Has(value, "CGST") || Has(value, "SGST") || Has(value, "IGST") || value.StartsWith("TOTAL", StringComparison.Ordinal);
            });
            if (score > bestScore) { bestScore = score; bestColumn = column; }
        }
        return bestScore >= 2 ? bestColumn : -1;
    }

    private static IReadOnlyList<string> CanonicalizeSummaryRow(IReadOnlyList<string> row, int labelColumn, bool forceTotal)
    {
        var label = forceTotal ? "Total" : GridCell(row, labelColumn);
        var numeric = row.Skip(labelColumn + 1)
            .SelectMany(cell => ExtractGridNumericTokens(cell))
            .ToList();
        while (numeric.Count < 7) numeric.Add(string.Empty);
        var pcs = numeric.Count > 0 ? numeric[0] : string.Empty;
        var gross = numeric.Count > 1 ? numeric[1] : string.Empty;
        var sch = numeric.Count > 2 ? numeric[2] : string.Empty;
        var rsDisc = numeric.Count > 3 ? numeric[3] : string.Empty;
        var taxable = numeric.Count > 4 ? numeric[4] : string.Empty;
        var tax = numeric.Count > 5 ? numeric[5] : string.Empty;
        var net = numeric.LastOrDefault(x => x.Length > 0) ?? string.Empty;
        return new[] { label, pcs, gross, sch, rsDisc, taxable, tax, net };
    }

    private static IEnumerable<string> ExtractGridNumericTokens(string value)
    {
        foreach (Match match in Regex.Matches(value ?? string.Empty, @"[-+]?\d[\d,]*(?:\.\d+)?", RegexOptions.IgnoreCase))
            yield return match.Value;
    }

    private static string GridCell(IReadOnlyList<string> row, int index) => index >= 0 && index < row.Count ? Collapse(row[index]) : string.Empty;

    private static bool TryGridAmount(string value, out decimal amount)
    {
        amount = 0m;
        var match = Regex.Match(value ?? string.Empty, @"[-+]?\d[\d,]*(?:\.\d+)?", RegexOptions.IgnoreCase);
        return match.Success && decimal.TryParse(match.Value.Replace(",", string.Empty), NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out amount);
    }

    private static bool IsLikelyGstRate(decimal value)
    {
        var supported = new[] { 0.1m, 0.25m, 1m, 1.5m, 3m, 5m, 6m, 7.5m, 12m, 18m, 28m, 40m };
        return supported.Any(rate => Math.Abs(rate - value) <= 0.08m);
    }

    private static string ToHtmlTable(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var builder = new StringBuilder("<table>");
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            builder.Append("<tr>");
            foreach (var cell in rows[rowIndex])
            {
                builder.Append(rowIndex == 0 ? "<th>" : "<td>");
                builder.Append(WebUtility.HtmlEncode(cell));
                builder.Append(rowIndex == 0 ? "</th>" : "</td>");
            }
            builder.Append("</tr>");
        }
        return builder.Append("</table>").ToString();
    }

    private static SpatialTableResult? BuildSpatialItemTableFallback(
        IReadOnlyList<OcrWordBox> words,
        int pageWidth,
        int pageHeight)
    {
        if (words.Count < 20 || pageWidth <= 0 || pageHeight <= 0) return null;
        var medianHeight = words.Select(x => x.Height).OrderBy(x => x).ElementAt(words.Count / 2);
        var anchors = words
            .Where(x => x.CenterY >= pageHeight * 0.20d && x.CenterY <= pageHeight * 0.80d)
            .Where(x => x.CenterX <= pageWidth * 0.46d)
            .Select(x => new { Word = x, Hsn = ExtractSpatialHsn(x.Text) })
            .Where(x => x.Hsn.Length >= 4)
            .OrderBy(x => x.Word.CenterY)
            .ThenBy(x => x.Word.X)
            .ToArray();
        if (anchors.Length < 2) return null;

        // De-duplicate accidental HSN-like tokens on the same visual baseline.  Prefer the longer
        // HSN/SAC candidate and the one nearer the usual left-side HSN area.
        var rowAnchors = new List<(OcrWordBox Word, string Hsn)>();
        foreach (var anchor in anchors)
        {
            var existingIndex = rowAnchors.FindIndex(x => Math.Abs(x.Word.CenterY - anchor.Word.CenterY) <= Math.Max(5d, medianHeight * 0.70d));
            if (existingIndex < 0)
            {
                rowAnchors.Add((anchor.Word, anchor.Hsn));
                continue;
            }
            var existing = rowAnchors[existingIndex];
            if (anchor.Hsn.Length > existing.Hsn.Length || (anchor.Hsn.Length == existing.Hsn.Length && anchor.Word.X < existing.Word.X))
                rowAnchors[existingIndex] = (anchor.Word, anchor.Hsn);
        }
        rowAnchors.Sort((a, b) => a.Word.CenterY.CompareTo(b.Word.CenterY));

        var provedRows = new List<SpatialInvoiceRow>();
        for (var index = 0; index < rowAnchors.Count; index++)
        {
            var anchor = rowAnchors[index];
            var previousCenter = index == 0 ? anchor.Word.CenterY - Math.Max(12d, medianHeight * 1.5d) : rowAnchors[index - 1].Word.CenterY;
            var nextCenter = index + 1 >= rowAnchors.Count ? anchor.Word.CenterY + Math.Max(12d, medianHeight * 1.5d) : rowAnchors[index + 1].Word.CenterY;
            var top = (int)Math.Floor((previousCenter + anchor.Word.CenterY) / 2d);
            var bottom = (int)Math.Ceiling((anchor.Word.CenterY + nextCenter) / 2d);
            var rowWords = words
                .Where(x => x.CenterY >= top && x.CenterY < bottom)
                .Where(x => x.X >= Math.Max(0, anchor.Word.X - pageWidth * 0.04d))
                .OrderBy(x => x.X).ThenBy(x => x.Y)
                .ToArray();
            if (rowWords.Length < 5) continue;
            var tax = FindSpatialTaxTuple(rowWords, anchor.Word);
            if (tax is null) continue;

            var description = Collapse(string.Join(" ", rowWords
                .Where(x => x.X >= anchor.Word.X)
                .Where(x => x.CenterX < tax.TaxableX)
                .Where(x => x != anchor.Word)
                .Select(x => x.Text)
                .Where(x => x.Any(char.IsLetter))
                .Where(x => !Regex.IsMatch(x, @"^(?:PCS?|CS|PKM|MRP|UPC|GST|CGST|SGST|UTGST|IGST)$", RegexOptions.IgnoreCase))));
            if (description.Length == 0)
            {
                var residual = Regex.Replace(anchor.Word.Text, @".*?\d{4,8}", string.Empty, RegexOptions.IgnoreCase).Trim();
                if (residual.Any(char.IsLetter)) description = residual;
            }

            provedRows.Add(new SpatialInvoiceRow(
                anchor.Hsn,
                description,
                tax.Taxable,
                tax.GstRate,
                tax.Cgst,
                tax.Sgst,
                tax.Igst,
                tax.Net,
                rowWords.Min(x => x.Y),
                rowWords.Max(x => x.Bottom),
                tax.Score));
        }

        // A real item table should contain repeated accounting rows.  Two isolated arithmetic
        // coincidences are not enough to promote a document region into ItemTable authority.
        if (provedRows.Count < 3) return null;
        var ordered = provedRows.OrderBy(x => x.Top).ToArray();
        var htmlRows = new List<IReadOnlyList<string>>
        {
            new[] { "HSN/SAC", "Product / Description", "Qty", "Unit", "Rate", "Discount", "Taxable", "GST %", "CGST", "SGST / UTGST", "IGST", "Cess", "Net Amt" }
        };
        foreach (var row in ordered)
        {
            htmlRows.Add(new[]
            {
                row.Hsn,
                row.Description,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                SpatialMoney(row.Taxable),
                row.GstRate.ToString("0.##", CultureInfo.InvariantCulture),
                row.Cgst > 0m ? SpatialMoney(row.Cgst) : string.Empty,
                row.Sgst > 0m ? SpatialMoney(row.Sgst) : string.Empty,
                row.Igst > 0m ? SpatialMoney(row.Igst) : string.Empty,
                string.Empty,
                row.Net > 0m ? SpatialMoney(row.Net) : string.Empty
            });
        }

        var left = Math.Max(0, words.Where(x => x.CenterY >= ordered[0].Top && x.CenterY <= ordered[^1].Bottom).Select(x => x.X).DefaultIfEmpty(0).Min());
        var right = Math.Min(pageWidth, words.Where(x => x.CenterY >= ordered[0].Top && x.CenterY <= ordered[^1].Bottom).Select(x => x.Right).DefaultIfEmpty(pageWidth).Max());
        var block = new LayoutOutputBlock("table-item-spatial", ToHtmlTable(htmlRows), left, ordered[0].Top, right, ordered[^1].Bottom);
        return new SpatialTableResult(block, ordered[0].Top, ordered[^1].Bottom, ordered.Length, ordered.Sum(x => x.Score));
    }

    private static SpatialTaxEvidence? FindSpatialTaxTuple(IReadOnlyList<OcrWordBox> rowWords, OcrWordBox hsnWord)
    {
        var numbers = new List<SpatialNumber>();
        foreach (var word in rowWords)
        {
            foreach (Match match in Regex.Matches(word.Text ?? string.Empty, @"[-+]?\d[\d,]*(?:\.\d+)?", RegexOptions.IgnoreCase))
            {
                if (!decimal.TryParse(match.Value.Replace(",", string.Empty), NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)) continue;
                value = Math.Abs(value);
                if (word == hsnWord && match.Value.Contains(ExtractSpatialHsn(hsnWord.Text), StringComparison.OrdinalIgnoreCase)) continue;
                numbers.Add(new SpatialNumber(value, word.X, word.Right, match.Value));
            }
        }
        numbers = numbers.OrderBy(x => x.X).ThenBy(x => x.Right).ToList();
        if (numbers.Count < 4) return null;

        SpatialTaxEvidence? best = null;
        for (var taxableIndex = 0; taxableIndex < numbers.Count; taxableIndex++)
        {
            var taxable = numbers[taxableIndex];
            if (taxable.Value < 5m) continue;
            for (var cgstIndex = taxableIndex + 1; cgstIndex < numbers.Count - 1; cgstIndex++)
            {
                var cgst = numbers[cgstIndex];
                if (cgst.Value <= 0m || cgst.Value >= taxable.Value * 0.40m) continue;
                for (var sgstIndex = cgstIndex + 1; sgstIndex < numbers.Count; sgstIndex++)
                {
                    var sgst = numbers[sgstIndex];
                    if (sgst.Value <= 0m || sgst.Value >= taxable.Value * 0.40m) continue;
                    var equalTolerance = Math.Max(0.12m, Math.Max(cgst.Value, sgst.Value) * 0.012m);
                    if (Math.Abs(cgst.Value - sgst.Value) > equalTolerance) continue;
                    var derivedRate = (cgst.Value + sgst.Value) * 100m / taxable.Value;
                    var snapped = SnapSpatialRate(derivedRate);
                    if (snapped <= 0m || Math.Abs(snapped - derivedRate) > 0.45m) continue;
                    var expectedHalf = taxable.Value * snapped / 200m;
                    var taxTolerance = Math.Max(0.15m, expectedHalf * 0.012m);
                    if (Math.Abs(cgst.Value - expectedHalf) > taxTolerance || Math.Abs(sgst.Value - expectedHalf) > taxTolerance) continue;

                    var score = 10d;
                    var printedRate = numbers.Skip(taxableIndex + 1).Take(Math.Max(0, cgstIndex - taxableIndex - 1))
                        .FirstOrDefault(x => Math.Abs(x.Value - snapped) <= 0.20m);
                    if (printedRate is not null) score += 3d;

                    var expectedNet = taxable.Value + cgst.Value + sgst.Value;
                    var net = numbers.Skip(sgstIndex + 1)
                        .Where(x => x.Value > 0m)
                        .Select(x => new { Number = x, Delta = Math.Abs(x.Value - expectedNet) })
                        .Where(x => x.Delta <= Math.Max(1.00m, expectedNet * 0.015m))
                        .OrderBy(x => x.Delta)
                        .FirstOrDefault();
                    if (net is not null) score += 4d;

                    var candidate = new SpatialTaxEvidence(
                        taxable.Value,
                        snapped,
                        cgst.Value,
                        sgst.Value,
                        0m,
                        net?.Number.Value ?? 0m,
                        taxable.X,
                        score);
                    if (best is null || candidate.Score > best.Score) best = candidate;
                }
            }

            // Inter-state fallback: Taxable -> printed GST% -> IGST -> Net.  Requiring a printed
            // supported rate prevents an arbitrary line amount from becoming IGST authority.
            for (var rateIndex = taxableIndex + 1; rateIndex < numbers.Count - 1; rateIndex++)
            {
                var rate = SnapSpatialRate(numbers[rateIndex].Value);
                if (rate <= 0m || Math.Abs(rate - numbers[rateIndex].Value) > 0.20m) continue;
                for (var igstIndex = rateIndex + 1; igstIndex < numbers.Count; igstIndex++)
                {
                    var igst = numbers[igstIndex];
                    var expected = taxable.Value * rate / 100m;
                    var tolerance = Math.Max(0.15m, expected * 0.012m);
                    if (Math.Abs(igst.Value - expected) > tolerance) continue;
                    var expectedNet = taxable.Value + igst.Value;
                    var net = numbers.Skip(igstIndex + 1)
                        .Select(x => new { Number = x, Delta = Math.Abs(x.Value - expectedNet) })
                        .Where(x => x.Delta <= Math.Max(1.00m, expectedNet * 0.015m))
                        .OrderBy(x => x.Delta)
                        .FirstOrDefault();
                    var score = 10d + (net is not null ? 4d : 0d);
                    var candidate = new SpatialTaxEvidence(taxable.Value, rate, 0m, 0m, igst.Value, net?.Number.Value ?? 0m, taxable.X, score);
                    if (best is null || candidate.Score > best.Score) best = candidate;
                }
            }
        }
        return best is not null && best.Score >= 10d ? best : null;
    }

    private static string ExtractSpatialHsn(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var matches = Regex.Matches(value.ToUpperInvariant(), @"(?<!\d)(\d{4,8})(?!\d)");
        if (matches.Count == 0) return string.Empty;
        return matches.Cast<Match>().Select(x => x.Groups[1].Value).OrderByDescending(x => x.Length).First();
    }

    private static decimal SnapSpatialRate(decimal value)
    {
        var supported = new[] { 0.1m, 0.25m, 1m, 1.5m, 3m, 5m, 6m, 7.5m, 12m, 18m, 28m, 40m };
        var nearest = supported.OrderBy(x => Math.Abs(x - value)).First();
        return Math.Abs(nearest - value) <= 0.50m ? nearest : 0m;
    }

    private static int CountTableDataRows(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return 0;
        var rows = Regex.Matches(html, @"<tr\b", RegexOptions.IgnoreCase).Count;
        return Math.Max(0, rows - 1);
    }

    private static string SpatialMoney(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private static bool LooksLikeItemRow(string text)
    {
        if (text.Length == 0) return false;
        if (Regex.IsMatch(text, @"\b\d{4,8}[A-Z]*\b", RegexOptions.IgnoreCase) && CountNumbers(text) >= 3) return true;
        return Regex.IsMatch(text, @"^\d{1,3}\s+\d{4,8}") && CountNumbers(text) >= 3;
    }

    private static int CountNumbers(string text) => Regex.Matches(text, @"(?<![A-Z])[-+]?\d[\d,]*(?:\.\d+)?", RegexOptions.IgnoreCase).Count;
    private static bool Has(string text, string token) => text.Contains(token, StringComparison.OrdinalIgnoreCase);
    private static bool TokenHas(OcrWordBox word, string token) => Normalize(word.Text).Contains(token, StringComparison.OrdinalIgnoreCase);
    private static string Normalize(string value) => Regex.Replace((value ?? string.Empty).ToUpperInvariant().Replace('/', ' ').Replace('-', ' '), @"\s+", " ").Trim();
    private static string Collapse(string value) => Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();

    private static async Task WriteStatusAsync(string path, string stage, string message, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(new { stage, message, updatedUtc = DateTime.UtcNow });
        await File.WriteAllTextAsync(path, json, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
    }

    private static async Task AppendLogAsync(string path, string message, CancellationToken cancellationToken)
    {
        await File.AppendAllTextAsync(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}", new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
    }

    private sealed class OcrVisualRow
    {
        public List<OcrWordBox> Words { get; } = [];
        public int Left { get; private set; }
        public int Top { get; private set; }
        public int Right { get; private set; }
        public int Bottom { get; private set; }
        public int Height => Math.Max(1, Bottom - Top);
        public double CenterY => Top + Height / 2d;
        public string Text => Collapse(string.Join(" ", Words.OrderBy(x => x.X).Select(x => x.Text)));
        public void Recalculate()
        {
            Left = Words.Min(x => x.X); Top = Words.Min(x => x.Y); Right = Words.Max(x => x.Right); Bottom = Words.Max(x => x.Bottom);
        }
    }

    private sealed record OcrWordBox(string Text, int X, int Y, int Width, int Height)
    {
        public int Right => X + Width;
        public int Bottom => Y + Height;
        public double CenterX => X + Width / 2d;
        public double CenterY => Y + Height / 2d;
    }

    private sealed class GridBoundaryCluster
    {
        public List<int> Values { get; } = [];
        public HashSet<int> Bands { get; } = [];
        public double Mean => Values.Count == 0 ? 0d : Values.Average();
    }

    private sealed record GridBand(int Top, int Bottom, IReadOnlyList<int> Boundaries);

    private sealed record GridRegion(int Top, int Bottom, IReadOnlyList<int> Boundaries, IReadOnlyList<GridBand> Bands)
    {
        public int Left => Boundaries.Count == 0 ? 0 : Boundaries[0];
        public int Right => Boundaries.Count == 0 ? 0 : Boundaries[^1];
        public GridRegion Scale(double scaleX, double scaleY)
        {
            var scaledBands = Bands.Select(band => new GridBand(
                (int)Math.Round(band.Top * scaleY),
                (int)Math.Round(band.Bottom * scaleY),
                band.Boundaries.Select(x => (int)Math.Round(x * scaleX)).ToArray())).ToArray();
            return new GridRegion(
                (int)Math.Round(Top * scaleY),
                (int)Math.Round(Bottom * scaleY),
                Boundaries.Select(x => (int)Math.Round(x * scaleX)).ToArray(),
                scaledBands);
        }
    }

    private sealed record RuledGridDetection(IReadOnlyList<GridRegion> Regions)
    {
        public static RuledGridDetection Empty { get; } = new(Array.Empty<GridRegion>());
        public RuledGridDetection Scale(double scaleX, double scaleY) => new(Regions.Select(region => region.Scale(scaleX, scaleY)).ToArray());
    }

    private enum GridTableKind { Item, Summary }
    private sealed record GridTableCandidate(GridTableKind Kind, double Score, LayoutOutputBlock Block, GridRegion Region);
    private sealed record TaxColumnTuple(int TaxableColumn, int GstColumn, int CgstColumn, int SgstColumn, double Score);

    private sealed record SpatialNumber(decimal Value, int X, int Right, string Raw);
    private sealed record SpatialTaxEvidence(decimal Taxable, decimal GstRate, decimal Cgst, decimal Sgst, decimal Igst, decimal Net, int TaxableX, double Score);
    private sealed record SpatialInvoiceRow(string Hsn, string Description, decimal Taxable, decimal GstRate, decimal Cgst, decimal Sgst, decimal Igst, decimal Net, int Top, int Bottom, double Score);
    private sealed record SpatialTableResult(LayoutOutputBlock Block, int Top, int Bottom, int RowCount, double Score);

    private sealed record LayoutOutputBlock(string Label, string Content, int X1, int Y1, int X2, int Y2);
    private sealed record HeaderWindow(int Start, int End, int Score)
    {
        public int Length => End - Start + 1;
    }
    private sealed record TableRange(int Start, int End, HeaderWindow Header);
    private sealed record ColumnAnchor(string Name, double X, bool Optional, bool Observed);
}
