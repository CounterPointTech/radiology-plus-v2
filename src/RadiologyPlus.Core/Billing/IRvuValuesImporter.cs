namespace RadiologyPlus.Core.Billing;

/// <summary>
/// Parses a CMS PFS Relative Value upload into <see cref="RvuValueUpsert"/> rows ready
/// for <see cref="IBillingRepository.ImportRvuValuesAsync"/>. Stateless — no DB writes.
/// </summary>
public interface IRvuValuesImporter
{
    /// <summary>
    /// Parse either the CMS <c>RVUyy{A-D}</c> zip (the PPRRVU csv is located inside,
    /// preferring the non-QPP variant) or a bare PPRRVU csv stream. The multi-row CMS
    /// preamble + header is skipped automatically; one <see cref="RvuValueUpsert"/> is
    /// produced per (HCPCS, modifier) data row.
    /// </summary>
    /// <param name="content">Raw upload bytes (zip or csv). Need not be seekable.</param>
    /// <param name="fileName">Original filename — used to detect a zip vs csv.</param>
    /// <param name="year">Calendar year the file represents (e.g. 2026).</param>
    /// <param name="quarter">CMS quarter letter: A=Jan, B=Apr, C=Jul, D=Oct.</param>
    RvuValueParseResult Parse(Stream content, string fileName, short year, char quarter);
}

public sealed record RvuValueParseResult(
    short Year,
    char Quarter,
    IReadOnlyList<RvuValueUpsert> Rows,
    IReadOnlyList<CptImportError> Errors);
