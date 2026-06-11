using System.Globalization;
using ClosedXML.Excel;
using RadiologyPlus.Core.Billing;

namespace RadiologyPlus.Data.Billing;

/// <summary>
/// Filter set applied to the export AFTER the run is loaded — same shape as the
/// UI's filter dropdowns. Null = no filter on that dimension.
/// </summary>
public sealed record ReconciliationExportFilters(long? NovaradPhysicianId, string? SiteCode);

/// <summary>
/// Render a persisted <see cref="ReconciliationRun"/> as an .xlsx workbook with
/// two sheets: "Summary" (header rollups + active-filter banner) and "Line items"
/// (the per-(physician × site × CPT) table). The bytes are handed back to the
/// caller for HTTP streaming.
/// </summary>
public interface IReconciliationExporter
{
    byte[] Export(ReconciliationRun run, ReconciliationExportFilters filters);
}

public sealed class ReconciliationExporter : IReconciliationExporter
{
    // Brand cyan from the design tokens — accent-on-white is plenty for a header
    // band, and matches the in-app reconciliation surface.
    private static readonly XLColor HeaderFill = XLColor.FromHtml("#E2F4F8");
    private static readonly XLColor HeaderFg   = XLColor.FromHtml("#0A6E84");

    public byte[] Export(ReconciliationRun run, ReconciliationExportFilters filters)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(filters);

        var filtered = run.LineItems
            .Where(li => filters.NovaradPhysicianId == null || li.NovaradPhysicianId == filters.NovaradPhysicianId)
            .Where(li => filters.SiteCode == null || string.Equals(li.SiteCode, filters.SiteCode, StringComparison.OrdinalIgnoreCase))
            .ToList();

        using var wb = new XLWorkbook();
        BuildSummarySheet(wb, run, filters, filtered);
        BuildLineItemsSheet(wb, filtered);

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    private static void BuildSummarySheet(
        XLWorkbook wb,
        ReconciliationRun run,
        ReconciliationExportFilters filters,
        List<ReconciliationLineItem> filtered)
    {
        var ws = wb.Worksheets.Add("Summary");

        ws.Cell("A1").Value = "Reconciliation summary";
        ws.Cell("A1").Style.Font.Bold = true;
        ws.Cell("A1").Style.Font.FontSize = 14;

        int row = 3;
        ws.Cell(row, 1).Value = "Period start";
        ws.Cell(row++, 2).Value = run.PeriodStart.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        ws.Cell(row, 1).Value = "Period end";
        ws.Cell(row++, 2).Value = run.PeriodEnd.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        ws.Cell(row, 1).Value = "Generated at";
        ws.Cell(row++, 2).Value = run.GeneratedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        ws.Cell(row, 1).Value = "Run kind";
        ws.Cell(row++, 2).Value = run.RunKind == 2 ? "Final" : "Preview";

        row++;
        ws.Cell(row, 1).Value = "Physician filter";
        ws.Cell(row++, 2).Value = filters.NovaradPhysicianId?.ToString(CultureInfo.InvariantCulture) ?? "(all)";
        ws.Cell(row, 1).Value = "Site filter";
        ws.Cell(row++, 2).Value = filters.SiteCode ?? "(all)";

        row++;
        ws.Cell(row, 1).Value = "Reports (run total)";
        ws.Cell(row++, 2).Value = run.TotalReports;
        ws.Cell(row, 1).Value = "Radiologists (run total)";
        ws.Cell(row++, 2).Value = run.TotalRadiologists;
        ws.Cell(row, 1).Value = "Work RVU (run total)";
        ws.Cell(row, 2).Value = (double)run.TotalWorkRvu;
        ws.Cell(row++, 2).Style.NumberFormat.Format = "0.00";

        row++;
        ws.Cell(row, 1).Value = "Filtered lines";
        ws.Cell(row++, 2).Value = filtered.Count;
        ws.Cell(row, 1).Value = "Filtered Work RVU";
        ws.Cell(row, 2).Value = (double)filtered.Sum(l => l.WorkRvuTotal);
        ws.Cell(row, 2).Style.NumberFormat.Format = "0.00";

        ws.Column(1).Width = 26;
        ws.Column(2).Width = 28;
    }

    private static void BuildLineItemsSheet(XLWorkbook wb, IReadOnlyList<ReconciliationLineItem> rows)
    {
        var ws = wb.Worksheets.Add("Line items");

        var headers = new[]
        {
            "Physician", "Site", "CPT", "Description",
            "Reports", "Units", "RVU/unit", "Total RVU",
            "Novarad RVU", "Mismatch",
        };
        for (int i = 0; i < headers.Length; i++)
        {
            var cell = ws.Cell(1, i + 1);
            cell.Value = headers[i];
            cell.Style.Font.Bold = true;
            cell.Style.Font.FontColor = HeaderFg;
            cell.Style.Fill.BackgroundColor = HeaderFill;
        }

        int r = 2;
        foreach (var li in rows)
        {
            ws.Cell(r, 1).Value = li.PhysicianDisplayName;
            ws.Cell(r, 2).Value = li.SiteCode;
            ws.Cell(r, 3).Value = li.CptCode;
            ws.Cell(r, 4).Value = li.CptDescription ?? "";
            ws.Cell(r, 5).Value = li.ReportCount;
            ws.Cell(r, 6).Value = (double)li.Units;
            ws.Cell(r, 6).Style.NumberFormat.Format = "0.00";
            ws.Cell(r, 7).Value = (double)li.WorkRvuPerUnit;
            ws.Cell(r, 7).Style.NumberFormat.Format = "0.00";
            ws.Cell(r, 8).Value = (double)li.WorkRvuTotal;
            ws.Cell(r, 8).Style.NumberFormat.Format = "0.00";
            if (li.NovaradRvuWork.HasValue)
            {
                ws.Cell(r, 9).Value = (double)li.NovaradRvuWork.Value;
                ws.Cell(r, 9).Style.NumberFormat.Format = "0.00";
            }
            ws.Cell(r, 10).Value = li.RvuMismatch ? "Y" : "";
            r++;
        }

        ws.SheetView.FreezeRows(1);
        ws.Columns(1, headers.Length).AdjustToContents();
    }
}
