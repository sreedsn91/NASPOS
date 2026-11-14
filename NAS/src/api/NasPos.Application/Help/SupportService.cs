using System.Text;
using Microsoft.EntityFrameworkCore;
using NasPos.Application.Common;
using NasPos.Application.Messaging;
using NasPos.Domain;
using NasPos.Domain.Entities;

namespace NasPos.Application.Help;

public sealed record SupportInfo(string? Email, string? Phone);
public sealed record SupportRequest(string? Topic, string? Subject, string? Message, string? ReplyTo);
public sealed record SupportMessageDto(long Id, string? Subject, MessageStatus Status, DateTimeOffset CreatedAt, DateTimeOffset? SentAt, string? LastError);

/// <summary>
/// FAQ › Email Us: queues a message to the support address (Settings › Features › Help &amp; support) through the
/// normal e-mail outbox, so it is sent by the background sender once SMTP/internet is available.
/// </summary>
public class SupportService(IAppDbContext db, ICurrentUser user, IClock clock, SettingsService settings, DocumentMessaging queue, AuditService audit)
{
    public static readonly string[] Topics = ["Question", "Problem / error", "Suggestion", "Other"];
    private const int MaxPerHour = 10;

    private string Source => $"Support:{user.UserId}";

    public async Task<SupportInfo> InfoAsync(CancellationToken ct)
    {
        var s = await settings.GetAsync(ct);
        return new SupportInfo(NullIfEmpty(s.SupportEmail), NullIfEmpty(s.SupportPhone));
    }

    public async Task<long> SendAsync(SupportRequest r, CancellationToken ct)
    {
        var s = await settings.GetAsync(ct);
        if (string.IsNullOrWhiteSpace(s.SupportEmail))
            throw new DomainException("The support e-mail address is not set yet. Ask your administrator to set it in Settings › Features › Help & support.", "support_not_configured");
        var topic = Topics.Contains(r.Topic) ? r.Topic! : "Question";
        var subject = (r.Subject ?? "").Trim();
        var message = (r.Message ?? "").Trim();
        var replyTo = (r.ReplyTo ?? "").Trim();
        if (subject.Length == 0) throw new DomainException("Enter a subject.");
        if (subject.Length > 150) throw new DomainException("Subject must be at most 150 characters.");
        if (message.Length < 5) throw new DomainException("Write your message.");
        if (message.Length > 4000) throw new DomainException("Message must be at most 4000 characters.");
        if (replyTo.Length > 0 && (replyTo.Length > 200 || !System.Net.Mail.MailAddress.TryCreate(replyTo, out _)))
            throw new DomainException("Reply-to e-mail is not a valid address.");

        var since = clock.Now.AddHours(-1);
        if (await db.OutboundMessages.CountAsync(m => m.Source == Source && m.CreatedAt >= since, ct) >= MaxPerHour)
            throw new DomainException("You have sent many messages in the last hour. Please wait a while before sending another.", "rate_limited");

        var company = await db.Companies.AsNoTracking().Where(c => c.Id == user.CompanyId).Select(c => new { c.Name, c.Phone, c.Gstin }).FirstAsync(ct);
        var body = new StringBuilder()
            .AppendLine(message)
            .AppendLine()
            .AppendLine("-----")
            .AppendLine($"Topic: {topic}")
            .AppendLine($"From: {user.UserName} — {company.Name}")
            .AppendLine($"Reply to: {(replyTo.Length > 0 ? replyTo : "(not given)")}")
            .AppendLine($"Company phone: {company.Phone ?? "-"} · GSTIN: {company.Gstin ?? "-"}")
            .AppendLine($"Counter: {user.Machine ?? "-"} · Sent: {clock.Now:dd-MM-yyyy HH:mm}")
            .AppendLine("Sent from NAS POS Pro › FAQ › Email Us")
            .ToString();

        await queue.QueueAsync(MessageChannel.Email, s.SupportEmail, $"[NAS POS] {topic}: {subject}", body, Source, ct: ct);
        audit.Log(Screens.Messaging, "Email", $"Support message: {subject}");
        await db.SaveChangesAsync(ct);
        return await db.OutboundMessages.Where(m => m.Source == Source).OrderByDescending(m => m.Id).Select(m => m.Id).FirstAsync(ct);
    }

    /// <summary>The signed-in user's own support messages and whether they were sent.</summary>
    public Task<List<SupportMessageDto>> MineAsync(CancellationToken ct) =>
        db.OutboundMessages.AsNoTracking().Where(m => m.Source == Source).OrderByDescending(m => m.CreatedAt).Take(20)
            .Select(m => new SupportMessageDto(m.Id, m.Subject, m.Status, m.CreatedAt, m.SentAt, m.LastError)).ToListAsync(ct);

    /// <summary>Puts one of the user's own failed messages back in the queue (e.g. after the internet is back).</summary>
    public async Task RetryAsync(long id, CancellationToken ct)
    {
        var m = await db.OutboundMessages.FirstOrDefaultAsync(x => x.Id == id && x.Source == Source, ct) ?? throw new NotFoundException("Message not found.");
        if (m.Status != MessageStatus.Failed) throw new DomainException("Only a message that failed can be sent again.");
        m.Status = MessageStatus.Queued;
        m.Attempts = 0;
        await db.SaveChangesAsync(ct);
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
