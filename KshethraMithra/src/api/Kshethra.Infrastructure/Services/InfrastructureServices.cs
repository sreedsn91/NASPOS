using System.Net;
using System.Net.Mail;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Kshethra.Application.Admin;
using Kshethra.Application.Common;
using Kshethra.Application.Messaging;
using Kshethra.Application.Tools;
using Kshethra.Domain;
using Kshethra.Domain.Entities;

namespace Kshethra.Infrastructure.Services;

public sealed class BusinessOptions
{
    /// <summary>Business time zone offset, e.g. "05:30" for IST.</summary>
    public string UtcOffset { get; set; } = "05:30";
}

public sealed class SystemClock(IOptions<BusinessOptions> options) : IClock
{
    private readonly TimeSpan _offset = TimeSpan.TryParse(options.Value.UtcOffset.TrimStart('+'), out var o) ? o : TimeSpan.FromHours(5.5);
    public DateTimeOffset Now => DateTimeOffset.UtcNow.ToOffset(_offset);
    public TimeSpan Offset => _offset;
}

public sealed class PermissionCache(IServiceScopeFactory scopes, IMemoryCache cache) : IPermissionCache
{
    public IReadOnlyDictionary<string, Right> Get(long roleId) =>
        cache.GetOrCreate($"perm:{roleId}", e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            using var _ = db.IgnoreTenant();
            return (IReadOnlyDictionary<string, Right>)db.RolePermissions.AsNoTracking().Where(p => p.RoleId == roleId)
                .ToDictionary(p => p.Screen, p => p.Rights);
        })!;

    public void Invalidate(long roleId) => cache.Remove($"perm:{roleId}");
}

public sealed class SmsOptions
{
    /// <summary>HTTP GET template with {mobile}, {message} and {apiKey} placeholders. The API key comes from secrets.</summary>
    public string? UrlTemplate { get; set; }
    public string? ApiKey { get; set; }
    public string? SenderId { get; set; }
}

public sealed class EmailOptions
{
    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public bool EnableSsl { get; set; } = true;
    public string? UserName { get; set; }
    public string? Password { get; set; }
    public string? From { get; set; }
}

/// <summary>Provider abstraction for transactional SMS (HTTPS gateway) and e-mail (SMTP). All credentials from configuration.</summary>
public sealed class MessageSender(IHttpClientFactory http, IOptions<SmsOptions> sms, IOptions<EmailOptions> email) : IMessageSender
{
    public async Task SendSmsAsync(string mobile, string text, CancellationToken ct)
    {
        var o = sms.Value;
        if (string.IsNullOrWhiteSpace(o.UrlTemplate)) throw new InvalidOperationException("SMS gateway is not configured (Messaging:Sms:UrlTemplate).");
        if (!o.UrlTemplate.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("SMS gateway URL must use HTTPS.");
        var url = o.UrlTemplate
            .Replace("{mobile}", Uri.EscapeDataString(mobile))
            .Replace("{message}", Uri.EscapeDataString(text))
            .Replace("{apiKey}", Uri.EscapeDataString(o.ApiKey ?? ""))
            .Replace("{sender}", Uri.EscapeDataString(o.SenderId ?? ""));
        using var resp = await http.CreateClient("sms").GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"SMS gateway returned {(int)resp.StatusCode}.");
    }

    public async Task SendEmailAsync(string to, string subject, string body, string? attachmentName, byte[]? attachment, CancellationToken ct)
    {
        var o = email.Value;
        if (string.IsNullOrWhiteSpace(o.Host) || string.IsNullOrWhiteSpace(o.From)) throw new InvalidOperationException("E-mail is not configured (Messaging:Email).");
        using var client = new SmtpClient(o.Host, o.Port) { EnableSsl = o.EnableSsl };
        if (!string.IsNullOrEmpty(o.UserName)) client.Credentials = new NetworkCredential(o.UserName, o.Password);
        using var msg = new MailMessage(o.From, to, subject, body);
        if (attachment is not null) msg.Attachments.Add(new Attachment(new MemoryStream(attachment), attachmentName ?? "document.pdf"));
        await client.SendMailAsync(msg, ct);
    }
}

/// <summary>Drains the outbox and turns due scheduled SMS into outbox messages.</summary>
public sealed class OutboxDispatcher(IServiceScopeFactory scopes, ILogger<OutboxDispatcher> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Outbox dispatch failed"); }
            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        }
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var sender = scope.ServiceProvider.GetRequiredService<IMessageSender>();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();
        using var _ = db.IgnoreTenant();

        var due = await db.SmsSchedules.Where(s => !s.Processed && s.ScheduledAt <= clock.Now).Take(200).ToListAsync(ct);
        foreach (var s in due)
        {
            var t = await db.SmsTemplates.AsNoTracking().FirstOrDefaultAsync(x => x.Id == s.TemplateId, ct);
            var l = await db.Ledgers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == s.LedgerId, ct);
            s.Processed = true;
            string? to = t?.Channel == MessageChannel.Email ? l?.Email : l?.Mobile;
            if (t is null || l is null || string.IsNullOrWhiteSpace(to)) continue;
            db.OutboundMessages.Add(new OutboundMessage
            {
                CompanyId = s.CompanyId, Channel = t.Channel, Recipient = to, Subject = t.Subject, Source = $"Scheduled {t.Name}",
                Body = TemplateRenderer.Render(t.Body, new Dictionary<string, string?> { ["CustomerName"] = l.Name, ["Mobile"] = l.Mobile }),
            });
        }
        if (due.Count > 0) await db.SaveChangesAsync(ct);

        var batch = await db.OutboundMessages.Where(m => m.Status == MessageStatus.Queued && m.Attempts < 5).OrderBy(m => m.CreatedAt).Take(50).ToListAsync(ct);
        foreach (var m in batch)
        {
            m.Attempts++;
            try
            {
                if (m.Channel == MessageChannel.Sms) await sender.SendSmsAsync(m.Recipient, m.Body, ct);
                else await sender.SendEmailAsync(m.Recipient, m.Subject ?? "", m.Body, m.AttachmentName, m.Attachment, ct);
                m.Status = MessageStatus.Sent;
                m.SentAt = clock.Now;
                m.LastError = null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                m.LastError = ex.Message.Length > 1900 ? ex.Message[..1900] : ex.Message;
                if (m.Attempts >= 5) m.Status = MessageStatus.Failed;
                log.LogWarning("Message {Id} to {Recipient} failed: {Error}", m.Id, m.Recipient, ex.Message);
            }
        }
        if (batch.Count > 0) await db.SaveChangesAsync(ct);
    }
}

public sealed class TenantScopeFactory(IServiceScopeFactory scopes, ICurrentUser current) : ITenantScopeFactory
{
    public async Task<T> RunAsAsync<T>(long companyId, Func<IServiceProvider, Task<T>> work)
    {
        await using var scope = scopes.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var over = sp.GetRequiredService<TenantOverride>();
        over.CompanyId = companyId;
        over.UserId = current.UserId;
        over.UserName = current.UserName;
        var db = sp.GetRequiredService<IAppDbContext>();
        using (db.IgnoreTenant())
        {
            var today = DateOnly.FromDateTime(DateTime.Today);
            var years = await db.FinancialYears.AsNoTracking().Where(f => f.CompanyId == companyId).OrderByDescending(f => f.StartDate).ToListAsync();
            over.FinancialYearId = (years.FirstOrDefault(f => f.Contains(today) && !f.IsClosed) ?? years.FirstOrDefault(f => !f.IsClosed) ?? years.First()).Id;
        }
        return await work(sp);
    }
}
