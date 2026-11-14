using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using NasPos.Application.Common;
using NasPos.Application.Settings;
using NasPos.Domain;
using NasPos.Domain.Entities;

namespace NasPos.Application.Messaging;

public static partial class TemplateRenderer
{
    [GeneratedRegex(@"\{(\w+)\}")]
    private static partial Regex Tag();

    /// <summary>Replaces {Tag} placeholders; unknown tags are left as-is.</summary>
    public static string Render(string template, IReadOnlyDictionary<string, string?> values) =>
        Tag().Replace(template, m => values.TryGetValue(m.Groups[1].Value, out var v) ? v ?? "" : m.Value);

    public static readonly string[] Tags =
        ["CustomerName", "CompanyName", "BillNo", "BillDate", "BillAmount", "Balance", "Points", "Mobile"];
}

/// <summary>Queues invoice SMS / e-mail into the outbox (sent by the background dispatcher).</summary>
public class DocumentMessaging(IAppDbContext db, ICurrentUser user, IClock clock)
{
    public async Task QueueInvoiceSmsAsync(long documentId, CompanySettings settings, CancellationToken ct)
    {
        if (!settings.EnableSms || !settings.EnableCustomerSms) return;
        var doc = await db.TradeDocuments.AsNoTracking().FirstAsync(d => d.Id == documentId, ct);
        if (doc.PartyLedgerId is null) return;
        var party = await db.Ledgers.AsNoTracking().FirstAsync(l => l.Id == doc.PartyLedgerId, ct);
        if (!party.SmsOptIn || string.IsNullOrWhiteSpace(party.Mobile)) return;
        var template = await db.SmsTemplates.AsNoTracking()
            .Where(t => t.IsActive && t.Channel == MessageChannel.Sms && t.Event != null && t.Event.Name == "Invoice")
            .FirstOrDefaultAsync(ct);
        if (template is null) return;
        var values = await ValuesForAsync(doc, party, ct);
        db.OutboundMessages.Add(new OutboundMessage
        {
            Channel = MessageChannel.Sms, Recipient = party.Mobile!, Body = TemplateRenderer.Render(template.Body, values),
            Source = $"{doc.VoucherType} {doc.DocumentNo}",
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task<Dictionary<string, string?>> ValuesForAsync(TradeDocument doc, Ledger? party, CancellationToken ct)
    {
        var company = await db.Companies.AsNoTracking().FirstAsync(c => c.Id == user.CompanyId, ct);
        decimal balance = party is null ? 0 : await db.AccountEntries.Where(e => e.LedgerId == party.Id && e.FinancialYearId == doc.FinancialYearId)
            .SumAsync(e => (decimal?)(e.Debit - e.Credit), ct) ?? 0;
        return new Dictionary<string, string?>
        {
            ["CustomerName"] = doc.PartyName ?? party?.Name,
            ["CompanyName"] = company.Name,
            ["BillNo"] = doc.DocumentNo,
            ["BillDate"] = clock.BusinessDate(doc.Date).ToString("dd-MM-yyyy", CultureInfo.InvariantCulture),
            ["BillAmount"] = doc.BillAmount.ToString("0.00", CultureInfo.InvariantCulture),
            ["Balance"] = balance.ToString("0.00", CultureInfo.InvariantCulture),
            ["Points"] = doc.PointsEarned.ToString("0.##", CultureInfo.InvariantCulture),
            ["Mobile"] = party?.Mobile,
        };
    }

    public async Task QueueAsync(MessageChannel channel, string recipient, string? subject, string body, string? source,
        string? attachmentName = null, byte[]? attachment = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(recipient)) throw new DomainException("Recipient is required.");
        db.OutboundMessages.Add(new OutboundMessage
        {
            Channel = channel, Recipient = recipient.Trim(), Subject = subject, Body = body, Source = source,
            AttachmentName = attachmentName, Attachment = attachment,
        });
        await db.SaveChangesAsync(ct);
    }
}

public sealed record SmsTemplateDto(long Id, long? EventId, string? EventName, string Name, MessageChannel Channel, string? Subject, string Body, bool IsActive);
public sealed record SmsScheduleDto(long Id, long LedgerId, string? LedgerName, long TemplateId, string? TemplateName, DateTimeOffset ScheduledAt, bool Processed);
public sealed record OutboundMessageDto(long Id, MessageChannel Channel, string Recipient, string? Subject, string Body, MessageStatus Status,
    int Attempts, string? LastError, DateTimeOffset CreatedAt, DateTimeOffset? SentAt, string? Source);
public sealed record BulkSmsRequest(long TemplateId, long[] LedgerIds, DateTimeOffset? ScheduleAt);
public sealed record SendMessageRequest(MessageChannel Channel, string Recipient, string? Subject, string Body);

public class MessagingService(IAppDbContext db, IClock clock, AuditService audit, DocumentMessaging queue)
{
    public async Task<List<SmsTemplateDto>> TemplatesAsync(CancellationToken ct) =>
        await db.SmsTemplates.AsNoTracking().OrderBy(t => t.Name)
            .Select(t => new SmsTemplateDto(t.Id, t.EventId, t.Event != null ? t.Event.Name : null, t.Name, t.Channel, t.Subject, t.Body, t.IsActive))
            .ToListAsync(ct);

    public async Task<long> SaveTemplateAsync(SmsTemplateDto dto, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dto.Name) || string.IsNullOrWhiteSpace(dto.Body)) throw new DomainException("Name and message are required.");
        var t = dto.Id > 0 ? await db.SmsTemplates.FirstOrDefaultAsync(x => x.Id == dto.Id, ct) ?? throw new NotFoundException("Template not found.") : new SmsTemplate();
        if (await db.SmsTemplates.AnyAsync(x => x.Name == dto.Name.Trim() && x.Id != dto.Id, ct)) throw new DomainException("A template with this name exists.");
        t.Name = dto.Name.Trim(); t.EventId = dto.EventId; t.Channel = dto.Channel; t.Subject = dto.Subject; t.Body = dto.Body; t.IsActive = dto.IsActive;
        if (dto.Id == 0) db.SmsTemplates.Add(t);
        audit.Log(Screens.Messaging, dto.Id == 0 ? "Add" : "Edit", $"Template {t.Name}");
        await db.SaveChangesAsync(ct);
        return t.Id;
    }

