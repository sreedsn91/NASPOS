using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NasChatbot.Knowledge;

namespace NasChatbot.Admin;

/// <summary>
/// Protects the "manage questions" screens with one admin password. No default password ships: the first admin
/// visit (from this computer only) sets it. The password is stored as a salted PBKDF2-SHA256 hash; sign-in returns
/// a random session token held in memory for 8 hours. Five wrong passwords lock sign-in for 15 minutes.
/// </summary>
public sealed class AdminAuth
{
    public const int MinPasswordLength = 8;
    private const int Iterations = 210_000;
    private const int MaxFailures = 5;
    private static readonly TimeSpan LockFor = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan SessionFor = TimeSpan.FromHours(8);

    private sealed class AdminFile { public string Salt { get; set; } = ""; public string Hash { get; set; } = ""; public int Iterations { get; set; } }

    private readonly string _path;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, DateTime> _sessions = new();
    private AdminFile? _admin;
    private int _failures;
    private DateTime _lockedUntil;

    public AdminAuth(string dataDir)
    {
        _path = Path.Combine(dataDir, "admin.json");
        if (File.Exists(_path)) _admin = JsonSerializer.Deserialize<AdminFile>(File.ReadAllText(_path), KnowledgeStore.Json);
    }

    public bool IsConfigured { get { lock (_gate) return _admin is not null; } }

    public static string? ValidateNewPassword(string? password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinPasswordLength) return $"Password must be at least {MinPasswordLength} characters.";
        if (!password.Any(char.IsLetter) || !password.Any(char.IsDigit)) return "Password must contain letters and digits.";
        if (password.Length > 128) return "Password is too long.";
        return null;
    }

    /// <summary>First-time setup. Returns a session token, or null when a password already exists.</summary>
    public string? Setup(string password)
    {
        lock (_gate)
        {
            if (_admin is not null) return null;
            Save(password);
        }
        return NewSession();
    }

    /// <summary>Returns a session token, or an error message.</summary>
    public (string? Token, string? Error) Login(string? password)
    {
        lock (_gate)
        {
            if (_admin is null) return (null, "Admin password has not been set yet.");
            if (DateTime.UtcNow < _lockedUntil)
                return (null, $"Too many wrong passwords. Try again after {Math.Ceiling((_lockedUntil - DateTime.UtcNow).TotalMinutes)} minute(s).");
            if (!Verify(password ?? ""))
            {
                if (++_failures >= MaxFailures) { _lockedUntil = DateTime.UtcNow + LockFor; _failures = 0; }
                return (null, "Wrong password.");
            }
            _failures = 0;
        }
        return (NewSession(), null);
    }

    public string? ChangePassword(string? current, string? next)
    {
        var problem = ValidateNewPassword(next);
        if (problem is not null) return problem;
        lock (_gate)
        {
            if (!Verify(current ?? "")) return "Current password is wrong.";
            Save(next!);
        }
        _sessions.Clear(); // every other signed-in admin must sign in again
        return null;
    }

    public bool IsValid(string? token)
    {
        if (string.IsNullOrEmpty(token) || !_sessions.TryGetValue(token, out var expires)) return false;
        if (expires > DateTime.UtcNow) return true;
        _sessions.TryRemove(token, out _);
        return false;
    }

    public void Logout(string? token) { if (token is not null) _sessions.TryRemove(token, out _); }

    private string NewSession()
    {
        foreach (var (key, expires) in _sessions) if (expires <= DateTime.UtcNow) _sessions.TryRemove(key, out _);
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        _sessions[token] = DateTime.UtcNow + SessionFor;
        return token;
    }

    private void Save(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, 32);
        var admin = new AdminFile { Salt = Convert.ToBase64String(salt), Hash = Convert.ToBase64String(hash), Iterations = Iterations };
        KnowledgeStore.Write(_path, admin);
        _admin = admin;
    }

    private bool Verify(string password)
    {
        if (_admin is null) return false;
        var expected = Convert.FromBase64String(_admin.Hash);
        var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), Convert.FromBase64String(_admin.Salt),
            _admin.Iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }
}
