using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Kshethra.Domain;
using Kshethra.Domain.Entities;

namespace Kshethra.Application.Common;

public interface IAppDbContext
{
    DbSet<Company> Companies { get; }
    DbSet<FinancialYear> FinancialYears { get; }
    DbSet<CompanySetting> CompanySettings { get; }
    DbSet<InvoiceSetting> InvoiceSettings { get; }
    DbSet<PrintScheme> PrintSchemes { get; }
    DbSet<LabelTemplate> LabelTemplates { get; }
    DbSet<DocumentSequence> DocumentSequences { get; }
    DbSet<User> Users { get; }
    DbSet<UserCompany> UserCompanies { get; }
    DbSet<Role> Roles { get; }
    DbSet<RolePermission> RolePermissions { get; }
    DbSet<RefreshToken> RefreshTokens { get; }
    DbSet<UserActionLog> UserActionLogs { get; }
    DbSet<VoidItem> VoidItems { get; }
    DbSet<SmsEvent> SmsEvents { get; }
    DbSet<SmsTemplate> SmsTemplates { get; }
    DbSet<SmsSchedule> SmsSchedules { get; }
    DbSet<OutboundMessage> OutboundMessages { get; }
    DbSet<FaqEntry> FaqEntries { get; }
    DbSet<FaqQuestionLog> FaqQuestionLogs { get; }
    DbSet<Deity> Deities { get; }
    DbSet<PoojaSlot> PoojaSlots { get; }
    DbSet<Devotee> Devotees { get; }
    DbSet<Booking> Bookings { get; }
    DbSet<BookingLine> BookingLines { get; }
    DbSet<BookingOccurrence> BookingOccurrences { get; }
    DbSet<CalendarCorrection> CalendarCorrections { get; }
    DbSet<Donation> Donations { get; }

    DbSet<AccountGroup> AccountGroups { get; }
    DbSet<Ledger> Ledgers { get; }
    DbSet<Area> Areas { get; }
    DbSet<Category> Categories { get; }
    DbSet<Manufacturer> Manufacturers { get; }
    DbSet<PriceList> PriceLists { get; }
    DbSet<PriceListItem> PriceListItems { get; }
    DbSet<Employee> Employees { get; }
    DbSet<LoyaltyScheme> LoyaltySchemes { get; }
    DbSet<Product> Products { get; }
    DbSet<ProductImage> ProductImages { get; }
    DbSet<Batch> Batches { get; }
    DbSet<ItemOffer> ItemOffers { get; }
    DbSet<Bom> Boms { get; }
    DbSet<BomLine> BomLines { get; }

    DbSet<TradeDocument> TradeDocuments { get; }
    DbSet<TradeDocumentLine> TradeDocumentLines { get; }
    DbSet<Voucher> Vouchers { get; }
    DbSet<VoucherLine> VoucherLines { get; }
    DbSet<AccountEntry> AccountEntries { get; }
    DbSet<StockMovement> StockMovements { get; }
    DbSet<BillOutstanding> BillOutstandings { get; }
    DbSet<BillSettlement> BillSettlements { get; }
    DbSet<LoyaltyTransaction> LoyaltyTransactions { get; }

    DatabaseFacade Database { get; }
    Task<int> SaveChangesAsync(CancellationToken ct = default);
    /// <summary>Temporarily bypasses the tenant filter (first-run setup, cross-company copy, login).</summary>
    IDisposable IgnoreTenant();
}

public interface ICurrentUser
{
    bool IsAuthenticated { get; }
    long UserId { get; }
    string UserName { get; }
    long CompanyId { get; }
    long FinancialYearId { get; }
    bool IsAdmin { get; }
    string? Machine { get; }
    string? IpAddress { get; }
    bool HasRight(string screen, Right right);
}

public interface IClock
{
    DateTimeOffset Now { get; }
    /// <summary>UTC offset of the business (e.g. +05:30 for IST); used to turn calendar dates into instants.</summary>
    TimeSpan Offset { get; }
}

public static class ClockExtensions
{
    public static DateOnly Today(this IClock c) => DateOnly.FromDateTime(c.Now.ToOffset(c.Offset).Date);
    /// <summary>Start of the business day.</summary>
    public static DateTimeOffset DayStart(this IClock c, DateOnly d) => new(d.ToDateTime(TimeOnly.MinValue), c.Offset);
    /// <summary>Start of the following business day (exclusive upper bound).</summary>
    public static DateTimeOffset DayEnd(this IClock c, DateOnly d) => new(d.AddDays(1).ToDateTime(TimeOnly.MinValue), c.Offset);
    public static DateOnly BusinessDate(this IClock c, DateTimeOffset t) => DateOnly.FromDateTime(t.ToOffset(c.Offset).Date);
}

/// <summary>Pushes live events to connected clients (SignalR in the API).</summary>
public interface IRealtimeNotifier
{
    Task DocumentSavedAsync(long companyId, object payload);
    Task StockChangedAsync(long companyId, IEnumerable<long> productIds);
    Task MasterChangedAsync(long companyId, string kind);
}

/// <summary>Transactional SMS / e-mail provider. Credentials come from configuration, never from source.</summary>
public interface IMessageSender
{
    Task SendSmsAsync(string mobile, string text, CancellationToken ct);
    Task SendEmailAsync(string to, string subject, string body, string? attachmentName, byte[]? attachment, CancellationToken ct);
}

/// <summary>Sends a ready-made printer job (e.g. ESC/POS bytes) as a RAW job to a printer installed on the server.</summary>
public interface IRawPrinter
{
    /// <exception cref="DomainException">The printer is not installed or the job could not be sent.</exception>
    void Send(string printerName, string documentName, byte[] data);
}

public class NotFoundException(string message) : Exception(message);
public class ForbiddenException(string message) : Exception(message);
public class ConflictException(string message) : Exception(message);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);

public sealed record IdName(long Id, string Name);
