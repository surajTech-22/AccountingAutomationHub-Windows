using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AccountingAutomationHub.Domain.Clients;

namespace AccountingAutomationHub.Desktop.Services;

/// <summary>
/// Linked Phone Link / Universal Dump bridge to InvoiceVisionChild v2
/// (LightOnOCR-2-1B primary + Qwen3-VL-4B math-gated escalation).
/// AAH creates requests and keeps all database / Tally authority inside AAH.
/// </summary>
internal static class InvoiceOcrChildBridge
{
    public const bool IsLinked = true;
    public const string InstalledExe = @"C:\AccountingAutomationHub\InvoiceVisionChild\InvoiceVisionChild.exe";

    // Serialize heavy VLM extractions on CPU-only PCs so concurrent phone photos queue cleanly.
    private static readonly SemaphoreSlim ExtractionQueue = new(1, 1);
    private static readonly TimeSpan ExtractionTimeout = TimeSpan.FromMinutes(12);

    internal sealed class Status
    {
        public bool Stage1Succeeded { get; init; }
        public string Message { get; init; } = "";
        public string Stage1Archive { get; init; } = "";
        public string RequestId { get; init; } = "";
    }

    public static string? ResolveExecutable()
    {
        var candidates = new[]
        {
            InstalledExe,
            Path.Combine(AppContext.BaseDirectory, "InvoiceVisionChild", "InvoiceVisionChild.exe")
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    public static bool IsChildInstalled() => ResolveExecutable() is not null;

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public static bool OpenReview(
        IReadOnlyList<string> paths,
        long clientId,
        string financialYear,
        string scanId,
        string resultJsonPath,
        string evidenceFolder,
        string? clientGstin = null,
        string? clientName = null)
    {
        var exe = ResolveExecutable()
            ?? throw new FileNotFoundException("InvoiceVision Child v2 is not installed", InstalledExe);
        if (paths.Count == 0 || !File.Exists(paths[0]))
            throw new FileNotFoundException("Original phone invoice image is missing.");

        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(exe)!
        };
        foreach (var path in paths.Where(File.Exists))
        {
            start.ArgumentList.Add("--source");
            start.ArgumentList.Add(Path.GetFullPath(path));
        }
        start.ArgumentList.Add("--client-id");
        start.ArgumentList.Add(clientId.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--fy");
        start.ArgumentList.Add(financialYear);
        if (!string.IsNullOrWhiteSpace(clientGstin))
        {
            start.ArgumentList.Add("--client-gstin");
            start.ArgumentList.Add(clientGstin);
        }
        if (!string.IsNullOrWhiteSpace(clientName))
        {
            start.ArgumentList.Add("--client-name");
            start.ArgumentList.Add(clientName);
        }
        start.ArgumentList.Add("--scan-id");
        start.ArgumentList.Add(scanId);

        var hasVerifiedResult = !string.IsNullOrWhiteSpace(resultJsonPath) && File.Exists(resultJsonPath);
        if (hasVerifiedResult)
        {
            start.ArgumentList.Add("--result");
            start.ArgumentList.Add(Path.GetFullPath(resultJsonPath));
        }
        if (Process.Start(start) is null)
            throw new IOException("InvoiceVision Child v2 process failed to start.");
        return hasVerifiedResult;
    }

    public static async Task<(Status ChildStatus, MobileInvoiceDocumentOcrResult? DocumentResult)> ExtractForPhoneLinkAsync(
        IReadOnlyList<string> paths,
        ClientProfile client,
        string scanId,
        string evidenceFolder,
        string originalPayloadJson,
        string phoneRawOcr,
        Action<string>? progress,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? cachedPageOcrTexts = null)
    {
        var exe = ResolveExecutable();
        if (exe is null)
        {
            return (new Status { Message = "InvoiceVision Child v2 not found; using AAH ONNX OCR fallback." }, null);
        }

        var usablePaths = paths.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).Take(20).ToArray();
        if (usablePaths.Length == 0)
        {
            return (new Status { Message = "Original Phone Link photo is missing; InvoiceVision skipped." }, null);
        }

        await ExtractionQueue.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var runDir = Path.Combine(evidenceFolder, "ChildHandoff");
            Directory.CreateDirectory(runDir);
            var requestId = Guid.NewGuid().ToString("N");
            var requestPath = Path.Combine(runDir, "request.json");
            var resultPath = Path.Combine(runDir, "result.json");
            var workDir = Path.Combine(runDir, "Work", requestId);
            Directory.CreateDirectory(workDir);

            var preHashes = usablePaths.Select(p => new { Path = Path.GetFullPath(p), Sha256 = HashFile(p) }).ToArray();
            var request = new
            {
                schema = "AAH-INVOICEVISION-REQUEST-V1",
                requestId,
                clientId = client.Id,
                clientCode = client.ClientCode ?? string.Empty,
                clientName = client.ClientName ?? string.Empty,
                clientGstin = client.Gstin ?? string.Empty,
                financialYear = client.FinancialYear,
                directionHint = "Auto",
                sourceFiles = preHashes.Select(x => x.Path).ToArray(),
                outputPath = resultPath,
                scanId,
                workDirectory = workDir,
                mode = "hybrid",
                memoryDbPath = MobileSupplierMemoryStore.DatabasePath,
                cachedPageOcrTexts = (cachedPageOcrTexts is not null && cachedPageOcrTexts.Count == usablePaths.Length)
                    ? cachedPageOcrTexts.ToArray()
                    : Array.Empty<string>()
            };

            var tempReq = requestPath + ".tmp";
            await File.WriteAllTextAsync(tempReq, JsonSerializer.Serialize(request, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            File.Move(tempReq, requestPath, overwrite: true);
            if (File.Exists(resultPath)) File.Delete(resultPath);

            progress?.Invoke("InvoiceVision v2 • Queued local LightOnOCR-2-1B / Qwen3-VL-4B extraction...");

            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(exe)!
            };
            psi.ArgumentList.Add("--extract");
            psi.ArgumentList.Add(requestPath);

            using var proc = new Process { StartInfo = psi };
            if (!proc.Start())
            {
                return (new Status { RequestId = requestId, Message = "Could not start InvoiceVision Child v2." }, null);
            }

            var timeout = TimeSpan.FromMinutes(Math.Clamp(usablePaths.Length * 5, 12, 45));
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);

            var errTask = proc.StandardError.ReadToEndAsync(timeoutCts.Token);
            var outTask = Task.Run(async () =>
            {
                string? line;
                while ((line = await proc.StandardOutput.ReadLineAsync(timeoutCts.Token).ConfigureAwait(false)) is not null)
                {
                    if (line.StartsWith("PROGRESS|", StringComparison.Ordinal))
                    {
                        var parts = line.Split('|', 3);
                        if (parts.Length == 3 && !string.IsNullOrWhiteSpace(parts[2]))
                            progress?.Invoke("InvoiceVision v2 · " + parts[2]);
                    }
                }
            }, timeoutCts.Token);

            try
            {
                await Task.WhenAll(outTask, errTask, proc.WaitForExitAsync(timeoutCts.Token)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
                if (cancellationToken.IsCancellationRequested) throw;
                return (new Status { RequestId = requestId, Message = $"InvoiceVision Child v2 timed out ({timeout.TotalMinutes:0}m); using AAH ONNX OCR fallback." }, null);
            }

            var stderr = await errTask.ConfigureAwait(false);
            if (proc.ExitCode != 0 || !File.Exists(resultPath))
            {
                var detail = stderr.Trim();
                if (detail.Length > 280) detail = detail[^280..];
                return (new Status { RequestId = requestId, Message = $"InvoiceVision Child v2 failed (exit {proc.ExitCode}): {detail}" }, null);
            }

            // Re-verify immutable source photograph SHA-256 hashes after extraction
            foreach (var src in preHashes)
            {
                if (!HashFile(src.Path).Equals(src.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Source photograph changed during InvoiceVision extraction.");
            }

            var resultJson = await File.ReadAllTextAsync(resultPath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            var converted = ConvertResultJsonToMobileOcrResult(resultJson, client, originalPayloadJson, preHashes.Select(x => x.Sha256).ToArray(), resultPath);

            var status = new Status
            {
                Stage1Succeeded = true,
                Stage1Archive = resultPath,
                RequestId = requestId,
                Message = converted.Authority
            };
            return (status, converted);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (new Status { Message = "InvoiceVision Child v2 error: " + ex.Message + "; fallback active." }, null);
        }
        finally
        {
            ExtractionQueue.Release();
        }
    }

    private static MobileInvoiceDocumentOcrResult ConvertResultJsonToMobileOcrResult(
        string resultJson,
        ClientProfile client,
        string originalPayloadJson,
        IReadOnlyList<string> sourceSha256List,
        string resultPath)
    {
        var primarySourceSha256 = sourceSha256List.FirstOrDefault() ?? string.Empty;
        using var doc = JsonDocument.Parse(resultJson);
        var src = doc.RootElement;
        if (src.TryGetProperty("status", out var st) && string.Equals(st.GetString(), "Failed", StringComparison.OrdinalIgnoreCase))
        {
            var note = src.TryGetProperty("diagnosticsNote", out var dn) ? dn.GetString() ?? "Extraction failed" : "Extraction failed";
            return MobileInvoiceDocumentOcrResult.Failed(note);
        }

        static string S(JsonElement obj, string prop, string fallback = "") =>
            obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(prop, out var v) && v.ValueKind != JsonValueKind.Null
                ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : v.ToString()).Trim()
                : fallback;
        static decimal D(JsonElement obj, string prop)
        {
            if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(prop, out var v)) return 0m;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var dec)) return dec;
            if (v.ValueKind == JsonValueKind.String && decimal.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out dec)) return dec;
            return 0m;
        }
        static string Money(decimal value) => Math.Abs(value).ToString("0.00", CultureInfo.InvariantCulture);
        static string SignedMoney(decimal value) => value.ToString("0.00;-0.00;0.00", CultureInfo.InvariantCulture);
        static string NormalizeDateForAah(string isoOrRaw)
        {
            foreach (var fmt in new[] { "yyyy-MM-dd", "dd-MM-yyyy", "dd/MM/yyyy" })
                if (DateTime.TryParseExact(isoOrRaw, fmt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                    return parsed.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture);
            return isoOrRaw;
        }

        var sellerEl = src.TryGetProperty("seller", out var se) ? se : default;
        var buyerEl = src.TryGetProperty("buyer", out var be) ? be : default;
        var sellerName = S(sellerEl, "name");
        var sellerGstin = MobileInvoicePartyIdentityResolver.NormalizeGstin(S(sellerEl, "gstin"));
        var buyerName = S(buyerEl, "name");
        var buyerGstin = MobileInvoicePartyIdentityResolver.NormalizeGstin(S(buyerEl, "gstin"));
        var clientGstin = MobileInvoicePartyIdentityResolver.NormalizeGstin(client.Gstin ?? string.Empty);

        var identity = MobileInvoicePartyIdentityResolver.Select(clientGstin, sellerName, sellerGstin, buyerName, buyerGstin, S(src, "direction"));
        var direction = identity.Direction;
        var counterpartyName = identity.CounterpartyName;
        var counterpartyGstin = identity.CounterpartyGstin;

        var invoiceNumber = S(src, "invoiceNumber");
        var invoiceDate = NormalizeDateForAah(S(src, "invoiceDate"));
        var documentType = S(src, "documentType", "Tax Invoice");
        var placeOfSupply = S(src, "placeOfSupply");
        var taxableTotal = D(src, "taxableTotal");
        var discountTotal = D(src, "discountTotal");
        var cgstTotal = D(src, "cgstTotal");
        var sgstTotal = D(src, "sgstTotal");
        var igstTotal = D(src, "igstTotal");
        var cessTotal = D(src, "cessTotal");
        var creditNoteTotal = D(src, "creditNoteTotal");
        var roundOff = D(src, "roundOff");
        var invoiceTotal = D(src, "invoiceTotal");
        var engineUsed = S(src, "engineUsed", "LightOnOCR-2-1B");
        var elapsed = src.TryGetProperty("elapsedSeconds", out var elSec) && elSec.TryGetDouble(out var eVal) ? eVal : 0.0;
        var memoryApplied = src.TryGetProperty("supplierMemory", out var memEl) && memEl.ValueKind == JsonValueKind.Object &&
                            memEl.TryGetProperty("profileFound", out var pfEl) && pfEl.ValueKind == JsonValueKind.True;
        var preferredModeFromMemory = memEl.ValueKind == JsonValueKind.Object ? S(memEl, "preferredRecognitionMode") : string.Empty;

        JsonObject root;
        try { root = (JsonNode.Parse(originalPayloadJson) as JsonObject)?.DeepClone().AsObject() ?? new JsonObject(); }
        catch (JsonException) { root = new JsonObject(); }

        root["clientId"] = client.Id;
        root["financialYear"] = client.FinancialYear;
        root["direction"] = direction;
        root["directionConfidence"] = identity.ClientIdentityMatched ? 100 : 50;
        root["seller"] = new JsonObject { ["name"] = sellerName, ["gstin"] = sellerGstin, ["pan"] = MobileInvoicePartyIdentityResolver.PanFromGstin(sellerGstin) };
        root["buyer"] = new JsonObject { ["name"] = buyerName, ["gstin"] = buyerGstin, ["pan"] = MobileInvoicePartyIdentityResolver.PanFromGstin(buyerGstin) };
        root["counterparty"] = new JsonObject { ["name"] = counterpartyName, ["gstin"] = counterpartyGstin, ["pan"] = identity.CounterpartyPan };
        root["documentType"] = documentType;
        root["invoiceNumber"] = invoiceNumber;
        root["invoiceDate"] = invoiceDate;
        root["placeOfSupply"] = placeOfSupply;
        root["taxableTotal"] = Money(taxableTotal);
        root["discountTotal"] = discountTotal > 0m ? Money(discountTotal) : string.Empty;
        root["cgstTotal"] = Money(cgstTotal);
        root["sgstTotal"] = Money(sgstTotal);
        root["igstTotal"] = Money(igstTotal);
        root["cessTotal"] = cessTotal > 0m ? Money(cessTotal) : string.Empty;
        root["otherCharges"] = string.Empty;
        root["roundOff"] = SignedMoney(roundOff);
        root["creditNoteTotal"] = creditNoteTotal > 0m ? Money(creditNoteTotal) : string.Empty;
        root["invoiceTotal"] = Money(invoiceTotal);
        root["summaryLayoutAuthority"] = true;
        root["documentOcrAuthority"] = $"InvoiceVision v2 ({engineUsed}) · {elapsed:0.0}s" + (sourceSha256List.Count > 1 ? $" · {sourceSha256List.Count} pages" : "");
        root["ocrEngine"] = "AAH-INVOICEVISION-V2";
        root["ocrSourceSha256"] = primarySourceSha256;
        root["ocrSourceSha256List"] = new JsonArray(sourceSha256List.Select(h => (JsonNode?)JsonValue.Create(h)).ToArray());
        root["ocrSourcePageCount"] = sourceSha256List.Count;
        root["invoiceVisionResultPath"] = resultPath;
        if (memEl.ValueKind == JsonValueKind.Object)
        {
            try { root["supplierMemory"] = JsonNode.Parse(memEl.GetRawText()); } catch { }
        }
        var currentMode = root["recognitionMode"]?.ToString().Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(currentMode) && !string.IsNullOrWhiteSpace(preferredModeFromMemory))
            root["recognitionMode"] = preferredModeFromMemory;

