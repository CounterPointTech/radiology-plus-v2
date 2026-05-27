using System.Globalization;
using ClosedXML.Excel;
using RadiologyPlus.Core.Billing;

namespace RadiologyPlus.Data.Billing;

/// <summary>
/// Parses Amber's RVU spreadsheet. Per her 2026-05-14 walk-through:
///
///   The canonical sheet is "RVU" — header-less, column layout:
///     A: CPT code or ";"-joined bundle of CPTs (e.g. "70492;71270;74178")
///     B: work RVU (numeric)
///     C: exam description (free text)
///     E, G: facility/site notes she keeps for herself — not relevant.
///
///   Bundles stay atomic: we store and look them up as the joined string.
///
/// For backwards compatibility with the older "{year} mpfs" sheet shape
/// (where the prior-year RVU lived in column B and the current-year value
/// in column D), we keep an explicit alternate column map keyed on sheet
/// name. If neither shape recognizes the sheet, we read RVU layout by
/// default.
/// </summary>
public sealed class CptMasterImporter : ICptMasterImporter
{
    private const string DefaultSheet = "RVU";

    /// <summary>Canonical sheet name for the year-tagged variant ("2026 mpfs").</summary>
    public static string CanonicalSheetForYear(short year) =>
        $"{year} mpfs";

    private sealed record SheetLayout(int CptCol, int RvuCol, int DescCol);

    private static SheetLayout LayoutFor(string sheetName)
    {
        // "<year> mpfs" — prior in B, current in D.
        if (sheetName.EndsWith(" mpfs", StringComparison.OrdinalIgnoreCase))
            return new SheetLayout(CptCol: 1, RvuCol: 4, DescCol: 3);

        // Amber's RVU sheet (and any unknown layout — better to read A/B/C than fail).
        return new SheetLayout(CptCol: 1, RvuCol: 2, DescCol: 3);
    }

    public CptMasterParseResult Parse(Stream content, string sheetName, short year)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (string.IsNullOrWhiteSpace(sheetName)) sheetName = DefaultSheet;
        var layout = LayoutFor(sheetName);

        var rows = new List<CptCodeUpsert>(capacity: 1024);
        var errors = new List<CptImportError>();
        // Track the first-seen value of each CPT so we can distinguish silent
        // facility-duplicates (same code + same RVU + same description, just
        // repeated for a different facility on Amber's sheet) from real
        // conflicts (same code with a different RVU or description).
        var seen = new Dictionary<string, CptCodeUpsert>(StringComparer.OrdinalIgnoreCase);

        // Don't seek the stream — caller may have handed us a non-seekable upload.
        using var workbook = new XLWorkbook(content);
        if (!workbook.Worksheets.TryGetWorksheet(sheetName, out var ws))
        {
            errors.Add(new CptImportError(
                Row: 0,
                Cpt: null,
                Message: $"Sheet '{sheetName}' not found. Available sheets: {string.Join(", ", workbook.Worksheets.Select(w => w.Name))}."));
            return new CptMasterParseResult(year, sheetName, rows, errors);
        }

        var range = ws.RangeUsed();
        if (range is null)
        {
            errors.Add(new CptImportError(0, null, "Sheet has no used range."));
            return new CptMasterParseResult(year, sheetName, rows, errors);
        }

        // Skip a single header row if row 1's CPT cell doesn't look like a code
        // OR its RVU cell isn't numeric. The "RVU" sheet has no header; the
        // "<year> mpfs" sheet does — both cases are handled.
        var firstRow = range.FirstRow().RowNumber();
        var lastRow = range.LastRow().RowNumber();
        int startRow = firstRow;
        if (lastRow >= startRow && IsHeaderRow(ws, startRow, layout))
            startRow++;

