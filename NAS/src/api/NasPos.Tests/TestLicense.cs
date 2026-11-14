using System.Security.Cryptography;
using NasPos.Domain.Licensing;

namespace NasPos.Tests;

/// <summary>
/// Signs license keys with the test key that Debug builds of NAS POS Pro trust (LicenseTrust.TestPublicKey).
/// Release builds — the installer — do not accept these keys.
/// </summary>
public static class TestLicense
{
    private const string TestPrivateKeyPkcs8 =
        "MIGHAgEAMBMGByqGSM49AgEGCCqGSM49AwEHBG0wawIBAQQgre4BvrqnY/tT33ZoOuVKy4XU+Nh7wapW1RWlYI6V1t6hRANCAAQwcwJkjm76WPd76qeFW0hBZW26Fn78PV3BYtytSfcXUA5d+ytP4yQ6xxVAQda4QHYUKDYZVNhB3PEXkPApqLdd";

    public static ECDsa SigningKey()
    {
        var k = ECDsa.Create();
        k.ImportPkcs8PrivateKey(Convert.FromBase64String(TestPrivateKeyPkcs8), out _);
        return k;
    }

    /// <param name="months">Subscription length in months; null = lifetime.</param>
    public static string Key(string company, int? months = null, DateOnly? start = null, string? gstin = null, int? maxUsers = null)
    {
        using var k = SigningKey();
        var s = start ?? DateOnly.FromDateTime(DateTime.Today);
        return LicenseKey.Create(k, company, gstin, months, s, s, maxUsers);
    }
}