    public async Task DeleteTemplateAsync(long id, CancellationToken ct)
    {
        var t = await db.SmsTemplates.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Template not found.");
        if (await db.SmsSchedules.AnyAsync(s => s.TemplateId == id, ct)) throw new DomainException("This template is used by scheduled messages.", "in_use");
        db.SmsTemplates.Remove(t);
        audit.Log(Screens.Messaging, "Delete", $"Template {t.Name}");
        await db.SaveChangesAsync(ct);
    }

    public async Task<List<SmsScheduleDto>> SchedulesAsync(CancellationToken ct) =>
        await db.SmsSchedules.AsNoTracking().OrderByDescending(s => s.ScheduledAt)
            .Select(s => new SmsScheduleDto(s.Id, s.LedgerId, db.Ledgers.Where(l => l.Id == s.LedgerId).Select(l => l.Name).FirstOrDefault(),
                s.TemplateId, db.SmsTemplates.Where(t => t.Id == s.TemplateId).Select(t => t.Name).FirstOrDefault(), s.ScheduledAt, s.Processed))
            .Take(500).ToListAsync(ct);

    /// <summary>Sends a template to many customers now or schedules it.</summary>
    public async Task<int> BulkAsync(BulkSmsRequest r, CancellationToken ct)
    {
        var template = await db.SmsTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == r.TemplateId, ct) ?? throw new NotFoundException("Template not found.");
        var parties = await db.Ledgers.AsNoTracking().Where(l => r.LedgerIds.Contains(l.Id)).ToListAsync(ct);
        int n = 0;
        foreach (var p in parties)
        {
            if (r.ScheduleAt is not null && r.ScheduleAt > clock.Now)
            {
                db.SmsSchedules.Add(new SmsSchedule { LedgerId = p.Id, TemplateId = template.Id, ScheduledAt = r.ScheduleAt.Value });
                n++;
                continue;
            }
            string? to = template.Channel == MessageChannel.Sms ? p.Mobile : p.Email;
            if (string.IsNullOrWhiteSpace(to)) continue;
            db.OutboundMessages.Add(new OutboundMessage
            {
                Channel = template.Channel, Recipient = to, Subject = template.Subject, Source = $"Bulk {template.Name}",
                Body = TemplateRenderer.Render(template.Body, new Dictionary<string, string?> { ["CustomerName"] = p.Name, ["Mobile"] = p.Mobile }),
            });
            n++;
        }
        audit.Log(Screens.Messaging, "Sms", $"Bulk {template.Name} to {n} parties");
        await db.SaveChangesAsync(ct);
        return n;
    }

    public async Task<List<OutboundMessageDto>> OutboxAsync(MessageStatus? status, CancellationToken ct)
    {
        var q = db.OutboundMessages.AsNoTracking();
        if (status is not null) q = q.Where(m => m.Status == status);
        return await q.OrderByDescending(m => m.CreatedAt).Take(500)
            .Select(m => new OutboundMessageDto(m.Id, m.Channel, m.Recipient, m.Subject, m.Body, m.Status, m.Attempts, m.LastError, m.CreatedAt, m.SentAt, m.Source))
            .ToListAsync(ct);
    }

    public async Task RetryAsync(long id, CancellationToken ct)
    {
        var m = await db.OutboundMessages.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Message not found.");
        m.Status = MessageStatus.Queued;
        m.Attempts = 0;
        await db.SaveChangesAsync(ct);
    }

    public Task SendAsync(SendMessageRequest r, CancellationToken ct) =>
        queue.QueueAsync(r.Channel, r.Recipient, r.Subject, r.Body, "Manual", ct: ct);
}
