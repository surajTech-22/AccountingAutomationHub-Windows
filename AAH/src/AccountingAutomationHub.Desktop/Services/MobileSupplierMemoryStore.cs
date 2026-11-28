using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using AccountingAutomationHub.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace AccountingAutomationHub.Desktop.Services;

/// <summary>
/// Shared offline self-learning SQLite supplier memory for Mobile Link &amp; InvoiceVisionChild v2.
/// Learns in 1 shot from every user confirmation or explicit "Teach AI" action:
/// canonical supplier name, OCR aliases, preferred direction/mode, HSN-&gt;GST rate map, and few-shot JSON.
/// </summary>
internal static class MobileSupplierMemoryStore
{
    private static readonly decimal[] SupportedGstRates = [0m, 0.1m, 0.25m, 1m, 1.5m, 3m, 5m, 6m, 7.5m, 12m, 18m, 28m, 40m];
    private static readonly object Gate = new();

    public static string DatabasePath => Path.Combine(ApplicationDataPaths.RootFolder, "MobileLink", "supplier_memory.db");

    public sealed record SupplierMemoryInfo(
        bool Found,
        string Gstin,
        string CanonicalName,
        int ConfirmedCount,
        int HsnRateCount,
        string PreferredDirection,
        string PreferredRecognitionMode,
        string LastConfirmedUtc);

    private static SqliteConnection OpenConnection()
    {
        var dbPath = DatabasePath;
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        var conn = new SqliteConnection($"Data Source={dbPath};Pooling=False;");
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
        pragma.ExecuteNonQuery();
        EnsureSchema(conn);
        return conn;
    }

