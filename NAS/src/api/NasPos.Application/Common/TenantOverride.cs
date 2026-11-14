namespace NasPos.Application.Common;

/// <summary>
/// Scoped holder that lets a background job or a cross-company copy act on a specific company.
/// When set, <see cref="ICurrentUser"/> reports this company / year instead of the signed-in one.
/// </summary>
public sealed class TenantOverride
{
    public long? CompanyId { get; set; }
    public long? FinancialYearId { get; set; }
    public long? UserId { get; set; }
    public string? UserName { get; set; }
}

public interface ICompanyBackupService
{
    Task<byte[]> ExportAsync(CancellationToken ct);
    Task<long> RestoreAsync(Stream zip, string newCompanyName, CancellationToken ct);
}
