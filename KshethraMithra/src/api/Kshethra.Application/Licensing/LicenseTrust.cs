using System.Security.Cryptography;

namespace Kshethra.Application.Licensing;

/// <summary>
/// Public keys whose signatures Kshethra Mithra accepts. They are compiled in (not read from configuration) so a shop
/// cannot swap in its own key. The matching private key lives only in the NasKeyGen tool on the vendor's PC.
/// </summary>
public static class LicenseTrust
{
    /// <summary>Public half of the NasKeyGen signing key (NasKeyGen › Signing key › Show public key).</summary>
    public const string ProductionPublicKey =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEFuAy6qCu9pm48I+Cahmcz7LI/U8sWeJM3gzIu6g1LKyH39quyUNvqObLkfDNy4sUoR4GnX2PsGTfYoBxRdMRgg==";

#if DEBUG
    /// <summary>
    /// Test-only key so automated tests and developer runs can create licenses. Debug builds only: the installer
    /// publishes Release, which trusts the production key alone.
    /// </summary>
    public const string TestPublicKey =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEMHMCZI5u+lj3e+qnhVtIQWVtuhZ+/D1dwWLcrUn3F1AOXfsrT+MkOscVQEHWuEB2FCg2GVTYQdzxF5DwKai3XQ==";
#endif

    private static readonly Lazy<ECDsa[]> Keys = new(() =>
    {
        var list = new List<string> { ProductionPublicKey };
#if DEBUG
        list.Add(TestPublicKey);
#endif
        return list.Select(b64 =>
        {
            var k = ECDsa.Create();
            k.ImportSubjectPublicKeyInfo(Convert.FromBase64String(b64), out _);
            return k;
        }).ToArray();
    });

    public static IReadOnlyList<ECDsa> Trusted => Keys.Value;
}
