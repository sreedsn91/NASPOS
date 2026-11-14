using Microsoft.EntityFrameworkCore;

namespace NasLicenseManager.Data;

/// <summary>Someone at the vendor who can sign in and issue keys.</summary>
public class StaffUser
{
    public int Id { get; set; }
    public string UserName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public bool IsAdmin { get; set; }
    public bool IsActive { get; set; } = true;
    public int FailedLogins { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A shop (company) that buys NAS POS Pro subscriptions.</summary>
public class Customer
{
    public int Id { get; set; }
    /// <summary>Exactly as the shop types it in NAS POS Pro (capitals, spaces and punctuation don't matter).</summary>
    public string CompanyName { get; set; } = "";
    public string? Gstin { get; set; }
    public string? ContactPerson { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? City { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string CreatedBy { get; set; } = "";
    public List<IssuedLicense> Licenses { get; set; } = [];
}

/// <summary>One key issued to a customer — the permanent record of what was sold.</summary>
public class IssuedLicense
{
    public int Id { get; set; }
    public int CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public string KeyId { get; set; } = "";
    /// <summary>Subscription length in months (1 = a one-month trial); null = lifetime.</summary>
    public int? Months { get; set; }
    /// <summary>Most active users in NAS POS Pro; null = unlimited.</summary>
    public int? MaxUsers { get; set; }
    public DateOnly StartsOn { get; set; }
    /// <summary>Last valid day; null = lifetime.</summary>
    public DateOnly? ExpiresOn { get; set; }
    /// <summary>Company name and GSTIN the key was signed for (the customer record may change later).</summary>
    public string CompanyName { get; set; } = "";
    public string? Gstin { get; set; }
    public string Key { get; set; } = "";
    public string? Notes { get; set; }
    public DateTimeOffset IssuedAt { get; set; }
    public string IssuedBy { get; set; } = "";
}

public class LicensingDb(DbContextOptions<LicensingDb> options) : DbContext(options)
{
    public DbSet<StaffUser> StaffUsers => Set<StaffUser>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<IssuedLicense> IssuedLicenses => Set<IssuedLicense>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<StaffUser>(b =>
        {
            b.HasIndex(x => x.UserName).IsUnique();
            b.Property(x => x.UserName).HasMaxLength(60);
            b.Property(x => x.DisplayName).HasMaxLength(100);
            b.Property(x => x.PasswordHash).HasMaxLength(200);
        });
        mb.Entity<Customer>(b =>
        {
            b.HasIndex(x => x.CompanyName);
            b.Property(x => x.CompanyName).HasMaxLength(200);
            b.Property(x => x.Gstin).HasMaxLength(15);
            b.Property(x => x.ContactPerson).HasMaxLength(100);
            b.Property(x => x.Phone).HasMaxLength(40);
            b.Property(x => x.Email).HasMaxLength(200);
            b.Property(x => x.City).HasMaxLength(100);
            b.Property(x => x.Notes).HasMaxLength(2000);
            b.Property(x => x.CreatedBy).HasMaxLength(60);
        });
        mb.Entity<IssuedLicense>(b =>
        {
            b.HasOne(x => x.Customer).WithMany(c => c.Licenses).HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(x => x.KeyId).IsUnique();
            b.HasIndex(x => x.ExpiresOn);
            b.Property(x => x.KeyId).HasMaxLength(12);
            b.Property(x => x.CompanyName).HasMaxLength(200);
            b.Property(x => x.Gstin).HasMaxLength(15);
            b.Property(x => x.Key).HasMaxLength(300);
            b.Property(x => x.Notes).HasMaxLength(1000);
            b.Property(x => x.IssuedBy).HasMaxLength(60);
        });
    }
}
