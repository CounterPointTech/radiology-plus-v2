using System.Globalization;
using System.IO.Compression;
using System.Text;
using RadiologyPlus.Core.Billing;

namespace RadiologyPlus.Data.Billing;

/// <summary>
/// Parses the CMS PFS Relative Value File (PPRRVU) — either the RVUyy{A-D} zip (we
/// locate the non-QPP PPRRVU csv inside) or a bare PPRRVU csv. The file is a 32-column
/// CSV with a ~10-row title/preamble block; data rows are detected by shape (a HCPCS in
/// col 0 + a numeric work RVU in col 5) rather than a fixed skip count, so a future
/// release that adds or drops a banner line still parses cleanly.
///
/// Column map (0-based), validated against RVU26A (PPRRVU2026_Jan_nonQPP.csv):
///   0 HCPCS · 1 MOD · 2 DESCRIPTION · 3 STATUS · 5 WORK RVU · 6 PE RVU non-fac
///   · 8 PE RVU fac · 10 MP RVU · 11 TOTAL non-fac · 12 TOTAL fac · 14 GLOBAL DAYS
/// (col 4 = "not used for Medicare payment", 7/9 = PE NA indicators, 13 = PC/TC — skipped.)
/// </summary>
public sealed class RvuValuesImporter : IRvuValuesImporter
{
    private const int ColHcpcs = 0, ColMod = 1, ColDesc = 2, ColStatus = 3;
    private const int ColWork = 5, ColPeNonFac = 6, ColPeFac = 8, ColMp = 10;
    private const int ColTotalNonFac = 11, ColTotalFac = 12, ColGlobal = 14;
    private const int MinColumns = 15;  // we read up to index 14

    // numeric(8,4) domain in billing.rvu_values. An out-of-range value would abort the
    // whole UNNEST upsert with a Postgres overflow, so we reject it per-row instead.
    private const decimal MaxNumeric = 9999.9999m;

    // The upload cap bounds only the COMPRESSED zip, so a zip bomb could inflate to GBs.
    // Bound the decompressed read too — 128 MB fits the real ~3 MB csv with huge headroom.
    private const long MaxDecompressedBytes = 128L * 1024 * 1024;

    public RvuValueParseResult Parse(Stream content, string fileName, short year, char quarter)
    {
        ArgumentNullException.ThrowIfNull(content);

        var rows = new List<RvuValueUpsert>(capacity: 20_000);
        var errors = new List<CptImportError>();

        byte[] bytes;
        using (var ms = new MemoryStream())
        {
            content.CopyTo(ms);
            bytes = ms.ToArray();
        }

        string? csv;
        try
        {
            csv = ExtractCsv(bytes, errors);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException)
        {
            errors.Add(new CptImportError(0, null, $"Couldn't read the upload as a zip or csv: {ex.Message}"));
            return new RvuValueParseResult(year, quarter, rows, errors);
        }
        if (csv is null)
            return new RvuValueParseResult(year, quarter, rows, errors);  // error already recorded

        // Dedupe within the batch — a duplicate (hcpcs, modifier) would make the
        // ON CONFLICT upsert "affect a row a second time" and abort the whole load.
        var seen = new HashSet<(string, string)>();
        int lineNo = 0;
        foreach (var fields in ParseCsv(csv))
        {
            lineNo++;
            if (fields.Length < MinColumns) continue;            // preamble / short banner rows
            var hcpcs = fields[ColHcpcs].Trim();
            if (!LooksLikeHcpcs(hcpcs)) continue;                // skips preamble + the "HCPCS" header
            if (!TryParseDecimal(fields[ColWork], out var work)) continue;  // header row's "RVU" etc.
            if (work < 0 || work > MaxNumeric)
            {
                errors.Add(new CptImportError(lineNo, hcpcs, $"Work RVU {work} out of range [0, {MaxNumeric}]."));
                continue;
            }

            hcpcs = hcpcs.ToUpperInvariant();
            var modifier = fields[ColMod].Trim().ToUpperInvariant();
            if (!seen.Add((hcpcs, modifier)))
            {
                errors.Add(new CptImportError(lineNo, hcpcs, $"Duplicate (HCPCS, modifier '{modifier}') — first wins."));
                continue;
            }

            rows.Add(new RvuValueUpsert(
                Hcpcs:        hcpcs,
                Modifier:     modifier,
                Description:  NullIfBlank(fields[ColDesc]),
                WorkRvu:      work,
                PeRvuNonFac:  ParseComponent(fields, ColPeNonFac, lineNo, hcpcs, "PE RVU non-fac", errors),
                PeRvuFac:     ParseComponent(fields, ColPeFac, lineNo, hcpcs, "PE RVU fac", errors),
                MpRvu:        ParseComponent(fields, ColMp, lineNo, hcpcs, "MP RVU", errors),
                TotalNonFac:  ParseComponent(fields, ColTotalNonFac, lineNo, hcpcs, "total non-fac", errors),
                TotalFac:     ParseComponent(fields, ColTotalFac, lineNo, hcpcs, "total fac", errors),
                StatusCode:   NullIfBlank(fields[ColStatus]),
                GlobalDays:   NullIfBlank(fields[ColGlobal])));
        }

        if (rows.Count == 0 && errors.Count == 0)
            errors.Add(new CptImportError(0, null, "No PPRRVU data rows found — is this a CMS RVU (PPRRVU) file?"));

        return new RvuValueParseResult(year, quarter, rows, errors);
    }

