using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using NasLicenseManager.Data;
using NasLicenseManager.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<LicensingDb>(o => o.UseSqlServer(
    builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("ConnectionStrings:Default is not configured.")));
builder.Services.AddSingleton(new SigningKeyStore(builder.Configuration["Signing:Folder"]));
builder.Services.AddScoped<LicenseIssuer>();
builder.Services.AddSingleton<IPasswordHasher<StaffUser>, PasswordHasher<StaffUser>>();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.LoginPath = "/Login";
        o.AccessDeniedPath = "/Login";
        o.Cookie.Name = "nas_licmgr";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Strict;
        o.ExpireTimeSpan = TimeSpan.FromHours(10);
        o.SlidingExpiration = true;
    });
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("Admin", p => p.RequireClaim("admin", "1"));
builder.Services.AddRazorPages(o =>
{
    o.Conventions.AuthorizeFolder("/");
    o.Conventions.AllowAnonymousToPage("/Login");
    o.Conventions.AllowAnonymousToPage("/Setup");
    o.Conventions.AllowAnonymousToPage("/Error");
    o.Conventions.AuthorizePage("/Staff", "Admin");
    o.Conventions.AuthorizePage("/SigningKey", "Admin");
});

var app = builder.Build();

// The License Manager owns its database: create / upgrade it on start.
using (var scope = app.Services.CreateScope())
    scope.ServiceProvider.GetRequiredService<LicensingDb>().Database.Migrate();

if (!app.Environment.IsDevelopment()) app.UseExceptionHandler("/Error");
app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff";
    h["X-Frame-Options"] = "DENY";
    h["Referrer-Policy"] = "no-referrer";
    h["Content-Security-Policy"] = "default-src 'self'; style-src 'self'; script-src 'self'; img-src 'self' data:; object-src 'none'; base-uri 'none'; frame-ancestors 'none'";
    await next();
});
app.UseStaticFiles();

// First run: until a staff account exists, everything goes to Setup (before sign-in is asked for).
app.Use(async (ctx, next) =>
{
    if (!ctx.Request.Path.StartsWithSegments("/Setup") && !ctx.Request.Path.StartsWithSegments("/Error"))
    {
        var db = ctx.RequestServices.GetRequiredService<LicensingDb>();
        if (!await db.StaffUsers.AnyAsync()) { ctx.Response.Redirect("/Setup"); return; }
    }
    await next();
});

app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.MapRazorPages();
app.Run();

public partial class Program;
