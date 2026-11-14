namespace NasPos.Domain.Entities;

public class Company : Entity, IAuditable
{
    public string Name { get; set; } = "";
    public string? LegalName { get; set; }
    public string? Address1 { get; set; }
    public string? Address2 { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    /// <summary>Two-digit GST state code (e.g. "32" Kerala). Drives intra/inter-state tax.</summary>
    public string StateCode { get; set; } = "";
    public string? Country { get; set; } = "India";
    public string? PostalCode { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Gstin { get; set; }
    public string? Pan { get; set; }
    public string? Website { get; set; }
    public string? InvoiceFooter { get; set; }
    public byte[]? Logo { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsHidden { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public long? CreatedBy { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public long? UpdatedBy { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public class FinancialYear : Entity, ITenantEntity
{
    public long CompanyId { get; set; }
    public string Name { get; set; } = "";
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    /// <summary>Closed years are read-only; no document may be posted into them.</summary>
    public bool IsClosed { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public long? ClosedBy { get; set; }
    public long? PreviousYearId { get; set; }

    public bool Contains(DateOnly d) => d >= StartDate && d <= EndDate;
}

/// <summary>Key/value feature switch loaded into the typed settings object at login.</summary>
public class CompanySetting : Entity, ITenantEntity
{
    public long CompanyId { get; set; }
    public string Key { get; set; } = "";
    public string? Value { get; set; }
}

/// <summary>Per-voucher-type screen options (print on save, extra column, decimals...). Stored as JSON.</summary>
public class InvoiceSetting : Entity, ITenantEntity
{
    public long CompanyId { get; set; }
    public VoucherType VoucherType { get; set; }
    public string OptionsJson { get; set; } = "{}";
}

public class PrintScheme : TenantEntity
{
    public VoucherType VoucherType { get; set; }
    public string Name { get; set; } = "";
    public PrintFormat Format { get; set; } = PrintFormat.A4;
    public bool IsDefault { get; set; }
    /// <summary>Plain-text receipt mode for thermal printers.</summary>
    public bool TextMode { get; set; }
    /// <summary>Template layout: header lines, visible fields, grid columns, footer.</summary>
    public string TemplateJson { get; set; } = "{}";
}

public class LabelTemplate : TenantEntity
{
    public string Name { get; set; } = "";
    public decimal WidthMm { get; set; } = 50;
    public decimal HeightMm { get; set; } = 25;
    public int Columns { get; set; } = 2;
    public decimal GapMm { get; set; } = 2;
    public string FieldsJson { get; set; } = "{}";
    public bool IsDefault { get; set; }
}

public class DocumentSequence : Entity, ITenantEntity
{
    public long CompanyId { get; set; }
    public long FinancialYearId { get; set; }
    public VoucherType VoucherType { get; set; }
    public string Prefix { get; set; } = "";
    public long NextNumber { get; set; } = 1;
}

public class User : Entity, IAuditable
{
    public string UserName { get; set; } = "";
    public string NormalizedUserName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? Email { get; set; }
    public string PasswordHash { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public int FailedLoginCount { get; set; }
    public DateTimeOffset? LockoutEnd { get; set; }
    /// <summary>Bumped on password change / forced logout; embedded in tokens.</summary>
    public string SecurityStamp { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary>UI theme chosen by the user: "light", "dark", "ocean", "sunset" or "system". Follows the user to every computer.</summary>
    public string Theme { get; set; } = "light";
    public DateTimeOffset CreatedAt { get; set; }
    public long? CreatedBy { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public long? UpdatedBy { get; set; }
    public List<UserCompany> Companies { get; set; } = [];
}

/// <summary>A user's membership of a company, with the role they hold there.</summary>
public class UserCompany
{
    public long UserId { get; set; }
    public long CompanyId { get; set; }
    public long RoleId { get; set; }
    public User? User { get; set; }
    public Company? Company { get; set; }
    public Role? Role { get; set; }
}

public class Role : Entity
{
    public string Name { get; set; } = "";
    public bool IsAdmin { get; set; }
    public bool IsActive { get; set; } = true;
    public List<RolePermission> Permissions { get; set; } = [];
}

public class RolePermission : Entity
{
    public long RoleId { get; set; }
    public string Screen { get; set; } = "";
    public Right Rights { get; set; }
}

public class RefreshToken : Entity
{
    public long UserId { get; set; }
    public long CompanyId { get; set; }
    public string TokenHash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? ReplacedByHash { get; set; }
    public string? CreatedByIp { get; set; }
}

public class UserActionLog : Entity
{
    public long? CompanyId { get; set; }
    public long? UserId { get; set; }
    public string UserName { get; set; } = "";
    public string Screen { get; set; } = "";
    public string Action { get; set; } = "";
    public string? Remarks { get; set; }
    public string? Machine { get; set; }
    public DateTimeOffset At { get; set; }
}

/// <summary>Item removed from a bill before it was saved.</summary>
public class VoidItem : Entity, ITenantEntity
{
    public long CompanyId { get; set; }
    public long? UserId { get; set; }
    public string UserName { get; set; } = "";
    public string Screen { get; set; } = "";
    public string? DocumentNo { get; set; }
    public long? ProductId { get; set; }
    public string ProductName { get; set; } = "";
    public string? Batch { get; set; }
    public decimal Qty { get; set; }
    public decimal Rate { get; set; }
    public decimal Mrp { get; set; }
    public string? Machine { get; set; }
    public DateTimeOffset At { get; set; }
}

public class SmsEvent : TenantEntity
{
    public string Name { get; set; } = "";
    public bool IsActive { get; set; } = true;
}

public class SmsTemplate : TenantEntity
{
    public long? EventId { get; set; }
    public SmsEvent? Event { get; set; }
    public string Name { get; set; } = "";
    public MessageChannel Channel { get; set; } = MessageChannel.Sms;
    public string? Subject { get; set; }
    /// <summary>Body with tags like {CustomerName}, {BillNo}, {BillAmount}, {Balance}.</summary>
    public string Body { get; set; } = "";
    public bool IsActive { get; set; } = true;
}

public class SmsSchedule : TenantEntity
{
    public long LedgerId { get; set; }
    public long TemplateId { get; set; }
    public DateTimeOffset ScheduledAt { get; set; }
    public bool Processed { get; set; }
}

/// <summary>Outbox of SMS / e-mail messages, drained by a background sender.</summary>
public class OutboundMessage : TenantEntity
{
    public MessageChannel Channel { get; set; }
    public string Recipient { get; set; } = "";
    public string? Subject { get; set; }
    public string Body { get; set; } = "";
    public string? AttachmentName { get; set; }
    public byte[]? Attachment { get; set; }
    public MessageStatus Status { get; set; } = MessageStatus.Queued;
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public string? Source { get; set; }
}
