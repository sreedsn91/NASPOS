using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace NasPos.Application.Common;

/// <summary>
/// A transaction that joins the ambient one when a service is called from inside another service's transaction,
/// so composite operations (e.g. ledger save + opening voucher) still commit or roll back as one unit.
/// </summary>
public sealed class UnitOfWork(IDbContextTransaction? owned) : IAsyncDisposable
{
    public bool OwnsTransaction => owned is not null;
    public Task CommitAsync(CancellationToken ct = default) => owned?.CommitAsync(ct) ?? Task.CompletedTask;
    public ValueTask DisposeAsync() => owned?.DisposeAsync() ?? ValueTask.CompletedTask;
}

public static class UnitOfWorkExtensions
{
    public static async Task<UnitOfWork> BeginAsync(this IAppDbContext db, IsolationLevel level = IsolationLevel.ReadCommitted, CancellationToken ct = default) =>
        db.Database.CurrentTransaction is not null
            ? new UnitOfWork(null)
            : new UnitOfWork(await db.Database.BeginTransactionAsync(level, ct));

    public static Task<UnitOfWork> BeginAsync(this IAppDbContext db, CancellationToken ct) => db.BeginAsync(IsolationLevel.ReadCommitted, ct);
}
