using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace NasPos.Infrastructure.Persistence;

/// <summary>Used only by `dotnet ef migrations add`; no connection is opened and no secret is needed.</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(Environment.GetEnvironmentVariable("NASPOS_DESIGN_CONNECTION") ?? "Server=(design-time);Database=NasPos")
            .Options);
}
