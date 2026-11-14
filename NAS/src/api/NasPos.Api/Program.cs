using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using NasPos.Api.Infrastructure;
using NasPos.Api.Security;
using NasPos.Application.Auth;
using NasPos.Application.Common;
using NasPos.Infrastructure;
using NasPos.Infrastructure.Persistence;

QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

var jwt = config.GetSection("Jwt").Get<JwtOptions>() ?? new JwtOptions();
if (Encoding.UTF8.GetByteCount(jwt.SigningKey) < 32)
    throw new InvalidOperationException("Jwt:SigningKey must be at least 32 bytes. Set it with `dotnet user-secrets set Jwt:SigningKey <value>` or the Jwt__SigningKey environment variable.");
builder.Services.Configure<JwtOptions>(config.GetSection("Jwt"));

builder.Services.AddInfrastructure(config);
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, CurrentUser>();
builder.Services.AddSingleton<ITokenService, TokenService>();
builder.Services.AddScoped<IRealtimeNotifier, RealtimeNotifier>();

builder.Services.AddControllers(o => o.MaxModelBindingCollectionSize = 5000)
    .AddJsonOptions(o =>
    {
        o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
        o.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
    });
builder.Services.AddSignalR().AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks().AddDbContextCheck<AppDbContext>("database");

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    o.MapInboundClaims = false;
    o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidIssuer = jwt.Issuer, ValidAudience = jwt.Audience,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
        ValidateIssuer = true, ValidateAudience = true, ValidateLifetime = true, ValidateIssuerSigningKey = true,
        ClockSkew = TimeSpan.FromSeconds(30), NameClaimType = "unique_name",
    };
    // SignalR sends the token as a query string parameter on the hub endpoint only.
    o.Events = new JwtBearerEvents
    {
        OnMessageReceived = ctx =>
        {
            if (ctx.HttpContext.Request.Path.StartsWithSegments("/hubs") && ctx.Request.Query.TryGetValue("access_token", out var t)) ctx.Token = t;
            return Task.CompletedTask;
        },
        OnTokenValidated = ctx =>
        {
            if (ctx.Principal?.FindFirst(Claims.Purpose)?.Value != "access") ctx.Fail("Wrong token type.");
            return Task.CompletedTask;
        },
    };
});
builder.Services.AddAuthorization();

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("auth", ctx => RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    // Token refresh happens on every page load; it needs no password so it gets a looser limit.
    o.AddPolicy("refresh", ctx => RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

var origins = config.GetSection("Cors:Origins").Get<string[]>() ?? [];
builder.Services.AddCors(o => o.AddPolicy("web", p => p.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod().AllowCredentials()
    .WithExposedHeaders("Content-Disposition")));
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownNetworks.Clear();
    o.KnownProxies.Clear();
});
builder.Services.AddHsts(o => { o.MaxAge = TimeSpan.FromDays(180); o.IncludeSubDomains = true; });
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 25 * 1024 * 1024);

var app = builder.Build();

if (args.Contains("--migrate") || config.GetValue<bool>("Database:MigrateOnStartup"))
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
    if (args.Contains("--migrate")) return;
}

app.UseForwardedHeaders();
app.UseExceptionHandler();
if (!app.Environment.IsDevelopment() && config.GetValue("Security:HttpsRedirect", true))
{
    // Disable (Security__HttpsRedirect=false) only when TLS is terminated by a trusted reverse proxy.
    app.UseHsts();
    app.UseHttpsRedirection();
}
else
{
    app.MapOpenApi();
}
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers.XContentTypeOptions = "nosniff";
    ctx.Response.Headers.XFrameOptions = "DENY";
    ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
    await next();
});
// Single-site hosting (IIS installer): the Angular build is published into wwwroot and served by the API.
var spaIndex = Path.Combine(app.Environment.WebRootPath ?? Path.Combine(app.Environment.ContentRootPath, "wwwroot"), "index.html");
bool serveSpa = File.Exists(spaIndex);
if (serveSpa)
{
    const string csp = "default-src 'self'; img-src 'self' data: blob:; style-src 'self' 'unsafe-inline' https://fonts.googleapis.com; font-src 'self' https://fonts.gstatic.com; " +
                       "script-src 'self'; connect-src 'self' ws: wss:; frame-src 'self' blob:; object-src 'none'; base-uri 'self'";
    app.UseStaticFiles(new StaticFileOptions
    {
        OnPrepareResponse = f =>
        {
            // Hashed bundles are immutable; everything else (index.html, favicon) must revalidate.
            f.Context.Response.Headers.CacheControl = f.File.Name.Contains('-') && f.File.Name is not "index.html" ? "public, max-age=2592000, immutable" : "no-cache";
            f.Context.Response.Headers.ContentSecurityPolicy = csp;
        },
    });
    app.MapFallback(async ctx =>
    {
        if (ctx.Request.Path.StartsWithSegments("/api") || ctx.Request.Path.StartsWithSegments("/hubs")) { ctx.Response.StatusCode = 404; return; }
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.Headers.CacheControl = "no-cache";
        ctx.Response.Headers.ContentSecurityPolicy = csp;
        await ctx.Response.SendFileAsync(spaIndex);
    });
}
app.UseCors("web");
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHub<LiveHub>("/hubs/live");
app.MapHealthChecks("/health");
app.Run();

public partial class Program;