    private static void EnsureSchema(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS supplier_profiles (
                gstin TEXT PRIMARY KEY,
                canonical_name TEXT NOT NULL DEFAULT '',
                aliases_json TEXT NOT NULL DEFAULT '[]',
                preferred_direction TEXT NOT NULL DEFAULT '',
                preferred_recognition_mode TEXT NOT NULL DEFAULT '',
                tax_inclusive_hint INTEGER NOT NULL DEFAULT 0,
                confirmed_count INTEGER NOT NULL DEFAULT 0,
                last_confirmed_utc TEXT NOT NULL DEFAULT '',
                best_example_json TEXT NOT NULL DEFAULT '{}'
            );
            CREATE TABLE IF NOT EXISTS supplier_hsn_rates (
                gstin TEXT NOT NULL,
                hsn_code TEXT NOT NULL,
                gst_rate REAL NOT NULL,
                sample_description TEXT NOT NULL DEFAULT '',
                hit_count INTEGER NOT NULL DEFAULT 1,
                updated_utc TEXT NOT NULL DEFAULT '',
                PRIMARY KEY (gstin, hsn_code)
            );
            CREATE TABLE IF NOT EXISTS supplier_corrections (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                gstin TEXT NOT NULL,
                invoice_number TEXT NOT NULL DEFAULT '',
                field_diffs_json TEXT NOT NULL DEFAULT '{}',
                learned_utc TEXT NOT NULL DEFAULT ''
            );
            CREATE TABLE IF NOT EXISTS supplier_layout_rules (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                supplier_gstin TEXT NOT NULL DEFAULT '',
                supplier_name_norm TEXT NOT NULL DEFAULT '',
                layout_hash TEXT NOT NULL DEFAULT '',
                header_signature TEXT NOT NULL DEFAULT '',
                rule_json TEXT NOT NULL,
                source TEXT NOT NULL DEFAULT 'user_confirmed',
                confidence REAL NOT NULL DEFAULT 0.85,
                hit_count INTEGER NOT NULL DEFAULT 1,
                success_count INTEGER NOT NULL DEFAULT 1,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                UNIQUE(supplier_gstin, layout_hash)
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public static SupplierMemoryInfo GetSupplierSummary(string? gstin)
    {
        var norm = MobileInvoicePartyIdentityResolver.NormalizeGstin(gstin ?? string.Empty);
        if (!MobileInvoicePartyIdentityResolver.ValidGstin(norm) || !File.Exists(DatabasePath))
            return new SupplierMemoryInfo(false, norm, string.Empty, 0, 0, string.Empty, string.Empty, string.Empty);

        try
        {
            lock (Gate)
            {
                using var conn = OpenConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    SELECT p.canonical_name, p.preferred_direction, p.preferred_recognition_mode,
                           p.confirmed_count, p.last_confirmed_utc,
                           (SELECT COUNT(*) FROM supplier_hsn_rates h WHERE h.gstin = p.gstin) AS hsn_cnt
                    FROM supplier_profiles p
                    WHERE p.gstin = $g;
                    """;
                cmd.Parameters.AddWithValue("$g", norm);
                using var reader = cmd.ExecuteReader();
                if (!reader.Read())
                    return new SupplierMemoryInfo(false, norm, string.Empty, 0, 0, string.Empty, string.Empty, string.Empty);

                return new SupplierMemoryInfo(
                    true,
                    norm,
                    reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                    reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
                    reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
                    reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                    reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                    reader.IsDBNull(4) ? string.Empty : reader.GetString(4));
            }
        }
        catch
        {
            return new SupplierMemoryInfo(false, norm, string.Empty, 0, 0, string.Empty, string.Empty, string.Empty);
        }
    }

    public static string RecordConfirmedInvoice(MobileInvoiceQueueItem scan, MobileInvoiceMappedResult mapped, string? clientGstin = null)
    {
        var gstin = MobileInvoicePartyIdentityResolver.NormalizeGstin(mapped.CounterpartyGstin);
        var normClient = MobileInvoicePartyIdentityResolver.NormalizeGstin(clientGstin ?? string.Empty);
        if (!string.IsNullOrEmpty(normClient) && gstin.Equals(normClient, StringComparison.OrdinalIgnoreCase))
            return "Supplier memory skipped: counterparty GSTIN matches client's own GSTIN.";
        if (!MobileInvoicePartyIdentityResolver.ValidGstin(gstin))
            return "Supplier memory skipped: counterparty GSTIN is missing or invalid.";

        var canonicalName = (mapped.CounterpartyName ?? string.Empty).Trim();
        var originalOcrName = ExtractOriginalPartyName(scan.PayloadJson, mapped.Direction);
        var mode = mapped.IsInventoryInvoiceMode ? "Item Invoice" : "Accounting Invoice";
        var nowUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

        lock (Gate)
        {
            using var conn = OpenConnection();
            using var tx = conn.BeginTransaction();

            var aliases = new List<string>();
            var prevCount = 0;
            var prevTaxInclusive = 0;
            using (var sel = conn.CreateCommand())
            {
                sel.Transaction = tx;
                sel.CommandText = "SELECT canonical_name, aliases_json, tax_inclusive_hint, confirmed_count FROM supplier_profiles WHERE gstin = $g;";
                sel.Parameters.AddWithValue("$g", gstin);
                using var r = sel.ExecuteReader();
                if (r.Read())
                {
                    if (string.IsNullOrWhiteSpace(canonicalName) && !r.IsDBNull(0))
                        canonicalName = r.GetString(0).Trim();
                    prevTaxInclusive = r.IsDBNull(2) ? 0 : r.GetInt32(2);
                    prevCount = r.IsDBNull(3) ? 0 : r.GetInt32(3);
                    if (!r.IsDBNull(1))
                    {
                        try
                        {
                            var arr = JsonSerializer.Deserialize<List<string>>(r.GetString(1));
                            if (arr is not null) aliases.AddRange(arr.Where(x => !string.IsNullOrWhiteSpace(x)));
                        }
                        catch { }
                    }
                }
            }

            foreach (var cand in new[] { originalOcrName, canonicalName })
            {
                if (!string.IsNullOrWhiteSpace(cand) && !aliases.Any(a => a.Equals(cand, StringComparison.OrdinalIgnoreCase)))
                    aliases.Add(cand.Trim());
            }
            if (aliases.Count > 20)
                aliases = aliases.Skip(aliases.Count - 20).ToList();

            var rowLtSum = mapped.Lines.Sum(l => l.LineTotal);
            var taxSum = mapped.CgstTotal + mapped.SgstTotal + mapped.IgstTotal;
            var inferredInclusive = (prevTaxInclusive != 0 || (mapped.Lines.Count > 0 && mapped.InvoiceTotal > 0 && taxSum > 0 && Math.Abs(rowLtSum - mapped.InvoiceTotal) <= Math.Max(2m, mapped.InvoiceTotal * 0.015m))) ? 1 : 0;

            var compactExample = BuildCompactExampleJson(mapped);
            using (var upsert = conn.CreateCommand())
            {
                upsert.Transaction = tx;
                upsert.CommandText = """
                    INSERT INTO supplier_profiles(
                        gstin, canonical_name, aliases_json, preferred_direction,
                        preferred_recognition_mode, tax_inclusive_hint, confirmed_count,
                        last_confirmed_utc, best_example_json
                    ) VALUES ($g, $name, $aliases, $dir, $mode, $incl, $cnt, $utc, $ex)
                    ON CONFLICT(gstin) DO UPDATE SET
                        canonical_name = excluded.canonical_name,
                        aliases_json = excluded.aliases_json,
                        preferred_direction = excluded.preferred_direction,
                        preferred_recognition_mode = excluded.preferred_recognition_mode,
                        tax_inclusive_hint = excluded.tax_inclusive_hint,
                        confirmed_count = excluded.confirmed_count,
                        last_confirmed_utc = excluded.last_confirmed_utc,
                        best_example_json = excluded.best_example_json;
                    """;
                upsert.Parameters.AddWithValue("$g", gstin);
                upsert.Parameters.AddWithValue("$name", canonicalName);
                upsert.Parameters.AddWithValue("$aliases", JsonSerializer.Serialize(aliases));
                upsert.Parameters.AddWithValue("$dir", (mapped.Direction ?? string.Empty).Trim());
                upsert.Parameters.AddWithValue("$mode", mode);
                upsert.Parameters.AddWithValue("$incl", inferredInclusive);
                upsert.Parameters.AddWithValue("$cnt", prevCount + 1);
                upsert.Parameters.AddWithValue("$utc", nowUtc);
                upsert.Parameters.AddWithValue("$ex", compactExample);
                upsert.ExecuteNonQuery();
            }

            var learnedHsn = 0;
            foreach (var line in mapped.Lines)
            {
                var hsn = new string((line.HsnSac ?? string.Empty).Where(char.IsDigit).ToArray());
                if (hsn.Length < 4) continue;
                if (hsn.Length > 8) hsn = hsn[..8];
                if (!SupportedGstRates.Any(rate => Math.Abs(rate - line.GstRate) <= 0.01m)) continue;

                using var hsnCmd = conn.CreateCommand();
                hsnCmd.Transaction = tx;
                hsnCmd.CommandText = """
                    INSERT INTO supplier_hsn_rates(gstin, hsn_code, gst_rate, sample_description, hit_count, updated_utc)
                    VALUES ($g, $hsn, $rate, $desc, 1, $utc)
                    ON CONFLICT(gstin, hsn_code) DO UPDATE SET
                        gst_rate = excluded.gst_rate,
                        sample_description = CASE WHEN excluded.sample_description <> '' THEN excluded.sample_description ELSE supplier_hsn_rates.sample_description END,
                        hit_count = supplier_hsn_rates.hit_count + 1,
                        updated_utc = excluded.updated_utc;
                    """;
                hsnCmd.Parameters.AddWithValue("$g", gstin);
                hsnCmd.Parameters.AddWithValue("$hsn", hsn);
                hsnCmd.Parameters.AddWithValue("$rate", (double)line.GstRate);
                var desc = (line.Description ?? string.Empty).Trim();
                hsnCmd.Parameters.AddWithValue("$desc", desc.Length > 80 ? desc[..80] : desc);
                hsnCmd.Parameters.AddWithValue("$utc", nowUtc);
                hsnCmd.ExecuteNonQuery();
                learnedHsn++;
            }

            var diffsJson = BuildDiffsJson(scan.PayloadJson, mapped, originalOcrName, canonicalName, mode);
            using (var corrCmd = conn.CreateCommand())
            {
                corrCmd.Transaction = tx;
                corrCmd.CommandText = """
                    INSERT INTO supplier_corrections(gstin, invoice_number, field_diffs_json, learned_utc)
                    VALUES ($g, $inv, $diff, $utc);
                    """;
                corrCmd.Parameters.AddWithValue("$g", gstin);
                corrCmd.Parameters.AddWithValue("$inv", (mapped.InvoiceNumber ?? string.Empty).Trim());
                corrCmd.Parameters.AddWithValue("$diff", diffsJson);
                corrCmd.Parameters.AddWithValue("$utc", nowUtc);
                corrCmd.ExecuteNonQuery();
            }

            var clientHash = !string.IsNullOrEmpty(normClient) ? $"client:{normClient}" : string.Empty;
            var existingRuleNode = ReadClientRuleJson(conn, tx, gstin, clientHash);
            var origInv = ExtractOriginalInvoiceNumber(scan.PayloadJson);
            var invRule = InferInvoiceNumberRule(origInv, mapped.InvoiceNumber ?? string.Empty, existingRuleNode?["invoiceNumberRule"] as JsonObject);
            var lineRules = MergeLineRules(mapped, existingRuleNode?["savedLineRules"] as JsonArray);
            var origZeroRows = CountOriginalZeroValueRows(scan.PayloadJson);
            var savedZeroRows = mapped.Lines.Count(IsZeroValueFreeMappedLine);
            var prevDropZero = existingRuleNode?["dropZeroValueRows"]?.GetValue<bool?>() ?? false;
            var dropZero = savedZeroRows == 0 && (origZeroRows > 0 || prevDropZero);

            var ruleObj = new JsonObject
            {
                ["clientGstin"] = normClient,
                ["preferredDirection"] = (mapped.Direction ?? string.Empty).Trim(),
                ["preferredRecognitionMode"] = mode,
                ["taxInclusive"] = inferredInclusive == 1,
                ["creditNoteMode"] = mapped.CreditNoteTotal > 0m ? "deduct_footer" : (existingRuleNode?["creditNoteMode"]?.ToString() ?? string.Empty),
                ["dropZeroValueRows"] = dropZero,
                ["keepZeroValueRows"] = savedZeroRows > 0,
                ["confirmedByUser"] = true,
                ["savedLineRules"] = lineRules
            };
            if (invRule is not null)
                ruleObj["invoiceNumberRule"] = invRule;
            var ruleJson = ruleObj.ToJsonString();

            var hashesToSave = string.IsNullOrEmpty(clientHash) ? new[] { string.Empty } : new[] { clientHash, string.Empty };
            foreach (var hKey in hashesToSave)
            {
                using var ruleCmd = conn.CreateCommand();
                ruleCmd.Transaction = tx;
                ruleCmd.CommandText = """
                    INSERT INTO supplier_layout_rules(
                        supplier_gstin, supplier_name_norm, layout_hash, header_signature,
                        rule_json, source, confidence, hit_count, success_count, created_at, updated_at
                    ) VALUES ($g, $sn, $lh, '', $rj, 'user_confirmed', 0.95, 1, 1, $utc, $utc)
                    ON CONFLICT(supplier_gstin, layout_hash) DO UPDATE SET
                        supplier_name_norm = excluded.supplier_name_norm,
                        rule_json = excluded.rule_json,
                        confidence = 0.98,
                        success_count = supplier_layout_rules.success_count + 1,
                        updated_at = excluded.updated_at;
                    """;
                ruleCmd.Parameters.AddWithValue("$g", gstin);
                ruleCmd.Parameters.AddWithValue("$sn", new string(canonicalName.ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray()));
                ruleCmd.Parameters.AddWithValue("$lh", hKey);
                ruleCmd.Parameters.AddWithValue("$rj", ruleJson);
                ruleCmd.Parameters.AddWithValue("$utc", nowUtc);
                ruleCmd.ExecuteNonQuery();
            }

            tx.Commit();
            return $"AI Memory learned {canonicalName} ({gstin}) · {prevCount + 1} confirmation(s), {learnedHsn} HSN, {lineRules.Count} line rule(s), mode: {mode}.";
        }
    }

    public static void ApplyClientSupplierRules(JsonObject root, string? clientGstin, string? counterpartyGstin)
    {
        var gstin = MobileInvoicePartyIdentityResolver.NormalizeGstin(counterpartyGstin ?? string.Empty);
        if (!MobileInvoicePartyIdentityResolver.ValidGstin(gstin) || !File.Exists(DatabasePath))
            return;
        var normClient = MobileInvoicePartyIdentityResolver.NormalizeGstin(clientGstin ?? string.Empty);
        var clientHash = !string.IsNullOrEmpty(normClient) ? $"client:{normClient}" : string.Empty;

        try
        {
            JsonObject? ruleNode = null;
            lock (Gate)
            {
                using var conn = OpenConnection();
                ruleNode = (!string.IsNullOrEmpty(clientHash) ? ReadClientRuleJson(conn, null, gstin, clientHash) : null)
                           ?? ReadClientRuleJson(conn, null, gstin, string.Empty);
            }
            if (ruleNode is null) return;

            var prefDir = ruleNode["preferredDirection"]?.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(prefDir) && string.IsNullOrWhiteSpace(root["direction"]?.ToString()))
                root["direction"] = prefDir;

            var prefMode = ruleNode["preferredRecognitionMode"]?.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(prefMode) && string.IsNullOrWhiteSpace(root["recognitionMode"]?.ToString()))
                root["recognitionMode"] = prefMode;

            if (ruleNode["invoiceNumberRule"] is JsonObject invRule)
            {
                var curInv = root["invoiceNumber"]?.ToString().Trim() ?? string.Empty;
                var rewritten = ApplyInvoiceNumberTransform(curInv, invRule);
                if (!string.IsNullOrWhiteSpace(rewritten) && !string.Equals(rewritten, curInv, StringComparison.Ordinal))
                    root["invoiceNumber"] = rewritten;
            }

            if (root["rows"] is JsonArray rows)
            {
                var dropZero = ruleNode["dropZeroValueRows"]?.GetValue<bool?>() ?? false;
                var lineMap = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
                if (ruleNode["savedLineRules"] is JsonArray lineRules)
                {
                    foreach (var item in lineRules.OfType<JsonObject>())
                    {
                        var key = item["lineKey"]?.ToString() ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(key))
                            lineMap[key] = item;
                    }
                }
                for (var i = rows.Count - 1; i >= 0; i--)
                {
                    if (rows[i] is not JsonObject rObj) continue;
                    var desc = rObj["description"]?.ToString().Trim() ?? string.Empty;
                    var hsn = DigitsOnly(rObj["hsnSac"]?.ToString() ?? string.Empty);
                    var txVal = ParseDec(rObj["taxable"]?.ToString());
                    var ltVal = ParseDec(rObj["lineTotal"]?.ToString());
                    if (dropZero && txVal == 0m && ltVal == 0m && !string.IsNullOrWhiteSpace(desc))
                    {
                        rows.RemoveAt(i);
                        continue;
                    }
                    var kFull = MakeLineKey(desc, hsn);
                    var kDesc = MakeLineKey(desc, string.Empty);
                    var matched = (lineMap.TryGetValue(kFull, out var r1) ? r1 : null)
                                  ?? (!string.IsNullOrWhiteSpace(kDesc) && lineMap.TryGetValue(kDesc, out var r2) ? r2 : null);
                    if (matched is not null)
                    {
                        if (string.IsNullOrWhiteSpace(rObj["unit"]?.ToString()) && !string.IsNullOrWhiteSpace(matched["unit"]?.ToString()))
                            rObj["unit"] = matched["unit"]!.ToString();
                        if (string.IsNullOrWhiteSpace(rObj["stockItem"]?.ToString()) && !string.IsNullOrWhiteSpace(matched["stockItem"]?.ToString()))
                            rObj["stockItem"] = matched["stockItem"]!.ToString();
                        if (string.IsNullOrWhiteSpace(rObj["ledger"]?.ToString()) && !string.IsNullOrWhiteSpace(matched["ledger"]?.ToString()))
                            rObj["ledger"] = matched["ledger"]!.ToString();
                        if (ParseDec(rObj["gstRate"]?.ToString()) <= 0m && ParseDec(matched["gstRate"]?.ToString()) > 0m && txVal > 0m)
                            rObj["gstRate"] = matched["gstRate"]!.ToString();
                    }
                }
            }
        }
        catch { }
    }

    private static JsonObject? ReadClientRuleJson(SqliteConnection conn, SqliteTransaction? tx, string gstin, string layoutHash)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT rule_json FROM supplier_layout_rules WHERE supplier_gstin = $g AND layout_hash = $lh LIMIT 1;";
        cmd.Parameters.AddWithValue("$g", gstin);
        cmd.Parameters.AddWithValue("$lh", layoutHash);
        var txt = cmd.ExecuteScalar()?.ToString();
        if (string.IsNullOrWhiteSpace(txt)) return null;
        try { return JsonNode.Parse(txt) as JsonObject; } catch { return null; }
    }

    private static string ExtractOriginalInvoiceNumber(string payloadJson)
    {
        try { return JsonNode.Parse(payloadJson)?["invoiceNumber"]?.ToString().Trim() ?? string.Empty; }
        catch { return string.Empty; }
    }

    private static int CountOriginalZeroValueRows(string payloadJson)
    {
        try
        {
            if (JsonNode.Parse(payloadJson)?["rows"] is not JsonArray arr) return 0;
            return arr.OfType<JsonObject>().Count(r =>
                ParseDec(r["taxable"]?.ToString()) == 0m &&
                ParseDec(r["lineTotal"]?.ToString()) == 0m &&
                !string.IsNullOrWhiteSpace(r["description"]?.ToString()));
        }
        catch { return 0; }
    }

    private static bool IsZeroValueFreeMappedLine(MobileInvoiceMappedLine l) =>
        l.Taxable == 0m && l.Cgst == 0m && l.Sgst == 0m && l.Igst == 0m && l.Cess == 0m && l.LineTotal == 0m &&
        !string.IsNullOrWhiteSpace(l.Description);

    private static JsonObject? InferInvoiceNumberRule(string origInv, string savedInv, JsonObject? existing)
    {
        var orig = (origInv ?? string.Empty).Trim();
        var saved = (savedInv ?? string.Empty).Trim();
        if (!string.IsNullOrEmpty(orig) && !string.IsNullOrEmpty(saved) && !orig.Equals(saved, StringComparison.OrdinalIgnoreCase))
        {
            if (orig.EndsWith(saved, StringComparison.OrdinalIgnoreCase) && orig.Length > saved.Length)
            {
                var prefix = orig[..^saved.Length];
                var prefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { prefix, prefix.Trim() + " ", prefix.Trim() + "-", prefix.Trim() + "." };
                if (existing?["stripPrefixes"] is JsonArray arr)
                    foreach (var p in arr) if (!string.IsNullOrEmpty(p?.ToString())) prefixes.Add(p.ToString());
                return new JsonObject
                {
                    ["stripPrefixes"] = new JsonArray(prefixes.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray()),
                    ["digitsOnly"] = saved.All(char.IsDigit),
                    ["exampleFrom"] = orig,
                    ["exampleTo"] = saved
                };
            }
            if (!orig.All(char.IsDigit) && saved.All(char.IsDigit) && new string(orig.Where(char.IsDigit).ToArray()) == saved)
            {
                return new JsonObject
                {
                    ["digitsOnly"] = true,
                    ["exampleFrom"] = orig,
                    ["exampleTo"] = saved
                };
            }
        }
        return existing?.DeepClone() as JsonObject;
    }

    public static string ApplyInvoiceNumberTransform(string rawInv, JsonObject rule)
    {
        var s = (rawInv ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(s)) return s;
        if (rule["stripPrefixes"] is JsonArray prefixes)
        {
            foreach (var pNode in prefixes)
            {
                var p = pNode?.ToString() ?? string.Empty;
                if (!string.IsNullOrEmpty(p) && s.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                {
                    var trimmed = s[p.Length..].TrimStart(' ', '-', ':', '.');
                    if (trimmed.Length > 0) return trimmed;
                }
            }
        }
        if (rule["digitsOnly"]?.GetValue<bool?>() == true)
        {
            var digits = new string(s.Where(char.IsDigit).ToArray());
            if (digits.Length >= 2) return digits;
        }
        return s;
    }

    private static JsonArray MergeLineRules(MobileInvoiceMappedResult mapped, JsonArray? existing)
    {
        var merged = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        if (existing is not null)
        {
            foreach (var item in existing.OfType<JsonObject>())
            {
                var k = item["lineKey"]?.ToString() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(k)) merged[k] = item.DeepClone().AsObject();
            }
        }
        foreach (var l in mapped.Lines)
        {
            var hsn = DigitsOnly(l.HsnSac ?? string.Empty);
            var desc = (l.Description ?? string.Empty).Trim();
            var key = MakeLineKey(desc, hsn);
            if (string.IsNullOrWhiteSpace(key)) continue;
            merged[key] = new JsonObject
            {
                ["lineKey"] = key,
                ["description"] = desc.Length > 80 ? desc[..80] : desc,
                ["hsnSac"] = hsn,
                ["unit"] = l.Unit ?? string.Empty,
                ["gstRate"] = Math.Round(l.GstRate, 2),
                ["stockItem"] = l.StockItem ?? string.Empty,
                ["isZeroValueFree"] = IsZeroValueFreeMappedLine(l)
            };
        }
        var outArr = new JsonArray();
        foreach (var v in merged.Values.TakeLast(120)) outArr.Add(v);
        return outArr;
    }

    private static string MakeLineKey(string desc, string hsn)
    {
        var clean = new string((desc ?? string.Empty).ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
        if (clean.Length > 28) clean = clean[..28];
        return string.IsNullOrEmpty(hsn) ? clean : $"{hsn}:{clean}";
    }

    private static string DigitsOnly(string s) => new((s ?? string.Empty).Where(char.IsDigit).ToArray());

    private static decimal ParseDec(string? s)
    {
        var clean = (s ?? string.Empty).Replace(",", string.Empty).Trim();
        return decimal.TryParse(clean, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0m;
    }

    private static string BuildDiffsJson(string payloadJson, MobileInvoiceMappedResult mapped, string originalOcrName, string canonicalName, string mode)
    {
        var changedFields = new Dictionary<string, object?>();
        try
        {
            var root = JsonNode.Parse(payloadJson)?.AsObject();
            if (root is not null)
            {
                var origInv = root["invoiceNumber"]?.ToString().Trim() ?? "";
                if (!string.Equals(origInv, mapped.InvoiceNumber?.Trim() ?? "", StringComparison.OrdinalIgnoreCase))
                    changedFields["invoiceNumber"] = new { from = origInv, to = mapped.InvoiceNumber };
                var origTot = root["invoiceTotal"]?.GetValue<decimal?>() ?? 0m;
                if (Math.Abs(origTot - mapped.InvoiceTotal) > 0.05m)
                    changedFields["invoiceTotal"] = new { from = origTot, to = mapped.InvoiceTotal };
                var origTaxable = root["taxableTotal"]?.GetValue<decimal?>() ?? 0m;
                if (Math.Abs(origTaxable - mapped.TaxableTotal) > 0.05m)
                    changedFields["taxableTotal"] = new { from = origTaxable, to = mapped.TaxableTotal };
                var origDir = root["direction"]?.ToString().Trim() ?? "";
                if (!string.IsNullOrWhiteSpace(origDir) && !origDir.Equals(mapped.Direction, StringComparison.OrdinalIgnoreCase))
                    changedFields["direction"] = new { from = origDir, to = mapped.Direction };
            }
        }
        catch { }

        return JsonSerializer.Serialize(new
        {
            originalOcrName,
            verifiedName = canonicalName,
            direction = mapped.Direction,
            recognitionMode = mode,
            lineCount = mapped.Lines.Count,
            invoiceTotal = mapped.InvoiceTotal,
            taxableTotal = mapped.TaxableTotal,
            changedFields
        });
    }

    private static string ExtractOriginalPartyName(string payloadJson, string direction)
    {
        try
        {
            var root = JsonNode.Parse(payloadJson)?.AsObject();
            if (root is null) return string.Empty;
            var role = direction.Equals("Sales", StringComparison.OrdinalIgnoreCase) ? "buyer" : "seller";
            var val = root[role]?["name"]?.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(val)) return val;
            return root["counterparty"]?["name"]?.ToString().Trim() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string BuildCompactExampleJson(MobileInvoiceMappedResult mapped)
    {
        var sampleRows = mapped.Lines.Take(3).Select(l => new
        {
            description = (l.Description ?? string.Empty).Length > 60 ? l.Description![..60] : (l.Description ?? string.Empty),
            hsnSac = l.HsnSac ?? string.Empty,
            taxable = Math.Round(l.Taxable, 2),
            gstRate = Math.Round(l.GstRate, 2),
            cgst = Math.Round(l.Cgst, 2),
            sgst = Math.Round(l.Sgst, 2),
            igst = Math.Round(l.Igst, 2)
        }).ToArray();

        return JsonSerializer.Serialize(new
        {
            invoiceNumber = mapped.InvoiceNumber ?? string.Empty,
            taxableTotal = Math.Round(mapped.TaxableTotal, 2),
            invoiceTotal = Math.Round(mapped.InvoiceTotal, 2),
            rows = sampleRows
        });
    }
}
