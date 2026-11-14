using System.Security.Cryptography;
using System.Text;

namespace Kshethra.Domain.Licensing;

/// <summary>What a license key says. Company name and GSTIN are carried as hashes and checked against the company.</summary>
/// <param name="Months">Subscription length in months; null = lifetime.</param>
public sealed record LicenseInfo(string KeyId, int? Months, DateOnly IssuedOn, DateOnly? ExpiresOn, bool BoundToGstin, int? MaxUsers)
{
    /// <summary>Lifetime keys never expire; others are valid up to and including <see cref="ExpiresOn"/>.</summary>
    public bool IsExpiredOn(DateOnly today) => ExpiresOn is { } e && today > e;
}

public sealed class LicenseKeyException(string message, string code = "license_invalid") : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// Offline license keys: a small binary payload signed with ECDSA P-256. Only the key generator holds the private key;
/// Kshethra Mithra verifies with the public key, so keys cannot be forged or edited (changing any character breaks the
/// signature). Text form: "NAS1-" + Crockford base32 in groups of five, safe to send by e-mail or WhatsApp.
///
/// Payload (26 bytes): version, length, issued (days since 2020-01-01), expires (days, 0xFFFF = lifetime),
/// key id (6 random bytes), company-name hash (8), GSTIN hash (4, zero = not bound to a GSTIN),
/// maximum active users (2, zero = unlimited).
/// Version 2 stores the length in months (0 = lifetime); version 1 keys (1 = one year, 2 = two years, 9 = lifetime)
/// are still accepted.
/// </summary>
public static class LicenseKey
{
    public const string Prefix = "NAS1-";
    private const byte Version = 2;
    private const int PayloadLength = 26, SignatureLength = 64;
    /// <summary>Largest user limit a key can carry.</summary>
    public const int MaxUserLimit = 9999;
    /// <summary>Longest fixed-term subscription (20 years); longer is sold as lifetime.</summary>
    public const int MaxMonths = 240;
    private const ushort LifetimeDays = 0xFFFF;
    private static readonly DateOnly Epoch = new(2020, 1, 1);
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"; // Crockford base32: no I, L, O, U

    /// <summary>Last valid day of a subscription of <paramref name="months"/> starting on <paramref name="start"/>
    /// (the day before the same date that many months later); null = lifetime.</summary>
    public static DateOnly? ExpiryFor(int? months, DateOnly start) => months switch
    {
        null => null,
        < 1 or > MaxMonths => throw new ArgumentOutOfRangeException(nameof(months), $"Length must be 1 to {MaxMonths} months."),
        _ => start.AddMonths(months.Value).AddDays(-1),
    };

    /// <summary>"1 month", "6 months", "1 year", "1 year 6 months", "2 years" or "Lifetime".</summary>
    public static string DurationText(int? months)
    {
        if (months is not { } m) return "Lifetime";
        int y = m / 12, r = m % 12;
        var parts = new List<string>();
        if (y > 0) parts.Add(y == 1 ? "1 year" : $"{y} years");
        if (r > 0) parts.Add(r == 1 ? "1 month" : $"{r} months");
        return string.Join(" ", parts);
    }

    /// <summary>Company names are compared ignoring case, spaces and punctuation ("Sree Stores, Kochi" = "SREE STORES KOCHI").</summary>
    public static string NormalizeName(string name) =>
        new((name ?? "").ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());

    public static string NormalizeGstin(string? gstin) =>
        new((gstin ?? "").ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());

    /// <summary>Product codes: a key works only in the product it was issued for.</summary>
    public static class Products
    {
        /// <summary>NAS POS Pro (shops). Its keys carry no product code, as keys issued before products existed.</summary>
        public const string NasPos = "";
        /// <summary>Kshethra Mithra (temples).</summary>
        public const string Kshethra = "KSHETHRA";
    }

    /// <summary>The fingerprint input: the product code (if any) and the normalized company name.</summary>
    private static string NameFingerprint(string companyName, string? product) =>
        string.IsNullOrEmpty(product) ? NormalizeName(companyName) : product + ":" + NormalizeName(companyName);

    /// <summary>Creates a signed key (key generator only — needs the private key).</summary>
    /// <param name="months">Subscription length in months (1–240); null = lifetime.</param>
    /// <param name="maxUsers">How many active users the company may have; null = unlimited.</param>
    /// <param name="product">Which product the key unlocks (<see cref="Products"/>).</param>
    public static string Create(ECDsa signingKey, string companyName, string? gstin, int? months, DateOnly start, DateOnly issuedOn, int? maxUsers, string? product = Products.Kshethra)
    {
        if (string.IsNullOrEmpty(NormalizeName(companyName))) throw new ArgumentException("Temple name is required.", nameof(companyName));
        if (maxUsers is < 1 or > MaxUserLimit) throw new ArgumentOutOfRangeException(nameof(maxUsers), $"Users must be between 1 and {MaxUserLimit}.");
        var expires = ExpiryFor(months, start);
        var payload = new byte[PayloadLength];
        payload[0] = Version;
        payload[1] = (byte)(months ?? 0);
        WriteDays(payload, 2, issuedOn);
        if (expires is null) { payload[4] = 0xFF; payload[5] = 0xFF; } else WriteDays(payload, 4, expires.Value);
        RandomNumberGenerator.Fill(payload.AsSpan(6, 6));
        Hash(NameFingerprint(companyName, product)).AsSpan(0, 8).CopyTo(payload.AsSpan(12, 8));
        var g = NormalizeGstin(gstin);
        if (g.Length > 0) Hash(g).AsSpan(0, 4).CopyTo(payload.AsSpan(20, 4));
        if (maxUsers is { } users) { payload[24] = (byte)(users >> 8); payload[25] = (byte)users; }
        var signature = signingKey.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return Prefix + Group(Base32(payload.Concat(signature).ToArray()));
    }

