namespace RadiologyPlus.Core.Billing;

public interface ICptMasterImporter
{
    /// <summary>
    /// Parse an .xlsx stream and return the (rows, errors) tuple ready for
    /// IBillingRepository.ImportCptMasterAsync. Stateless — does no DB writes.
    /// </summary>
    /// <param name="content">The raw bytes of the workbook.</param>
    /// <param name="sheetName">Sheet to read. Defaults to the canonical "2026 mpfs" sheet.</param>
    /// <param name="year">Year column to anchor on. Used to disambiguate when the sheet has both prior-year and current-year columns side by side.</param>
    CptMasterParseResult Parse(
        Stream content,
        string sheetName,
        short year);
}

public sealed record CptMasterParseResult(
    short Year,
    string SheetName,
    IReadOnlyList<CptCodeUpsert> Rows,
    IReadOnlyList<CptImportError> Errors);