        for (int r = startRow; r <= lastRow; r++)
        {
            var cptRaw = ReadCell(ws, r, layout.CptCol);
            if (string.IsNullOrWhiteSpace(cptRaw))
                continue;                                  // blank rows are common — skip silently

            var code = NormalizeCpt(cptRaw);
            if (string.IsNullOrEmpty(code))
            {
                errors.Add(new CptImportError(r, cptRaw, "Couldn't normalize CPT code."));
                continue;
            }

            var rvuRaw = ReadCell(ws, r, layout.RvuCol);
            if (!TryParseRvu(rvuRaw, out var workRvu))
            {
                errors.Add(new CptImportError(r, code, $"Non-numeric RVU value '{rvuRaw}' in column {ColumnLetter(layout.RvuCol)}."));
                continue;
            }
            if (workRvu < 0)
            {
                errors.Add(new CptImportError(r, code, $"Negative RVU value {workRvu}."));
                continue;
            }

            var description = ReadCell(ws, r, layout.DescCol)?.Trim();
            if (string.IsNullOrWhiteSpace(description))
            {
                errors.Add(new CptImportError(r, code, $"Description column ({ColumnLetter(layout.DescCol)}) is empty."));
                continue;
            }

            if (seen.TryGetValue(code, out var prior))
            {
                // Per Amber's 2026-05-14 spec: descriptions vary per-facility on
                // the RVU sheet but the work-RVU is invariant. Only flag when
                // the RVU itself disagrees. We keep the first-seen description
                // (deterministic and matches the row order in her file).
                if (prior.WorkRvu == workRvu)
                    continue;

                errors.Add(new CptImportError(
                    r, code,
                    $"Duplicate CPT with different RVU (prev={prior.WorkRvu}, now={workRvu}) — last value wins."));
                rows.RemoveAll(x => string.Equals(x.Code, code, StringComparison.OrdinalIgnoreCase));
            }

            var upsert = new CptCodeUpsert(code, description, workRvu, Notes: null);
            seen[code] = upsert;
            rows.Add(upsert);
        }

        return new CptMasterParseResult(year, sheetName, rows, errors);
    }

    private static bool IsHeaderRow(IXLWorksheet ws, int row, SheetLayout layout)
    {
        // A header row has non-numeric values in BOTH the CPT and RVU columns.
        // The "RVU" sheet is header-less (row 1 col B is numeric) so we keep it.
        var rvuRaw = ReadCell(ws, row, layout.RvuCol);
        if (TryParseRvu(rvuRaw, out _)) return false;
        var cptRaw = ReadCell(ws, row, layout.CptCol);
        if (string.IsNullOrWhiteSpace(cptRaw)) return false;
        return !LooksLikeCpt(cptRaw);
    }

    private static string ColumnLetter(int col) =>
        col switch
        {
            1 => "A", 2 => "B", 3 => "C", 4 => "D", 5 => "E",
            _ => col.ToString(CultureInfo.InvariantCulture),
        };

    private static string? ReadCell(IXLWorksheet ws, int row, int col)
    {
        var cell = ws.Cell(row, col);
        if (cell is null || cell.IsEmpty()) return null;
        // GetFormattedString gives us what the user sees — handles dates, formulas, numbers consistently.
        return cell.GetFormattedString();
    }

    private static bool LooksLikeCpt(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var s = raw.Trim();
        // Single CPT: 4-6 alphanumeric characters (handles HCPCS like g0279).
        // Bundle: multiple CPTs joined by ';' per Amber's 2026-05-14 spec.
        // Allowed chars are letters, digits, and ';' separators.
        if (s.Length < 4) return false;
        bool seenAlnum = false;
        foreach (var c in s)
        {
            if (char.IsLetterOrDigit(c)) { seenAlnum = true; continue; }
            if (c == ';') continue;
            return false;
        }
        return seenAlnum;
    }

    private static string NormalizeCpt(string raw)
    {
        var s = raw.Trim();
        // Strip the ".0" Excel sometimes appends when the column is "general" and the cell is integer.
        if (s.EndsWith(".0", StringComparison.Ordinal)) s = s[..^2];
        return s;
    }

    private static bool TryParseRvu(string? raw, out decimal value)
    {
        value = 0m;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var trimmed = raw.Trim();
        // Common spreadsheet poison: '#N/A', '#REF!', '#NAME?'.
        if (trimmed.StartsWith('#')) return false;
        return decimal.TryParse(
            trimmed,
            NumberStyles.Number | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture,
            out value);
    }
}
