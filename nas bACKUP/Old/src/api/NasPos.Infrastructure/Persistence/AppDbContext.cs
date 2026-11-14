using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using NasPos.Application.Common;
using NasPos.Domain;
using NasPos.Domain.Entities;

namespace NasPos.Infrastructure.Persistence;

public class AppDbContext : DbContext, IAppDbContext
{
    private readonly ICurrentUser? _user;
    private readonly IClock? _clock;
    private int _ignoreTenant;

    public AppDbContext(DbContextOptions<AppDbContext> options, ICurrentUser? user = null, IClock? clock = null) : base(options)
    {
        _user = user;
        _clock = clock;
    }

    /// <summary>Evaluated per query by the global tenant filter.</summary>
    internal long TenantId => _user is { IsAuthenticated: true } ? _user.CompanyId : 0;
    internal bool TenantIgnored => _ignoreTenant > 0;

    public IDisposable IgnoreTenant()
    {
        Interlocked.Increment(ref _ignoreTenant);
        return new Scope(() => Interlocked.Decrement(ref _ignoreTenant));
    }

    private sealed class Scope(Action onDispose) : IDisposable
    {
        private Action? _a = onDispose;
        public void Dispose() { _a?.Invoke(); _a = null; }
    }

    public DbSet<Company> Companies => Set<Company>();
    public DbSet<FinancialYear> FinancialYears => Set<FinancialYear>();
    public DbSet<CompanySetting> CompanySettings => Set<CompanySetting>();
    public DbSet<InvoiceSetting> InvoiceSettings => Set<InvoiceSetting>();
    public DbSet<PrintScheme> PrintSchemes => Set<PrintScheme>();
    public DbSet<LabelTemplate> LabelTemplates => Set<LabelTemplate>();
    public DbSet<DocumentSequence> DocumentSequences => Set<DocumentSequence>();
    public DbSet<User> Users => Set<User>();
    public DbSet<UserCompany> UserCompanies => Set<UserCompany>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<UserActionLog> UserActionLogs => Set<UserActionLog>();
    public DbSet<VoidItem> VoidItems => Set<VoidItem>();
    public DbSet<SmsEvent> SmsEvents => Set<SmsEvent>();
    public DbSet<SmsTemplate> SmsTemplates => Set<SmsTemplate>();
    public DbSet<SmsSchedule> SmsSchedules => Set<SmsSchedule>();
    public DbSet<OutboundMessage> OutboundMessages => Set<OutboundMessage>();
    public DbSet<AccountGroup> AccountGroups => Set<AccountGroup>();
    public DbSet<Ledger> Ledgers => Set<Ledger>();
    public DbSet<Area> Areas => Set<Area>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Manufacturer> Manufacturers => Set<Manufacturer>();
    public DbSet<PriceList> PriceLists => Set<PriceList>();
    public DbSet<PriceListItem> PriceListItems => Set<PriceListItem>();
    public DbSet<Employee> Employees => Set<Employee>();
    public DbSet<LoyaltyScheme> LoyaltySchemes => Set<LoyaltyScheme>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<ProductImage> ProductImages => Set<ProductImage>();
    public DbSet<Batch> Batches => Set<Batch>();
    public DbSet<ItemOffer> ItemOffers => Set<ItemOffer>();
    public DbSet<Bom> Boms => Set<Bom>();
    public DbSet<BomLine> BomLines => Set<BomLine>();
    public DbSet<TradeDocument> TradeDocuments => Set<TradeDocument>();
    public DbSet<TradeDocumentLine> TradeDocumentLines => Set<TradeDocumentLine>();
    public DbSet<Voucher> Vouchers => Set<Voucher>();
    public DbSet<VoucherLine> VoucherLines => Set<VoucherLine>();
    public DbSet<AccountEntry> AccountEntries => Set<AccountEntry>();
    public DbSet<StockMovement> StockMovements => Set<StockMovement>();
    public DbSet<BillOutstanding> BillOutstandings => Set<BillOutstanding>();
    public DbSet<BillSettlement> BillSettlements => Set<BillSettlement>();
    public DbSet<LoyaltyTransaction> LoyaltyTransactions => Set<LoyaltyTransaction>();

