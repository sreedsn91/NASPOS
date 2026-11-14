using System.Collections.Concurrent;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Kshethra.Api.Security;
using Kshethra.Application.Common;
using Kshethra.Application.Documents;
using Kshethra.Domain;

namespace Kshethra.Api.Infrastructure;

/// <summary>Maps exceptions to ProblemDetails. Nothing is swallowed: unexpected errors are logged with a trace id.</summary>
public sealed class ApiExceptionHandler(ILogger<ApiExceptionHandler> log, IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext http, Exception ex, CancellationToken ct)
    {
        var (status, title, code, extra) = ex switch
        {
            WarningException w => (409, "Please confirm", w.Code, (object?)w.Warnings),
            DomainException d => (400, d.Message, d.Code, null),
            NotFoundException => (404, ex.Message, "not_found", null),
            ForbiddenException => (403, ex.Message, "forbidden", null),
            ConflictException => (409, ex.Message, "conflict", null),
            DbUpdateConcurrencyException => (409, "This record was changed by another user. Reload and try again.", "concurrency", null),
            DbUpdateException { InnerException: SqlException { Number: 547 } } => (409, "This record is in use by other records and cannot be changed or deleted.", "in_use", null),
            DbUpdateException { InnerException: SqlException { Number: 2601 or 2627 } } => (409, "A record with the same unique value already exists.", "duplicate", null),
            BadHttpRequestException b => (b.StatusCode, "Invalid request.", "bad_request", null),
            OperationCanceledException => (499, "Request cancelled.", "cancelled", null),
            _ => (500, "An unexpected error occurred.", "server_error", null),
        };
        if (status >= 500) log.LogError(ex, "Unhandled error on {Path}", http.Request.Path);
        else log.LogInformation("Request to {Path} rejected: {Code} {Message}", http.Request.Path, code, ex.Message);
        http.Response.StatusCode = status;
        var pd = new ProblemDetails { Status = status, Title = title, Instance = http.Request.Path };
        pd.Extensions["code"] = code;
        pd.Extensions["traceId"] = http.TraceIdentifier;
        if (extra is not null) pd.Extensions["warnings"] = extra;
        return await problems.TryWriteAsync(new ProblemDetailsContext { HttpContext = http, ProblemDetails = pd, Exception = ex });
    }
}

/// <summary>Live counter dashboard, stock/master invalidation and soft edit locks.</summary>
[Authorize]
public sealed class LiveHub(ICurrentUser user) : Hub
{
    private static readonly ConcurrentDictionary<string, (string ConnectionId, string User, DateTimeOffset Since)> Locks = new();
    private static string Group(long companyId) => $"c:{companyId}";

    public override async Task OnConnectedAsync()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, Group(user.CompanyId));
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        foreach (var kv in Locks.Where(l => l.Value.ConnectionId == Context.ConnectionId).ToList())
            if (Locks.TryRemove(kv.Key, out _))
                await Clients.Group(Group(user.CompanyId)).SendAsync("unlocked", kv.Key.Split('|')[1]);
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>Soft lock: returns the other holder (if any) so the client can warn before editing.</summary>
    public async Task<string?> OpenDocument(string key)
    {
        string k = $"{user.CompanyId}|{key}";
        var mine = (Context.ConnectionId, user.UserName, DateTimeOffset.UtcNow);
        var current = Locks.GetOrAdd(k, mine);
        if (current.ConnectionId != Context.ConnectionId) return current.User;
        await Clients.OthersInGroup(Group(user.CompanyId)).SendAsync("locked", key, user.UserName);
        return null;
    }

    public async Task CloseDocument(string key)
    {
        string k = $"{user.CompanyId}|{key}";
        if (Locks.TryGetValue(k, out var v) && v.ConnectionId == Context.ConnectionId && Locks.TryRemove(k, out _))
            await Clients.OthersInGroup(Group(user.CompanyId)).SendAsync("unlocked", key);
    }
}

public sealed class RealtimeNotifier(IHubContext<LiveHub> hub) : IRealtimeNotifier
{
    public Task DocumentSavedAsync(long companyId, object payload) => hub.Clients.Group($"c:{companyId}").SendAsync("documentSaved", payload);
    public Task StockChangedAsync(long companyId, IEnumerable<long> productIds) => hub.Clients.Group($"c:{companyId}").SendAsync("stockChanged", productIds);
    public Task MasterChangedAsync(long companyId, string kind) => hub.Clients.Group($"c:{companyId}").SendAsync("masterChanged", kind);
}

public static class FileResults
{
    public static FileContentResult Pdf(this ControllerBase c, byte[] bytes, string name, bool inline = true)
    {
        c.Response.Headers.ContentDisposition = $"{(inline ? "inline" : "attachment")}; filename=\"{name}\"";
        return new FileContentResult(bytes, "application/pdf");
    }
}
