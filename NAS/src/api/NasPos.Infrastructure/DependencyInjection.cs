using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NasPos.Application.Accounting;
using NasPos.Application.Admin;
using NasPos.Application.Auth;
using NasPos.Application.Common;
using NasPos.Application.Documents;
using NasPos.Application.Help;
using NasPos.Application.Inventory;
using NasPos.Application.Licensing;
using NasPos.Application.Masters;
using NasPos.Application.Messaging;
using NasPos.Application.Printing;
using NasPos.Application.Reports;
using NasPos.Application.Setup;
using NasPos.Application.Tools;
using NasPos.Domain.Entities;
using NasPos.Infrastructure.Backup;
using NasPos.Infrastructure.Persistence;
using NasPos.Infrastructure.Services;

namespace NasPos.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        var cs = config.GetConnectionString("Default");
        if (string.IsNullOrWhiteSpace(cs))
            throw new InvalidOperationException("ConnectionStrings:Default is not configured. Set it via user-secrets or the ConnectionStrings__Default environment variable.");

        services.AddDbContext<AppDbContext>(o => o
            .UseSqlServer(cs, sql =>
            {
                sql.CommandTimeout(120);
                sql.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName);
            })
            // Child rows (lines) are only reached through their tenant-filtered parent, so this warning is expected.
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.PossibleIncorrectRequiredNavigationWithQueryFilterInteractionWarning)));
        services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());

        services.Configure<BusinessOptions>(config.GetSection("Business"));
        services.Configure<SmsOptions>(config.GetSection("Messaging:Sms"));
        services.Configure<EmailOptions>(config.GetSection("Messaging:Email"));
        services.AddMemoryCache();
        services.AddHttpClient("sms", c => c.Timeout = TimeSpan.FromSeconds(20));

        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IPermissionCache, PermissionCache>();
        services.AddSingleton<IMessageSender, MessageSender>();
        services.AddScoped<TenantOverride>();
        services.AddScoped<ITenantScopeFactory, TenantScopeFactory>();
        services.AddScoped<ICompanyBackupService, CompanyBackupService>();
        services.AddHostedService<OutboxDispatcher>();
        services.AddScoped<IPasswordHasher<User>, PasswordHasher<User>>();

        // Application services
        services.AddScoped<AuditService>();
        services.AddScoped<SystemLedgerResolver>();
        services.AddScoped<SettingsService>();
        services.AddScoped<NumberingService>();
        services.AddScoped<FinancialYearService>();
        services.AddScoped<CompanySetupService>();
        services.AddScoped<AuthService>();
        services.AddScoped<StockQueries>();
        services.AddScoped<LedgerQueries>();
        services.AddScoped<DocumentService>();
        services.AddScoped<DocumentMessaging>();
        services.AddScoped<MessagingService>();
        services.AddScoped<VoucherService>();
        services.AddScoped<YearEndService>();
        services.AddScoped<LookupService>();
        services.AddScoped<AccountGroupService>();
        services.AddScoped<LedgerService>();
        services.AddScoped<EmployeeService>();
        services.AddScoped<LoyaltyService>();
        services.AddScoped<ProductService>();
        services.AddScoped<OfferService>();
        services.AddScoped<PriceListService>();
        services.AddScoped<BomService>();
        services.AddScoped<ReportData>();
        services.AddScoped<TradeReports>();
        services.AddScoped<StockReports>();
        services.AddScoped<AccountReports>();
        services.AddScoped<TaxReports>();
        services.AddScoped<IReportProvider>(sp => sp.GetRequiredService<TradeReports>());
        services.AddScoped<IReportProvider>(sp => sp.GetRequiredService<StockReports>());
        services.AddScoped<IReportProvider>(sp => sp.GetRequiredService<AccountReports>());
        services.AddScoped<IReportProvider>(sp => sp.GetRequiredService<TaxReports>());
        services.AddScoped<ReportService>();
        services.AddScoped<PrintService>();
        services.AddScoped<CompanyService>();
        services.AddScoped<UserService>();
        services.AddScoped<RoleService>();
        services.AddScoped<AuditQueryService>();
        services.AddScoped<DashboardService>();
        services.AddScoped<ImportExportService>();
        services.AddScoped<TransferService>();
        services.AddScoped<ResetService>();
        services.AddScoped<LicenseService>();
        services.AddScoped<FaqService>();
        services.AddScoped<SupportService>();
        return services;
    }
}