    protected override void ConfigureConventions(ModelConfigurationBuilder b)
    {
        b.Properties<decimal>().HavePrecision(18, 4);
        b.Properties<string>().HaveMaxLength(256);
    }

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        // Posted money amounts are stored at 2 decimals; rates, percentages, factors and quantities keep 4.
        foreach (var type in new[] { typeof(TradeDocument), typeof(Voucher), typeof(VoucherLine), typeof(AccountEntry),
                     typeof(BillOutstanding), typeof(BillSettlement) })
        foreach (var p in mb.Entity(type).Metadata.GetProperties().Where(p => p.ClrType == typeof(decimal)))
        {
            bool fourDp = p.Name.Contains("Rate") || p.Name.Contains("Percent") || p.Name.Contains("Factor");
            p.SetPrecision(18);
            p.SetScale(fourDp ? 4 : 2);
        }

        // All relationships restrict deletes unless configured otherwise (document lines cascade).
        foreach (var fk in mb.Model.GetEntityTypes().SelectMany(e => e.GetForeignKeys()).Where(fk => !fk.IsOwnership))
            if (fk.DeleteBehavior == DeleteBehavior.Cascade && !IsCascadeAllowed(fk.PrincipalEntityType.ClrType, fk.DeclaringEntityType.ClrType))
                fk.DeleteBehavior = DeleteBehavior.Restrict;

        // Tenant isolation: every ITenantEntity is filtered to the signed-in company.
        foreach (var et in mb.Model.GetEntityTypes().Where(t => typeof(ITenantEntity).IsAssignableFrom(t.ClrType)))
        {
            var method = typeof(AppDbContext).GetMethod(nameof(TenantFilter), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .MakeGenericMethod(et.ClrType);
            mb.Entity(et.ClrType).HasQueryFilter((LambdaExpression)method.Invoke(this, null)!);
        }
    }

    private static bool IsCascadeAllowed(Type principal, Type dependent) =>
        (principal, dependent) switch
        {
            _ when principal == typeof(TradeDocument) && dependent == typeof(TradeDocumentLine) => true,
            _ when principal == typeof(Voucher) && dependent == typeof(VoucherLine) => true,
            _ when principal == typeof(Bom) && dependent == typeof(BomLine) => true,
            _ when principal == typeof(Role) && dependent == typeof(RolePermission) => true,
            _ when principal == typeof(User) && dependent == typeof(UserCompany) => true,
            _ when principal == typeof(Product) && dependent == typeof(ProductImage) => true,
            _ when principal == typeof(BillOutstanding) && dependent == typeof(BillSettlement) => true,
            _ when principal == typeof(PriceList) && dependent == typeof(PriceListItem) => true,
            _ => false,
        };

    private Expression<Func<T, bool>> TenantFilter<T>() where T : class, ITenantEntity =>
        e => TenantIgnored || e.CompanyId == TenantId;

    public override Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        StampChanges();
        return base.SaveChangesAsync(ct);
    }

    public override int SaveChanges()
    {
        StampChanges();
        return base.SaveChanges();
    }

    private void StampChanges()
    {
        var now = _clock?.Now ?? DateTimeOffset.UtcNow;
        long? userId = _user is { IsAuthenticated: true } ? _user.UserId : null;
        foreach (var e in ChangeTracker.Entries())
        {
            if (e.Entity is ITenantEntity t && e.State == EntityState.Added && t.CompanyId == 0)
                t.CompanyId = TenantId;
            if (e.Entity is ITenantEntity t2 && e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted
                && !TenantIgnored && t2.CompanyId != TenantId)
                throw new ForbiddenException("Cross-company write rejected.");
            if (e.Entity is IAuditable a)
            {
                if (e.State == EntityState.Added) { a.CreatedAt = now; a.CreatedBy = userId; }
                else if (e.State == EntityState.Modified) { a.UpdatedAt = now; a.UpdatedBy = userId; }
            }
        }
    }
}
