using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Kshethra.Domain;
using Kshethra.Domain.Entities;

namespace Kshethra.Infrastructure.Persistence;

internal static class ConfigExtensions
{
    public static void Tenant<T>(this EntityTypeBuilder<T> b) where T : class, ITenantEntity =>
        b.HasOne<Company>().WithMany().HasForeignKey(x => x.CompanyId).OnDelete(DeleteBehavior.Restrict);

    public static PropertyBuilder<T> Max<T>(this PropertyBuilder<T> p) => p.HasColumnType("nvarchar(max)");
}

internal class CompanyConfig : IEntityTypeConfiguration<Company>
{
    public void Configure(EntityTypeBuilder<Company> b)
    {
        b.HasIndex(x => x.Name).IsUnique();
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.Property(x => x.StateCode).HasMaxLength(2);
        b.Property(x => x.Gstin).HasMaxLength(15);
        b.Property(x => x.InvoiceFooter).Max();
        b.Property(x => x.Logo).HasColumnType("varbinary(max)");
        b.Property(x => x.RowVersion).IsRowVersion();
    }
}

internal class FinancialYearConfig : IEntityTypeConfiguration<FinancialYear>
{
    public void Configure(EntityTypeBuilder<FinancialYear> b)
    {
        b.Tenant();
        b.HasIndex(x => new { x.CompanyId, x.StartDate }).IsUnique();
        b.Property(x => x.Name).HasMaxLength(20);
        b.HasOne<FinancialYear>().WithMany().HasForeignKey(x => x.PreviousYearId).OnDelete(DeleteBehavior.Restrict);
        b.ToTable(t => t.HasCheckConstraint("CK_FinancialYear_Range", "[EndDate] > [StartDate]"));
    }
}

internal class CompanySettingConfig : IEntityTypeConfiguration<CompanySetting>
{
    public void Configure(EntityTypeBuilder<CompanySetting> b)
    {
        b.Tenant();
        b.HasIndex(x => new { x.CompanyId, x.Key }).IsUnique();
        b.Property(x => x.Key).HasMaxLength(100);
        b.Property(x => x.Value).HasMaxLength(1000);
    }
}

internal class InvoiceSettingConfig : IEntityTypeConfiguration<InvoiceSetting>
{
    public void Configure(EntityTypeBuilder<InvoiceSetting> b)
    {
        b.Tenant();
        b.HasIndex(x => new { x.CompanyId, x.VoucherType }).IsUnique();
        b.Property(x => x.OptionsJson).Max();
    }
}

internal class PrintSchemeConfig : IEntityTypeConfiguration<PrintScheme>
{
    public void Configure(EntityTypeBuilder<PrintScheme> b)
    {
        b.Tenant();
        b.HasIndex(x => new { x.CompanyId, x.VoucherType, x.Name }).IsUnique();
        b.Property(x => x.TemplateJson).Max();
        b.Property(x => x.RowVersion).IsRowVersion();
    }
}

internal class LabelTemplateConfig : IEntityTypeConfiguration<LabelTemplate>
{
    public void Configure(EntityTypeBuilder<LabelTemplate> b)
    {
        b.Tenant();
        b.HasIndex(x => new { x.CompanyId, x.Name }).IsUnique();
        b.Property(x => x.FieldsJson).Max();
        b.Property(x => x.RowVersion).IsRowVersion();
    }
}

internal class DocumentSequenceConfig : IEntityTypeConfiguration<DocumentSequence>
{
    public void Configure(EntityTypeBuilder<DocumentSequence> b)
    {
        b.Tenant();
        b.HasIndex(x => new { x.CompanyId, x.FinancialYearId, x.VoucherType }).IsUnique();
        b.HasOne<FinancialYear>().WithMany().HasForeignKey(x => x.FinancialYearId).OnDelete(DeleteBehavior.Restrict);
        b.Property(x => x.Prefix).HasMaxLength(20);
        b.ToTable(t => t.HasCheckConstraint("CK_DocumentSequence_Next", "[NextNumber] >= 1"));
    }
}

