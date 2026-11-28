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
                upsert.Parameters.AddWithValue("$incl", prevTaxInclusive);
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

            var diffsJson = JsonSerializer.Serialize(new
            {
                originalOcrName,
                verifiedName = canonicalName,
                direction = mapped.Direction,
                recognitionMode = mode,
                lineCount = mapped.Lines.Count,
                invoiceTotal = mapped.InvoiceTotal
            });
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

            tx.Commit();
            return $"AI Memory learned {canonicalName} ({gstin}) · {prevCount + 1} confirmation(s), {learnedHsn} HSN rule(s), mode: {mode}.";
        }
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