        var rows = new JsonArray();
        if (src.TryGetProperty("rows", out var rowsEl) && rowsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in rowsEl.EnumerateArray())
            {
                var qty = D(r, "quantity");
                var rate = D(r, "rate");
                var disc = D(r, "discount");
                var tx = D(r, "taxable");
                var gstRate = D(r, "gstRate");
                var cg = D(r, "cgst");
                var sg = D(r, "sgst");
                var ig = D(r, "igst");
                var cs = D(r, "cess");
                var lt = D(r, "lineTotal");
                rows.Add(new JsonObject
                {
                    ["description"] = S(r, "description"),
                    ["hsnSac"] = S(r, "hsnSac"),
                    ["quantity"] = qty > 0m ? qty.ToString("0.###", CultureInfo.InvariantCulture) : string.Empty,
                    ["unit"] = S(r, "unit"),
                    ["rate"] = rate > 0m ? Money(rate) : string.Empty,
                    ["discount"] = disc > 0m ? Money(disc) : string.Empty,
                    ["taxable"] = Money(tx),
                    ["gstRate"] = gstRate.ToString("0.##", CultureInfo.InvariantCulture),
                    ["cgst"] = Money(cg),
                    ["sgst"] = Money(sg),
                    ["igst"] = Money(ig),
                    ["cess"] = cs > 0m ? Money(cs) : string.Empty,
                    ["lineTotal"] = Money(lt),
                    ["stockItem"] = string.Empty,
                    ["ledger"] = string.Empty,
                    ["sourceText"] = S(r, "description"),
                    ["derivation"] = "InvoiceVision v2 (" + engineUsed + ")"
                });
            }
        }
        root["rows"] = rows;

        var rates = new JsonArray();
        var slabs = new JsonArray();
        if (src.TryGetProperty("gstSlabs", out var slabsEl) && slabsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in slabsEl.EnumerateArray())
            {
                var rateVal = D(s, "gstRate");
                var slabTx = D(s, "taxable");
                if (rateVal <= 0m || slabTx <= 0m) continue;
                var rateStr = rateVal.ToString("0.##", CultureInfo.InvariantCulture);
                rates.Add(JsonValue.Create(rateStr));
                slabs.Add(new JsonObject
                {
                    ["gstRate"] = rateStr,
                    ["taxable"] = Money(slabTx),
                    ["cgst"] = Money(D(s, "cgst")),
                    ["sgst"] = Money(D(s, "sgst")),
                    ["igst"] = Money(D(s, "igst"))
                });
            }
        }
        root["gstSlabRates"] = rates;
        root["gstSlabs"] = slabs;

        var blockingFlags = new List<string>();
        var clientIsSeller = clientGstin.Length == 15 && MobileInvoicePartyIdentityResolver.ValidGstin(sellerGstin) && clientGstin == sellerGstin;
        var clientIsBuyer = clientGstin.Length == 15 && MobileInvoicePartyIdentityResolver.ValidGstin(buyerGstin) && clientGstin == buyerGstin;
        if (clientIsSeller == clientIsBuyer)
            blockingFlags.Add("CLIENT_GSTIN_DIRECTION_REQUIRES_MANUAL_VERIFICATION");
        if (creditNoteTotal > 0m)
            blockingFlags.Add($"CREDIT_NOTE_DEDUCTION_{creditNoteTotal:0.00}_REVIEW_REQUIRED");

        if (src.TryGetProperty("verification", out var verEl))
        {
            if (verEl.TryGetProperty("errors", out var errArr) && errArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in errArr.EnumerateArray())
                    if (!string.IsNullOrWhiteSpace(e.GetString())) blockingFlags.Add(e.GetString()!);
            }
        }

        root["ocrBlockingFlags"] = new JsonArray(blockingFlags.Distinct(StringComparer.OrdinalIgnoreCase).Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
        root["ocrRequiresExplicitReconciliation"] = blockingFlags.Count > 0;
        root["ocrValidatedImportAllowed"] = false;

        var rawDisplay = new StringBuilder();
        rawDisplay.AppendLine($"INVOICEVISION V2 EXTRACTION ({engineUsed} · {elapsed:0.0}s · {sourceSha256List.Count} page(s))");
        rawDisplay.AppendLine("Source SHA-256: " + primarySourceSha256);
        if (sourceSha256List.Count > 1)
        {
            for (var pIdx = 0; pIdx < sourceSha256List.Count; pIdx++)
                rawDisplay.AppendLine($"  Page {pIdx + 1} SHA-256: {sourceSha256List[pIdx]}");
        }
        rawDisplay.AppendLine("Seller: " + sellerName + " / " + sellerGstin);
        rawDisplay.AppendLine("Buyer: " + buyerName + " / " + buyerGstin);
        rawDisplay.AppendLine($"Invoice: {invoiceNumber} / {invoiceDate} / Total {invoiceTotal:N2}");
        if (blockingFlags.Count > 0)
            rawDisplay.AppendLine("Review notes: " + string.Join("; ", blockingFlags.Distinct()));
        rawDisplay.AppendLine();
        rawDisplay.AppendLine(S(src, "rawOcrText"));

        return new MobileInvoiceDocumentOcrResult
        {
            WasSuccessful = true,
            PayloadJson = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            RawOcrText = rawDisplay.ToString(),
            CandidatesJson = "{}",
            CounterpartyName = counterpartyName,
            CounterpartyGstin = counterpartyGstin,
            InvoiceNumber = invoiceNumber,
            InvoiceDate = invoiceDate,
            Direction = direction,
            RecognitionMode = root["recognitionMode"]?.ToString() ?? "Accounting Invoice",
            InvoiceTotal = Money(invoiceTotal),
            TableCount = slabs.Count > 0 ? 2 : 1,
            ItemRowCount = rows.Count,
            SummarySlabCount = slabs.Count,
            Authority = $"InvoiceVision v2 ({engineUsed} · {elapsed:0.0}s · {(sourceSha256List.Count > 1 ? sourceSha256List.Count + " pages · " : "")}{rows.Count} row(s), {(blockingFlags.Count == 0 ? "math balanced" : blockingFlags.Count + " review note(s)")}{(memoryApplied ? " · AI memory" : "")})"
        };
    }

    internal sealed class MobileInvoiceAiAnalysisResult
    {
        public string Verdict { get; init; } = string.Empty;
        public string Summary { get; init; } = string.Empty;
        public string PostingPreview { get; init; } = string.Empty;
        public string SupplierMemoryStatus { get; init; } = string.Empty;
        public List<string> Findings { get; init; } = [];
        public List<string> Recommendations { get; init; } = [];
        public string LlmExplanation { get; init; } = string.Empty;
        public string LlmEngineUsed { get; init; } = string.Empty;
        public double ElapsedSec { get; init; }
    }

    public static async Task<MobileInvoiceAiAnalysisResult> AnalyzeForPhoneLinkAsync(
        MobileInvoiceQueueItem scan,
        MobileInvoiceMappedResult mapped,
        ClientProfile client,
        string sellerName,
        string sellerGstin,
        string buyerName,
        string buyerGstin,
        bool useLlm,
        CancellationToken cancellationToken)
    {
        var exe = ResolveExecutable()
            ?? throw new FileNotFoundException("InvoiceVision Child v2 is not installed", InstalledExe);

        var runDir = Path.Combine(
            string.IsNullOrWhiteSpace(scan.EvidenceFolder) ? Path.GetTempPath() : scan.EvidenceFolder,
            "ChildHandoff");
        Directory.CreateDirectory(runDir);
        var reqPath = Path.Combine(runDir, "analyze_request.json");
        var outPath = Path.Combine(runDir, "analyze_result.json");
        if (File.Exists(outPath)) File.Delete(outPath);

        var blockingFlags = new List<string>();
        try
        {
            using var pDoc = JsonDocument.Parse(scan.PayloadJson);
            if (pDoc.RootElement.TryGetProperty("ocrBlockingFlags", out var bf) && bf.ValueKind == JsonValueKind.Array)
                blockingFlags.AddRange(bf.EnumerateArray().Select(x => x.GetString() ?? string.Empty).Where(s => s.Length > 0));
        }
        catch { }

        var payload = new
        {
            schema = "AAH-INVOICEVISION-ANALYSIS-REQUEST-V1",
            requestId = Guid.NewGuid().ToString("N"),
            invoiceNumber = mapped.InvoiceNumber,
            invoiceDate = mapped.InvoiceDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? scan.InvoiceDate,
            direction = mapped.Direction,
            recognitionMode = mapped.RecognitionMode,
            clientGstin = client.Gstin ?? string.Empty,
            sellerName,
            sellerGstin,
            buyerName,
            buyerGstin,
            taxableTotal = mapped.TaxableTotal,
            cgstTotal = mapped.CgstTotal,
            sgstTotal = mapped.SgstTotal,
            igstTotal = mapped.IgstTotal,
            cessTotal = mapped.CessTotal,
            roundOff = mapped.RoundOff,
            invoiceTotal = mapped.InvoiceTotal,
            rows = mapped.Lines.Select(l => new
            {
                description = l.Description,
                hsnSac = l.HsnSac,
                quantity = l.Quantity,
                unit = l.Unit,
                rate = l.Rate,
                taxable = l.Taxable,
                gstRate = l.GstRate,
                cgst = l.Cgst,
                sgst = l.Sgst,
                igst = l.Igst,
                lineTotal = l.LineTotal
            }).ToArray(),
            gstSlabs = mapped.Lines
                .GroupBy(l => Math.Round(l.GstRate, 2))
                .Select(g => new
                {
                    gstRate = g.Key,
                    taxable = g.Sum(x => x.Taxable),
                    cgst = g.Sum(x => x.Cgst),
                    sgst = g.Sum(x => x.Sgst),
                    igst = g.Sum(x => x.Igst)
                }).ToArray(),
            rawOcrText = scan.RawOcr ?? string.Empty,
            blockingFlags,
            memoryDbPath = MobileSupplierMemoryStore.DatabasePath,
            useLlm,
            outputPath = outPath
        };

        await File.WriteAllTextAsync(reqPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);

        await ExtractionQueue.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(exe)!
            };
            psi.ArgumentList.Add("--analyze");
            psi.ArgumentList.Add(reqPath);

            using var proc = new Process { StartInfo = psi };
            if (!proc.Start())
                throw new IOException("InvoiceVision Child v2 failed to start for analysis.");

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(130));
            var outTask = proc.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var errTask = proc.StandardError.ReadToEndAsync(timeoutCts.Token);
            try
            {
                await Task.WhenAll(outTask, errTask, proc.WaitForExitAsync(timeoutCts.Token)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
                throw;
            }

            var stderr = await errTask.ConfigureAwait(false);
            if (proc.ExitCode != 0 || !File.Exists(outPath))
                throw new InvalidOperationException($"InvoiceVision AI analysis failed (exit {proc.ExitCode}): {stderr}");

            var resJson = await File.ReadAllTextAsync(outPath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            using var resDoc = JsonDocument.Parse(resJson);
            var r = resDoc.RootElement;
            static string GS(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;
            static List<string> GA(JsonElement e, string n) => e.TryGetProperty(n, out var a) && a.ValueKind == JsonValueKind.Array
                ? a.EnumerateArray().Select(x => x.GetString() ?? string.Empty).Where(s => s.Length > 0).ToList()
                : [];

            return new MobileInvoiceAiAnalysisResult
            {
                Verdict = GS(r, "verdict"),
                Summary = GS(r, "summary"),
                PostingPreview = GS(r, "postingPreview"),
                SupplierMemoryStatus = GS(r, "supplierMemoryStatus"),
                Findings = GA(r, "findings"),
                Recommendations = GA(r, "recommendations"),
                LlmExplanation = GS(r, "llmExplanation"),
                LlmEngineUsed = GS(r, "llmEngineUsed"),
                ElapsedSec = r.TryGetProperty("elapsedSec", out var el) && el.TryGetDouble(out var ev) ? ev : 0.0
            };
        }
        finally
        {
            ExtractionQueue.Release();
        }
    }

    // Legacy Stage-1 entry kept compiling while routing directly through v2 extraction
    public static async Task<Status> TryStage1Async(IReadOnlyList<string> paths, long clientId, string financialYear,
        string scanId, string evidenceFolder, CancellationToken cancellationToken)
    {
        var (status, _) = await ExtractForPhoneLinkAsync(paths,
            new ClientProfile { Id = clientId, FinancialYear = financialYear },
            scanId, evidenceFolder, "{}", "", null, cancellationToken).ConfigureAwait(false);
        return status;
    }
}