internal class UserConfig : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> b)
    {
        b.HasIndex(x => x.NormalizedUserName).IsUnique();
        b.Property(x => x.UserName).HasMaxLength(64);
        b.Property(x => x.NormalizedUserName).HasMaxLength(64);
        b.Property(x => x.PasswordHash).HasMaxLength(512);
        b.Property(x => x.Theme).HasMaxLength(16).HasDefaultValue("light");
        b.ToTable(t => t.HasCheckConstraint("CK_User_Theme", "[Theme] IN ('light', 'dark', 'system', 'ocean', 'sunset')"));
        b.HasMany(x => x.Companies).WithOne(x => x.User).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal class UserCompanyConfig : IEntityTypeConfiguration<UserCompany>
{
    public void Configure(EntityTypeBuilder<UserCompany> b)
    {
        b.HasKey(x => new { x.UserId, x.CompanyId });
        b.HasOne(x => x.Company).WithMany().HasForeignKey(x => x.CompanyId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Role).WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal class RoleConfig : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> b)
    {
        b.HasIndex(x => x.Name).IsUnique();
        b.Property(x => x.Name).HasMaxLength(64);
        b.HasMany(x => x.Permissions).WithOne().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal class RolePermissionConfig : IEntityTypeConfiguration<RolePermission>
{
    public void Configure(EntityTypeBuilder<RolePermission> b)
    {
        b.HasIndex(x => new { x.RoleId, x.Screen }).IsUnique();
        b.Property(x => x.Screen).HasMaxLength(64);
    }
}

internal class RefreshTokenConfig : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> b)
    {
        b.HasIndex(x => x.TokenHash).IsUnique();
        b.Property(x => x.TokenHash).HasMaxLength(128);
        b.Property(x => x.ReplacedByHash).HasMaxLength(128);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal class UserActionLogConfig : IEntityTypeConfiguration<UserActionLog>
{
    public void Configure(EntityTypeBuilder<UserActionLog> b)
    {
        b.HasIndex(x => new { x.CompanyId, x.At });
        b.Property(x => x.Remarks).HasMaxLength(1000);
        b.HasOne<Company>().WithMany().HasForeignKey(x => x.CompanyId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal class VoidItemConfig : IEntityTypeConfiguration<VoidItem>
{
    public void Configure(EntityTypeBuilder<VoidItem> b)
    {
        b.Tenant();
        b.HasIndex(x => new { x.CompanyId, x.At });
    }
}

internal class SmsEventConfig : IEntityTypeConfiguration<SmsEvent>
{
    public void Configure(EntityTypeBuilder<SmsEvent> b)
    {
        b.Tenant();
        b.HasIndex(x => new { x.CompanyId, x.Name }).IsUnique();
        b.Property(x => x.RowVersion).IsRowVersion();
    }
}

internal class SmsTemplateConfig : IEntityTypeConfiguration<SmsTemplate>
{
    public void Configure(EntityTypeBuilder<SmsTemplate> b)
    {
        b.Tenant();
        b.HasIndex(x => new { x.CompanyId, x.Name }).IsUnique();
        b.Property(x => x.Body).Max();
        b.HasOne(x => x.Event).WithMany().HasForeignKey(x => x.EventId).OnDelete(DeleteBehavior.Restrict);
        b.Property(x => x.RowVersion).IsRowVersion();
    }
}

internal class SmsScheduleConfig : IEntityTypeConfiguration<SmsSchedule>
{
    public void Configure(EntityTypeBuilder<SmsSchedule> b)
    {
        b.Tenant();
        b.HasOne<Ledger>().WithMany().HasForeignKey(x => x.LedgerId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<SmsTemplate>().WithMany().HasForeignKey(x => x.TemplateId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.CompanyId, x.Processed, x.ScheduledAt });
        b.Property(x => x.RowVersion).IsRowVersion();
    }
}

internal class TempleConfig : IEntityTypeConfiguration<Deity>, IEntityTypeConfiguration<PoojaSlot>, IEntityTypeConfiguration<Devotee>,
    IEntityTypeConfiguration<Booking>, IEntityTypeConfiguration<BookingLine>, IEntityTypeConfiguration<BookingOccurrence>,
    IEntityTypeConfiguration<CalendarCorrection>, IEntityTypeConfiguration<Donation>
{
    public void Configure(EntityTypeBuilder<Deity> b)
    {
        b.Tenant();
        b.HasIndex(x => new { x.CompanyId, x.Name }).IsUnique();
        b.Property(x => x.Name).HasMaxLength(100);
        b.Property(x => x.RegionalName).HasMaxLength(100);
        b.Property(x => x.RowVersion).IsRowVersion();
    }

    public void Configure(EntityTypeBuilder<PoojaSlot> b)
    {
        b.Tenant();
        b.HasIndex(x => new { x.CompanyId, x.Name }).IsUnique();
        b.Property(x => x.Name).HasMaxLength(100);
        b.Property(x => x.RegionalName).HasMaxLength(100);
        b.Property(x => x.RowVersion).IsRowVersion();
    }

    public void Configure(EntityTypeBuilder<Devotee> b)
    {
        b.Tenant();
        b.HasIndex(x => new { x.CompanyId, x.Name });
        b.HasIndex(x => new { x.CompanyId, x.Phone });
        b.Property(x => x.Name).HasMaxLength(150);
        b.Property(x => x.RegionalName).HasMaxLength(150);
        b.Property(x => x.HouseName).HasMaxLength(150);
        b.Property(x => x.Phone).HasMaxLength(30);
        b.Property(x => x.Address).HasMaxLength(500);
        b.Property(x => x.City).HasMaxLength(100);
        b.Property(x => x.Email).HasMaxLength(200);
        b.Property(x => x.Notes).HasMaxLength(1000);
        b.Property(x => x.RowVersion).IsRowVersion();
    }

    public void Configure(EntityTypeBuilder<Booking> b)
    {
        b.Tenant();
        b.HasIndex(x => new { x.CompanyId, x.BookingNo });
        b.HasIndex(x => new { x.CompanyId, x.BookedAt });
        b.HasIndex(x => x.DocumentId).IsUnique();
        b.HasOne<TradeDocument>().WithMany().HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Devotee>().WithMany().HasForeignKey(x => x.DevoteeId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(l => l.BookingId).OnDelete(DeleteBehavior.Cascade);
        b.Property(x => x.BookingNo).HasMaxLength(40);
        b.Property(x => x.ContactName).HasMaxLength(150);
        b.Property(x => x.Phone).HasMaxLength(30);
        b.Property(x => x.Address).HasMaxLength(500);
        b.Property(x => x.Notes).HasMaxLength(1000);
        b.Property(x => x.UserName).HasMaxLength(100);
        b.Property(x => x.Total).HasPrecision(18, 2);
        b.Property(x => x.RowVersion).IsRowVersion();
    }

    public void Configure(EntityTypeBuilder<BookingLine> b)
    {
        b.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Deity>().WithMany().HasForeignKey(x => x.DeityId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<PoojaSlot>().WithMany().HasForeignKey(x => x.PoojaSlotId).OnDelete(DeleteBehavior.Restrict);
        b.Property(x => x.DevoteeName).HasMaxLength(150);
        b.Property(x => x.ScheduleText).HasMaxLength(300);
        b.Property(x => x.Remarks).HasMaxLength(300);
        b.Property(x => x.Amount).HasPrecision(18, 2);
    }

    public void Configure(EntityTypeBuilder<BookingOccurrence> b)
    {
        b.Tenant();
        b.HasOne(x => x.Line).WithMany().HasForeignKey(x => x.BookingLineId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Booking>().WithMany().HasForeignKey(x => x.BookingId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.CompanyId, x.Date, x.Status });
        b.Property(x => x.DoneBy).HasMaxLength(100);
    }

    public void Configure(EntityTypeBuilder<CalendarCorrection> b)
    {
        b.Tenant();
        b.HasIndex(x => new { x.CompanyId, x.Date }).IsUnique();
        b.Property(x => x.Festival).HasMaxLength(200);
        b.Property(x => x.RowVersion).IsRowVersion();
    }

    public void Configure(EntityTypeBuilder<Donation> b)
    {
        b.Tenant();
        b.HasIndex(x => new { x.CompanyId, x.Date });
        b.HasOne(x => x.PurposeLedger).WithMany().HasForeignKey(x => x.PurposeLedgerId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Voucher>().WithMany().HasForeignKey(x => x.VoucherId).OnDelete(DeleteBehavior.Restrict);
        b.Property(x => x.ReceiptNo).HasMaxLength(40);
        b.Property(x => x.DonorName).HasMaxLength(150);
        b.Property(x => x.HouseName).HasMaxLength(150);
        b.Property(x => x.Phone).HasMaxLength(30);
        b.Property(x => x.Address).HasMaxLength(500);
        b.Property(x => x.Reference).HasMaxLength(100);
        b.Property(x => x.Pan).HasMaxLength(10);
        b.Property(x => x.Notes).HasMaxLength(500);
        b.Property(x => x.UserName).HasMaxLength(100);
        b.Property(x => x.Amount).HasPrecision(18, 2);
        b.Property(x => x.RowVersion).IsRowVersion();
    }
}

internal class OutboundMessageConfig : IEntityTypeConfiguration<OutboundMessage>
{
    public void Configure(EntityTypeBuilder<OutboundMessage> b)
    {
        b.Tenant();
        b.HasIndex(x => new { x.Status, x.CreatedAt });
        b.Property(x => x.Body).Max();
        b.Property(x => x.LastError).HasMaxLength(2000);
        b.Property(x => x.Attachment).HasColumnType("varbinary(max)");
        b.Property(x => x.RowVersion).IsRowVersion();
    }
}

internal class FaqConfig : IEntityTypeConfiguration<FaqEntry>, IEntityTypeConfiguration<FaqQuestionLog>
{
    public void Configure(EntityTypeBuilder<FaqEntry> b)
    {
        b.Tenant();
        b.Property(x => x.Question).HasMaxLength(300);
        b.Property(x => x.Variants).HasMaxLength(4000);
        b.Property(x => x.Answer).Max();
        b.Property(x => x.Category).HasMaxLength(60);
        b.Property(x => x.Tags).HasMaxLength(1000);
        b.Property(x => x.Screens).HasMaxLength(1000);
        b.Property(x => x.Link).HasMaxLength(200);
        b.Property(x => x.RowVersion).IsRowVersion();
    }

    public void Configure(EntityTypeBuilder<FaqQuestionLog> b)
    {
        b.Tenant();
        b.Property(x => x.Text).HasMaxLength(300);
        b.Property(x => x.Reason).HasMaxLength(20);
        b.Property(x => x.LastUserName).HasMaxLength(100);
        b.HasIndex(x => new { x.CompanyId, x.LastAskedAt });
        b.Property(x => x.RowVersion).IsRowVersion();
    }
}

internal class AccountGroupConfig : IEntityTypeConfiguration<AccountGroup>
{
    public void Configure(EntityTypeBuilder<AccountGroup> b)
    {
        b.Tenant();
        b.HasIndex(x => new { x.CompanyId, x.Name }).IsUnique();
        b.HasIndex(x => new { x.CompanyId, x.SystemCode }).IsUnique().HasFilter("[SystemCode] IS NOT NULL");
        b.HasOne(x => x.Parent).WithMany().HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.Restrict);
        b.Property(x => x.Name).HasMaxLength(100);
        b.Property(x => x.RowVersion).IsRowVersion();
    }
}

internal class LedgerConfig : IEntityTypeConfiguration<Ledger>
{
    public void Configure(EntityTypeBuilder<Ledger> b)
    {
        b.Tenant();
        b.HasIndex(x => new { x.CompanyId, x.Code }).IsUnique();
        // Corporate parties may share a name (branches, sister firms); every other ledger name must be unique.
        b.HasIndex(x => new { x.CompanyId, x.Name }).IsUnique().HasFilter("[IsCorporate] = 0").HasDatabaseName("IX_Ledgers_CompanyId_Name_Individual");
        b.HasIndex(x => new { x.CompanyId, x.Name, x.IsCorporate });
        b.HasIndex(x => new { x.CompanyId, x.SystemCode }).IsUnique().HasFilter("[SystemCode] IS NOT NULL");
        b.HasIndex(x => new { x.CompanyId, x.PartyType });
        b.HasIndex(x => new { x.CompanyId, x.Mobile });
        b.Property(x => x.Code).HasMaxLength(32);
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.Gstin).HasMaxLength(15);
        b.Property(x => x.StateCode).HasMaxLength(2);
        b.Property(x => x.Remarks).HasMaxLength(1000);
        b.HasOne(x => x.AccountGroup).WithMany().HasForeignKey(x => x.AccountGroupId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Area).WithMany().HasForeignKey(x => x.AreaId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.PriceList).WithMany().HasForeignKey(x => x.PriceListId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.LoyaltyScheme).WithMany().HasForeignKey(x => x.LoyaltySchemeId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Employee>().WithMany().HasForeignKey(x => x.SalespersonId).OnDelete(DeleteBehavior.Restrict);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.ToTable(t =>
        {
            t.HasCheckConstraint("CK_Ledger_CreditLimit", "[CreditLimit] >= 0");
            t.HasCheckConstraint("CK_Ledger_Discount", "[DiscountPercent] >= 0 AND [DiscountPercent] <= 100");
        });
    }
}

internal class SimpleMasterConfig : IEntityTypeConfiguration<Area>, IEntityTypeConfiguration<Category>,
    IEntityTypeConfiguration<Manufacturer>, IEntityTypeConfiguration<PriceList>, IEntityTypeConfiguration<LoyaltyScheme>
{
    public void Configure(EntityTypeBuilder<Area> b) { b.Tenant(); b.HasIndex(x => new { x.CompanyId, x.Name }).IsUnique(); b.Property(x => x.Name).HasMaxLength(100); b.Property(x => x.RowVersion).IsRowVersion(); }
    public void Configure(EntityTypeBuilder<Category> b) { b.Tenant(); b.HasIndex(x => new { x.CompanyId, x.Name }).IsUnique(); b.Property(x => x.Name).HasMaxLength(100); b.Property(x => x.Color).HasMaxLength(16); b.Property(x => x.RowVersion).IsRowVersion(); }
    public void Configure(EntityTypeBuilder<Manufacturer> b) { b.Tenant(); b.HasIndex(x => new { x.CompanyId, x.Name }).IsUnique(); b.Property(x => x.Name).HasMaxLength(100); b.Property(x => x.RowVersion).IsRowVersion(); }
    public void Configure(EntityTypeBuilder<PriceList> b) { b.Tenant(); b.HasIndex(x => new { x.CompanyId, x.Name }).IsUnique(); b.Property(x => x.Name).HasMaxLength(100); b.Property(x => x.RowVersion).IsRowVersion(); }
    public void Configure(EntityTypeBuilder<LoyaltyScheme> b)
    {
        b.Tenant(); b.HasIndex(x => new { x.CompanyId, x.Name }).IsUnique(); b.Property(x => x.Name).HasMaxLength(100); b.Property(x => x.RowVersion).IsRowVersion();
        b.ToTable(t => t.HasCheckConstraint("CK_Loyalty_Values", "[SaleAmount] >= 0 AND [Points] >= 0 AND [TargetPoints] >= 0 AND [PointValue] >= 0"));
    }
}

internal class PriceListItemConfig : IEntityTypeConfiguration<PriceListItem>
{
    public void Configure(EntityTypeBuilder<PriceListItem> b)
    {
        b.Tenant();
        b.HasIndex(x => new { x.PriceListId, x.ProductId }).IsUnique();
        b.HasOne<PriceList>().WithMany().HasForeignKey(x => x.PriceListId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        b.ToTable(t => t.HasCheckConstraint("CK_PriceListItem_Price", "[Price] >= 0"));
    }
}

internal class EmployeeConfig : IEntityTypeConfiguration<Employee>
{
    public void Configure(EntityTypeBuilder<Employee> b)
    {
        b.Tenant();
        b.HasIndex(x => new { x.CompanyId, x.Code }).IsUnique();
        b.Property(x => x.Code).HasMaxLength(32);
        b.Property(x => x.Name).HasMaxLength(200);
        b.HasOne(x => x.Ledger).WithMany().HasForeignKey(x => x.LedgerId).OnDelete(DeleteBehavior.Restrict);
        b.Property(x => x.RowVersion).IsRowVersion();
    }
}

internal class ProductConfig : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> b)
    {
        b.Tenant();
        b.HasIndex(x => new { x.CompanyId, x.Code }).IsUnique();
        b.HasIndex(x => new { x.CompanyId, x.DefaultBarcode }).IsUnique().HasFilter("[DefaultBarcode] IS NOT NULL");
        b.HasIndex(x => new { x.CompanyId, x.PluCode }).IsUnique().HasFilter("[PluCode] IS NOT NULL");
        b.HasIndex(x => new { x.CompanyId, x.Name });
        b.HasIndex(x => new { x.CompanyId, x.CategoryId });
        b.Property(x => x.Code).HasMaxLength(64);
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.HsnCode).HasMaxLength(16);
        b.Property(x => x.MainUnit).HasMaxLength(20);
        b.Property(x => x.AltUnit).HasMaxLength(20);
        b.Property(x => x.DefaultBarcode).HasMaxLength(64);
        b.Property(x => x.PluCode).HasMaxLength(16);
        b.Property(x => x.Remarks).HasMaxLength(1000);
        b.HasOne(x => x.Category).WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Manufacturer).WithMany().HasForeignKey(x => x.ManufacturerId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Images).WithOne().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Cascade);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.ToTable(t =>
        {
            t.HasCheckConstraint("CK_Product_Factor", "[ConversionFactor] > 0");
            t.HasCheckConstraint("CK_Product_Rates", "[PurchaseRate] >= 0 AND [SaleRate] >= 0 AND [Mrp] >= 0");
            t.HasCheckConstraint("CK_Product_Tax", "[CgstRate] >= 0 AND [SgstRate] >= 0 AND [IgstRate] >= 0 AND [CessRate] >= 0 AND [KfCessRate] >= 0 AND [AddCessPerUnit] >= 0");
        });
    }
}

internal class ProductImageConfig : IEntityTypeConfiguration<ProductImage>
{
    public void Configure(EntityTypeBuilder<ProductImage> b)
    {
        b.Property(x => x.Content).HasColumnType("varbinary(max)");
        b.Property(x => x.ContentType).HasMaxLength(64);
    }
}

internal class BatchConfig : IEntityTypeConfiguration<Batch>
{
    public void Configure(EntityTypeBuilder<Batch> b)
    {
        b.Tenant();
        b.HasIndex(x => new { x.CompanyId, x.ProductId, x.Code, x.Mrp }).IsUnique();
        b.HasIndex(x => new { x.CompanyId, x.Code });
        b.HasIndex(x => new { x.CompanyId, x.ExpiryDate });
        b.Property(x => x.Code).HasMaxLength(64);
        b.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.ToTable(t => t.HasCheckConstraint("CK_Batch_Rates", "[PurchaseRate] >= 0 AND [SaleRate] >= 0 AND [Mrp] >= 0 AND [Cost] >= 0"));
    }
}

internal class ItemOfferConfig : IEntityTypeConfiguration<ItemOffer>
{
    public void Configure(EntityTypeBuilder<ItemOffer> b)
    {
        b.Tenant();
        b.HasIndex(x => new { x.CompanyId, x.ProductId, x.StartAt, x.EndAt });
        b.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Batch>().WithMany().HasForeignKey(x => x.BatchId).OnDelete(DeleteBehavior.Restrict);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.ToTable(t => t.HasCheckConstraint("CK_ItemOffer_Window", "[EndAt] >= [StartAt] AND [NetRate] >= 0"));
    }
}

internal class BomConfig : IEntityTypeConfiguration<Bom>, IEntityTypeConfiguration<BomLine>
{
    public void Configure(EntityTypeBuilder<Bom> b)
    {
        b.Tenant();
        b.HasIndex(x => new { x.CompanyId, x.Name }).IsUnique();
        b.HasOne<Product>().WithMany().HasForeignKey(x => x.FinishedProductId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.BomId).OnDelete(DeleteBehavior.Cascade);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.ToTable(t => t.HasCheckConstraint("CK_Bom_BatchSize", "[BatchSize] > 0"));
    }

    public void Configure(EntityTypeBuilder<BomLine> b)
    {
        b.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        b.ToTable(t => t.HasCheckConstraint("CK_BomLine_Qty", "[Qty] > 0"));
    }
}

internal class TradeDocumentConfig : IEntityTypeConfiguration<TradeDocument>, IEntityTypeConfiguration<TradeDocumentLine>
{
    public void Configure(EntityTypeBuilder<TradeDocument> b)
    {
        b.Tenant();
        b.HasIndex(x => new { x.CompanyId, x.FinancialYearId, x.VoucherType, x.DocumentNo }).IsUnique();
        b.HasIndex(x => new { x.CompanyId, x.VoucherType, x.Date });
        b.HasIndex(x => new { x.CompanyId, x.PartyLedgerId });
        b.Property(x => x.DocumentNo).HasMaxLength(40);
        b.Property(x => x.Remarks).HasMaxLength(1000);
        b.Property(x => x.OrderDetails).HasMaxLength(1000);
        b.Property(x => x.DespatchDetails).HasMaxLength(1000);
        b.Property(x => x.CardLast4).HasMaxLength(4);
        b.Property(x => x.PartyGstin).HasMaxLength(15);
        b.Property(x => x.PartyStateCode).HasMaxLength(2);
        b.HasOne<FinancialYear>().WithMany().HasForeignKey(x => x.FinancialYearId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.PartyLedger).WithMany().HasForeignKey(x => x.PartyLedgerId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Salesperson).WithMany().HasForeignKey(x => x.SalespersonId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<PriceList>().WithMany().HasForeignKey(x => x.PriceListId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Ledger>().WithMany().HasForeignKey(x => x.CardLedgerId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<LoyaltyScheme>().WithMany().HasForeignKey(x => x.LoyaltySchemeId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Bom>().WithMany().HasForeignKey(x => x.BomId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<TradeDocument>().WithMany().HasForeignKey(x => x.SourceOrderId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Cascade);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.ToTable(t =>
        {
            t.HasCheckConstraint("CK_TradeDocument_Amounts", "[BillAmount] >= 0 AND [CashAmount] >= 0 AND [CardAmount] >= 0 AND [CreditAmount] >= 0 AND [CashDiscount] >= 0 AND [BillDiscount] >= 0");
            t.HasCheckConstraint("CK_TradeDocument_CardLast4", "[CardLast4] IS NULL OR LEN([CardLast4]) = 4");
        });
    }

    public void Configure(EntityTypeBuilder<TradeDocumentLine> b)
    {
        b.HasIndex(x => x.ProductId);
        b.HasIndex(x => x.BatchId);
        b.Property(x => x.BatchCode).HasMaxLength(64);
        b.Property(x => x.HsnCode).HasMaxLength(16);
        b.Property(x => x.Unit).HasMaxLength(20);
        b.Property(x => x.ExtraColumn).HasMaxLength(250);
        b.Property(x => x.PatternNo).HasMaxLength(250);
        b.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Batch).WithMany().HasForeignKey(x => x.BatchId).OnDelete(DeleteBehavior.Restrict);
        b.ToTable(t =>
        {
            t.HasCheckConstraint("CK_TradeLine_Qty", "[Qty] >= 0 AND [FreeQty] >= 0 AND [UnitFactor] > 0");
            t.HasCheckConstraint("CK_TradeLine_Amounts", "[Rate] >= 0 AND [Mrp] >= 0 AND [DiscountAmount] >= 0");
        });
    }
}

internal class VoucherConfig : IEntityTypeConfiguration<Voucher>, IEntityTypeConfiguration<VoucherLine>
{
    public void Configure(EntityTypeBuilder<Voucher> b)
    {
        b.Tenant();
        b.HasIndex(x => new { x.CompanyId, x.FinancialYearId, x.VoucherType, x.DocumentNo }).IsUnique();
        b.HasIndex(x => new { x.CompanyId, x.VoucherType, x.Date });
        b.Property(x => x.DocumentNo).HasMaxLength(40);
        b.Property(x => x.Narration).HasMaxLength(2000);
        b.HasOne<FinancialYear>().WithMany().HasForeignKey(x => x.FinancialYearId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.HeaderLedger).WithMany().HasForeignKey(x => x.HeaderLedgerId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Employee>().WithMany().HasForeignKey(x => x.SalespersonId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.VoucherId).OnDelete(DeleteBehavior.Cascade);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.ToTable(t => t.HasCheckConstraint("CK_Voucher_Total", "[Total] >= 0"));
    }

    public void Configure(EntityTypeBuilder<VoucherLine> b)
    {
        b.HasOne(x => x.Ledger).WithMany().HasForeignKey(x => x.LedgerId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Settlements).WithOne().HasForeignKey(x => x.VoucherLineId).OnDelete(DeleteBehavior.Restrict);
        b.Property(x => x.Narration).HasMaxLength(1000);
        b.ToTable(t => t.HasCheckConstraint("CK_VoucherLine_Amount", "[Amount] > 0"));
    }
}

internal class AccountEntryConfig : IEntityTypeConfiguration<AccountEntry>
{
    public void Configure(EntityTypeBuilder<AccountEntry> b)
    {
        b.Tenant();
        b.HasIndex(x => new { x.CompanyId, x.FinancialYearId, x.LedgerId, x.Date });
        b.HasIndex(x => new { x.CompanyId, x.FinancialYearId, x.Date });
        b.HasIndex(x => x.TradeDocumentId);
        b.HasIndex(x => x.VoucherId);
        b.Property(x => x.DocumentNo).HasMaxLength(40);
        b.Property(x => x.Remarks).HasMaxLength(500);
        b.Ignore(x => x.DocumentId);
        b.HasOne<FinancialYear>().WithMany().HasForeignKey(x => x.FinancialYearId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<TradeDocument>().WithMany().HasForeignKey(x => x.TradeDocumentId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Voucher>().WithMany().HasForeignKey(x => x.VoucherId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Ledger).WithMany().HasForeignKey(x => x.LedgerId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.ContraLedger).WithMany().HasForeignKey(x => x.ContraLedgerId).OnDelete(DeleteBehavior.Restrict);
        b.ToTable(t =>
        {
            t.HasCheckConstraint("CK_AccountEntry_Side", "[Debit] >= 0 AND [Credit] >= 0 AND ([Debit] = 0 OR [Credit] = 0) AND ([Debit] + [Credit]) > 0");
            t.HasCheckConstraint("CK_AccountEntry_Source", "([TradeDocumentId] IS NULL AND [VoucherId] IS NOT NULL) OR ([TradeDocumentId] IS NOT NULL AND [VoucherId] IS NULL)");
        });
    }
}

internal class StockMovementConfig : IEntityTypeConfiguration<StockMovement>
{
    public void Configure(EntityTypeBuilder<StockMovement> b)
    {
        b.Tenant();
        b.HasIndex(x => new { x.CompanyId, x.FinancialYearId, x.ProductId, x.BatchId });
        b.HasIndex(x => new { x.CompanyId, x.FinancialYearId, x.Date });
        b.HasIndex(x => x.TradeDocumentId);
        b.Property(x => x.DocumentNo).HasMaxLength(40);
        b.HasOne<FinancialYear>().WithMany().HasForeignKey(x => x.FinancialYearId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<TradeDocument>().WithMany().HasForeignKey(x => x.TradeDocumentId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Batch).WithMany().HasForeignKey(x => x.BatchId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Ledger>().WithMany().HasForeignKey(x => x.LedgerId).OnDelete(DeleteBehavior.Restrict);
        b.ToTable(t => t.HasCheckConstraint("CK_StockMovement_Qty", "[QtyIn] >= 0 AND [QtyOut] >= 0 AND ([QtyIn] > 0 OR [QtyOut] > 0)"));
    }
}

internal class BillOutstandingConfig : IEntityTypeConfiguration<BillOutstanding>, IEntityTypeConfiguration<BillSettlement>
{
    public void Configure(EntityTypeBuilder<BillOutstanding> b)
    {
        b.Tenant();
        b.HasIndex(x => new { x.CompanyId, x.LedgerId, x.Cancelled });
        b.HasIndex(x => x.TradeDocumentId);
        b.HasIndex(x => x.VoucherId);
        b.Property(x => x.DocumentNo).HasMaxLength(40);
        b.Ignore(x => x.Balance);
        b.HasOne(x => x.Ledger).WithMany().HasForeignKey(x => x.LedgerId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<TradeDocument>().WithMany().HasForeignKey(x => x.TradeDocumentId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Voucher>().WithMany().HasForeignKey(x => x.VoucherId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Settlements).WithOne(x => x.Bill).HasForeignKey(x => x.BillId).OnDelete(DeleteBehavior.Cascade);
        b.ToTable(t => t.HasCheckConstraint("CK_Bill_Amounts", "[Amount] >= 0 AND [Settled] >= 0 AND [Settled] <= [Amount]"));
    }

    public void Configure(EntityTypeBuilder<BillSettlement> b)
    {
        b.HasOne<TradeDocument>().WithMany().HasForeignKey(x => x.TradeDocumentId).OnDelete(DeleteBehavior.Restrict);
        b.ToTable(t => t.HasCheckConstraint("CK_BillSettlement_Amount", "[Amount] > 0"));
    }
}

internal class LoyaltyTransactionConfig : IEntityTypeConfiguration<LoyaltyTransaction>
{
    public void Configure(EntityTypeBuilder<LoyaltyTransaction> b)
    {
        b.Tenant();
        b.HasIndex(x => new { x.CompanyId, x.FinancialYearId, x.LedgerId });
        b.HasIndex(x => x.TradeDocumentId);
        b.HasOne<FinancialYear>().WithMany().HasForeignKey(x => x.FinancialYearId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Ledger>().WithMany().HasForeignKey(x => x.LedgerId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<LoyaltyScheme>().WithMany().HasForeignKey(x => x.SchemeId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<TradeDocument>().WithMany().HasForeignKey(x => x.TradeDocumentId).OnDelete(DeleteBehavior.Restrict);
        b.ToTable(t => t.HasCheckConstraint("CK_Loyalty_Points", "[PointsIn] >= 0 AND [PointsOut] >= 0"));
    }
}
