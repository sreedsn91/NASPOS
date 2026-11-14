using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using NasLicenseManager.Data;

namespace NasLicenseManager.Pages;

public static class Auth
{
    public const int MinPasswordLength = 8;

    public static string? PasswordProblem(string? password) =>
        string.IsNullOrEmpty(password) || password.Length < MinPasswordLength || !password.Any(char.IsLetter) || !password.Any(char.IsDigit)
            ? $"Password must be at least {MinPasswordLength} characters with letters and digits." : null;

    public static Task SignInAsync(HttpContext http, StaffUser u)
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, u.UserName),
            new Claim("display", string.IsNullOrWhiteSpace(u.DisplayName) ? u.UserName : u.DisplayName),
            new Claim("uid", u.Id.ToString()),
            new Claim("admin", u.IsAdmin ? "1" : "0"),
        ], CookieAuthenticationDefaults.AuthenticationScheme);
        return http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
    }

    public static int UserId(ClaimsPrincipal p) => int.TryParse(p.FindFirst("uid")?.Value, out var id) ? id : 0;
}