    /// <summary>
    /// Checks the signature and that the key was issued for this company (name, and GSTIN when the key carries one).
    /// Expiry is not checked here — an expired key is still a genuine key.
    /// </summary>
    public static LicenseInfo Verify(string key, string companyName, string? companyGstin, IEnumerable<ECDsa> trusted, string? product = Products.Kshethra)
    {
        var bytes = Decode(key);
        var payload = bytes.AsSpan(0, PayloadLength).ToArray();
        var signature = bytes.AsSpan(PayloadLength, SignatureLength).ToArray();
        if (!trusted.Any(k => k.VerifyData(payload, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)))
            throw new LicenseKeyException("This license key is not valid. Check that it was copied completely.");
        int? months = payload[0] switch
        {
            1 => payload[1] switch { 1 => 12, 2 => 24, 9 => null, _ => throw new LicenseKeyException("This license key is not valid.") },
            2 => payload[1] is 0 ? null : payload[1] <= MaxMonths ? payload[1] : throw new LicenseKeyException("This license key is not valid."),
            _ => throw new LicenseKeyException("This license key is for a different version of Kshethra Mithra."),
        };

        if (!Hash(NameFingerprint(companyName, product)).AsSpan(0, 8).SequenceEqual(payload.AsSpan(12, 8)))
            throw new LicenseKeyException($"This license key was issued for a different temple name (or a different product), not \"{companyName}\". The temple name must match the name given when the key was bought.", "license_company_mismatch");
        bool boundToGstin = payload.AsSpan(20, 4).IndexOfAnyExcept((byte)0) >= 0;
        if (boundToGstin && !Hash(NormalizeGstin(companyGstin)).AsSpan(0, 4).SequenceEqual(payload.AsSpan(20, 4)))
            throw new LicenseKeyException("This license key was issued for a different GSTIN. Enter the temple's GSTIN exactly as given when the key was bought.", "license_gstin_mismatch");

        ushort expiresDays = (ushort)(payload[4] << 8 | payload[5]);
        int users = payload[24] << 8 | payload[25];
        return new LicenseInfo(
            Convert.ToHexString(payload, 6, 6),
            months,
            ReadDays(payload, 2),
            expiresDays == LifetimeDays ? null : ReadDays(payload, 4),
            boundToGstin,
            users == 0 ? null : users);
    }
    // ---------------- encoding ----------------

    private static byte[] Decode(string key)
    {
        var text = (key ?? "").Trim().ToUpperInvariant();
        if (!text.StartsWith(Prefix, StringComparison.Ordinal)) throw new LicenseKeyException("A license key starts with NAS1-. Check that it was copied completely.");
        var sb = new StringBuilder();
        foreach (var raw in text[Prefix.Length..])
        {
            if (raw is '-' or ' ' or '\r' or '\n' or '\t') continue;
            var c = raw switch { 'O' => '0', 'I' or 'L' => '1', _ => raw };
            if (Alphabet.IndexOf(c) < 0) throw new LicenseKeyException($"The license key contains an invalid character '{raw}'.");
            sb.Append(c);
        }
        var bytes = FromBase32(sb.ToString());
        if (bytes.Length != PayloadLength + SignatureLength) throw new LicenseKeyException("This license key is incomplete or too long. Check that it was copied completely.");
        return bytes;
    }

    private static void WriteDays(byte[] buffer, int offset, DateOnly date)
    {
        var days = date.DayNumber - Epoch.DayNumber;
        if (days is < 0 or >= LifetimeDays) throw new ArgumentOutOfRangeException(nameof(date));
        buffer[offset] = (byte)(days >> 8);
        buffer[offset + 1] = (byte)days;
    }

    private static DateOnly ReadDays(byte[] buffer, int offset) => Epoch.AddDays(buffer[offset] << 8 | buffer[offset + 1]);

    private static byte[] Hash(string s) => SHA256.HashData(Encoding.UTF8.GetBytes(s));

    private static string Base32(byte[] data)
    {
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = buffer << 8 | b;
            bits += 8;
            while (bits >= 5) { sb.Append(Alphabet[(buffer >> (bits - 5)) & 31]); bits -= 5; }
        }
        if (bits > 0) sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    private static byte[] FromBase32(string s)
    {
        var bytes = new List<byte>(s.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var c in s)
        {
            buffer = buffer << 5 | Alphabet.IndexOf(c);
            bits += 5;
            if (bits >= 8) { bytes.Add((byte)(buffer >> (bits - 8))); bits -= 8; }
        }
        return [.. bytes];
    }

    private static string Group(string s) => string.Join('-', s.Chunk(5).Select(c => new string(c)));
}