    // Pull the PPRRVU csv text out of a zip (preferring the non-QPP variant), or decode
    // the bytes directly as csv when they aren't a zip. CMS ships these as Latin-1.
    private static string? ExtractCsv(byte[] bytes, List<CptImportError> errors)
    {
        bool isZip = bytes.Length >= 2 && bytes[0] == 0x50 && bytes[1] == 0x4B; // "PK"
        if (!isZip)
            return Encoding.Latin1.GetString(bytes);

        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var csvEntries = archive.Entries
            .Where(e => e.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
                        && e.Name.Contains("PPRRVU", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var chosen = csvEntries.FirstOrDefault(e => e.Name.Contains("nonQPP", StringComparison.OrdinalIgnoreCase))
                     ?? csvEntries.FirstOrDefault();
        if (chosen is null)
        {
            errors.Add(new CptImportError(0, null,
                $"No PPRRVU csv inside the zip. Entries: {string.Join(", ", archive.Entries.Select(e => e.Name))}."));
            return null;
        }
        // Bound the decompressed read so a zip bomb can't inflate past the cap and OOM
        // the process. entry.Length is a central-directory hint; the streamed counter is
        // the real guard against a spoofed length.
        if (chosen.Length > MaxDecompressedBytes)
        {
            errors.Add(new CptImportError(0, null,
                $"PPRRVU entry '{chosen.Name}' reports {chosen.Length} decompressed bytes, over the {MaxDecompressedBytes / 1024 / 1024} MB cap."));
            return null;
        }
        using var es = chosen.Open();
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = es.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > MaxDecompressedBytes)
            {
                errors.Add(new CptImportError(0, null,
                    $"PPRRVU entry '{chosen.Name}' exceeds the {MaxDecompressedBytes / 1024 / 1024} MB decompressed cap."));
                return null;
            }
            ms.Write(buffer, 0, read);
        }
        return Encoding.Latin1.GetString(ms.ToArray());
    }

    private static bool LooksLikeHcpcs(string s)
    {
        // CPT (5 digits), Category II (####F), III (####T), HCPCS Level II (A####):
        // all exactly 5 alphanumerics. Allow 4–7 to be forgiving of future shapes.
        if (s.Length is < 4 or > 7) return false;
        foreach (var c in s) if (!char.IsLetterOrDigit(c)) return false;
        return true;
    }

    // Parse an optional numeric component column. Blank -> null (legitimately absent). A
    // non-blank cell that won't parse, or whose magnitude exceeds the numeric(8,4) domain,
    // is recorded as a per-row error and stored as null — so one junk/oversized cell can't
    // abort the whole batch on the DB cast (mirrors CptMasterImporter's per-row errors).
    private static decimal? ParseComponent(string[] fields, int col, int lineNo, string hcpcs, string name, List<CptImportError> errors)
    {
        if (col >= fields.Length) return null;
        var raw = fields[col];
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (!TryParseDecimal(raw, out var v))
        {
            errors.Add(new CptImportError(lineNo, hcpcs, $"Non-numeric {name} '{raw.Trim()}'; stored null."));
            return null;
        }
        if (Math.Abs(v) > MaxNumeric)
        {
            errors.Add(new CptImportError(lineNo, hcpcs, $"{name} {v} out of range; stored null."));
            return null;
        }
        return v;
    }

    private static string? NullIfBlank(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static bool TryParseDecimal(string? raw, out decimal value)
    {
        value = 0m;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var t = raw.Trim();
        if (t.StartsWith('#')) return false;  // spreadsheet poison (#N/A, #REF! …)
        return decimal.TryParse(t, NumberStyles.Number, CultureInfo.InvariantCulture, out value);
    }

    // Minimal RFC-4180 reader: quoted fields, embedded commas/quotes ("" -> "), CRLF/LF.
    private static IEnumerable<string[]> ParseCsv(string text)
    {
        var row = new List<string>();
        var field = new StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else inQuotes = false;
                }
                else field.Append(ch);
                continue;
            }
            switch (ch)
            {
                case '"': inQuotes = true; break;
                case ',': row.Add(field.ToString()); field.Clear(); break;
                case '\r': break;
                case '\n':
                    row.Add(field.ToString()); field.Clear();
                    yield return row.ToArray(); row.Clear();
                    break;
                default: field.Append(ch); break;
            }
        }
        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            yield return row.ToArray();
        }
    }
}
