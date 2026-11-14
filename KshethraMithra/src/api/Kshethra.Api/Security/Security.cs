using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Kshethra.Application.Admin;
using Kshethra.Application.Auth;
using Kshethra.Application.Common;
using Kshethra.Domain;

namespace Kshethra.Api.Security;

public sealed class JwtOptions
{
    public string Issuer { get; set; } = "kshethra";
    public string Audience { get; set; } = "kshethra-web";
    /// <summary>Signing key (≥ 32 bytes). Must come from user-secrets / environment, never from source.</summary>
    public string SigningKey { get; set; } = "";
    public int AccessTokenMinutes { get; set; } = 15;
    public int RefreshTokenDays { get; set; } = 7;
}

public static class Claims
{
    public const string Company = "cid";
    public const string FinancialYear = "fy";
    public const string Role = "rid";
    public const string Admin = "adm";
    public const string Purpose = "pur";
}

public sealed class TokenService(IOptions<JwtOptions> options, IClock clock) : ITokenService
{
    private readonly JwtOptions _o = options.Value;
    private SigningCredentials Credentials => new(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_o.SigningKey)), SecurityAlgorithms.HmacSha256);
    public TimeSpan RefreshLifetime => TimeSpan.FromDays(_o.RefreshTokenDays);

    public IssuedToken CreateAccessToken(TokenRequest r)
    {
        var expires = clock.Now.AddMinutes(_o.AccessTokenMinutes);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, r.User.Id.ToString()),
            new(JwtRegisteredClaimNames.UniqueName, r.User.UserName),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new("name", r.User.DisplayName),
            new(Claims.Company, r.CompanyId.ToString()),
            new(Claims.FinancialYear, r.FinancialYearId.ToString()),
            new(Claims.Role, r.RoleId.ToString()),
            new(Claims.Admin, r.IsAdmin ? "1" : "0"),
            new(Claims.Purpose, "access"),
        };
        var token = new JwtSecurityToken(_o.Issuer, _o.Audience, claims, clock.Now.UtcDateTime, expires.UtcDateTime, Credentials);
        return new IssuedToken(new JwtSecurityTokenHandler().WriteToken(token), expires);
    }

    public IssuedToken CreateStepUpToken(long userId)
    {
        var expires = clock.Now.AddMinutes(5);
        var token = new JwtSecurityToken(_o.Issuer, _o.Audience + ":stepup",
            [new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()), new Claim(Claims.Purpose, "stepup")],
            clock.Now.UtcDateTime, expires.UtcDateTime, Credentials);
        return new IssuedToken(new JwtSecurityTokenHandler().WriteToken(token), expires);
    }

    public bool ValidateStepUpToken(string token, long userId)
    {
        try
        {
            var principal = new JwtSecurityTokenHandler { MapInboundClaims = false }.ValidateToken(token, new TokenValidationParameters
            {
                ValidIssuer = _o.Issuer, ValidAudience = _o.Audience + ":stepup", IssuerSigningKey = Credentials.Key,
                ValidateLifetime = true, ClockSkew = TimeSpan.FromSeconds(30),
            }, out _);
            return principal.FindFirst(Claims.Purpose)?.Value == "stepup" && principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value == userId.ToString();
        }
        catch (SecurityTokenException) { return false; }
        catch (ArgumentException) { return false; }
    }
}

/// <summary>The signed-in user from the JWT, unless a <see cref="TenantOverride"/> switches the company for this scope.</summary>
public sealed class CurrentUser(IHttpContextAccessor http, TenantOverride over, IPermissionCache permissions) : ICurrentUser
{
    private ClaimsPrincipal? P => http.HttpContext?.User;
    private long Claim(string type) => long.TryParse(P?.FindFirst(type)?.Value, out var v) ? v : 0;

    public bool IsAuthenticated => over.CompanyId is not null || P?.Identity?.IsAuthenticated == true;
    public long UserId => over.UserId ?? Claim(JwtRegisteredClaimNames.Sub);
    public string UserName => over.UserName ?? P?.FindFirst(JwtRegisteredClaimNames.UniqueName)?.Value ?? "system";
    public long CompanyId => over.CompanyId ?? Claim(Claims.Company);
    public long FinancialYearId => over.FinancialYearId ?? Claim(Claims.FinancialYear);
    public bool IsAdmin => P?.FindFirst(Claims.Admin)?.Value == "1";
    public string? Machine
    {
        get
        {
            var h = http.HttpContext?.Request.Headers["X-Counter"].ToString();
            return string.IsNullOrWhiteSpace(h) ? IpAddress : new string(h.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or ' ').Take(60).ToArray());
        }
    }
    public string? IpAddress => http.HttpContext?.Connection.RemoteIpAddress?.ToString();

    public bool HasRight(string screen, Right right)
    {
        if (!IsAuthenticated) return false;
        if (IsAdmin) return true;
        long role = Claim(Claims.Role);
        return role > 0 && permissions.Get(role).TryGetValue(screen, out var r) && (r & right) == right;
    }
}

/// <summary>Declares the screen right an endpoint needs; enforced on the server for every call.</summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = true)]
public sealed class PermissionAttribute(string screen, Right right) : Attribute, IAuthorizationFilter
{
    public void OnAuthorization(AuthorizationFilterContext context)
    {
        if (context.HttpContext.User.Identity?.IsAuthenticated != true) { context.Result = new UnauthorizedResult(); return; }
        var user = context.HttpContext.RequestServices.GetRequiredService<ICurrentUser>();
        if (!user.HasRight(screen, right))
            context.Result = new ObjectResult(new ProblemDetails { Status = 403, Title = "Access denied", Detail = $"You need '{right}' permission on {screen}." }) { StatusCode = 403 };
    }
}

/// <summary>Requires a fresh re-authentication token (X-Step-Up-Token) for destructive actions.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RequireStepUpAttribute : Attribute, IAuthorizationFilter
{
    public const string Header = "X-Step-Up-Token";

    public void OnAuthorization(AuthorizationFilterContext context)
    {
        var tokens = context.HttpContext.RequestServices.GetRequiredService<ITokenService>();
        var user = context.HttpContext.RequestServices.GetRequiredService<ICurrentUser>();
        var token = context.HttpContext.Request.Headers[Header].ToString();
        if (string.IsNullOrEmpty(token) || !tokens.ValidateStepUpToken(token, user.UserId))
            context.Result = new ObjectResult(new ProblemDetails
            {
                Status = 403, Title = "Re-authentication required", Detail = "Confirm your password to continue.", Extensions = { ["code"] = "step_up_required" },
            }) { StatusCode = 403 };
    }
}

public static class PermissionExtensions
{
    public static void Require(this ICurrentUser user, string screen, Right right)
    {
        if (!user.HasRight(screen, right)) throw new ForbiddenException($"You need '{right}' permission on {screen}.");
    }
}
