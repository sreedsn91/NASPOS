using System.Text.RegularExpressions;

namespace Kshethra.Domain.Validation;

public static partial class Gstin
{
    private const string Chars = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";

    /// <summary>Regular taxpayer: state + PAN (5 letters, 4 digits, letter) + entity no. + 'Z' + check.</summary>
    [GeneratedRegex("^[0-9]{2}[A-Z]{5}[0-9]{4}[A-Z][1-9A-Z]Z[0-9A-Z]$")]
    private static partial Regex RegularPattern();

    /// <summary>Any GSTN-issued number (regular, TDS/TCS, UIN, non-resident, OIDAR): 15 alphanumerics.</summary>
    [GeneratedRegex("^[0-9]{2}[0-9A-Z]{13}$")]
    private static partial Regex GeneralPattern();

    public static string Normalize(string gstin) => new string(gstin.Where(c => !char.IsWhiteSpace(c) && c != '-').ToArray()).ToUpperInvariant();

    public static bool IsValid(string? gstin) => Error(gstin) is null;

    /// <summary>Why a GSTIN is invalid, in words a user can act on; null when valid.</summary>
    public static string? Error(string? gstin)
    {
        if (string.IsNullOrWhiteSpace(gstin)) return "GSTIN is empty.";
        var g = Normalize(gstin);
        if (g.Length != 15) return $"GSTIN must be 15 characters; you entered {g.Length}.";
        if (!g.All(c => Chars.Contains(c))) return "GSTIN may contain only letters and digits.";
        if (!GeneralPattern().IsMatch(g)) return "GSTIN must start with a 2-digit state code.";
        string state = g[..2];
        if (!IndianStates.Names.ContainsKey(state) && state != "99")
            return $"'{state}' is not a valid GST state code (first two digits).";
        // A 'Z' in position 14 marks a regular registration, whose middle part must be a PAN.
        if (g[13] == 'Z' && !RegularPattern().IsMatch(g))
            return "Characters 3–12 must be the PAN (5 letters, 4 digits, 1 letter) and character 13 the entity number (1–9 or A–Z).";
        if (g[14] != CheckChar(g[..14]))
            return "The GSTIN's check character (last character) doesn't match — one of the characters is probably mistyped.";
        return null;
    }

    /// <summary>Throws a user-facing DomainException when the GSTIN is invalid.</summary>
    public static void EnsureValid(string? gstin, string label = "GSTIN")
    {
        if (Error(gstin) is { } e) throw new DomainException(label == "GSTIN" ? e : $"{label}: {e}", "invalid_gstin");
    }

    public static char CheckChar(string first14)
    {
        int sum = 0;
        for (int i = 0; i < 14; i++)
        {
            int v = Chars.IndexOf(first14[i]);
            int p = v * (i % 2 == 0 ? 1 : 2);
            sum += p / 36 + p % 36;
        }
        return Chars[(36 - sum % 36) % 36];
    }

    public static string? StateCode(string? gstin) =>
        string.IsNullOrWhiteSpace(gstin) || Normalize(gstin).Length < 2 ? null : Normalize(gstin)[..2];
}

/// <summary>GS1 retail barcodes (EAN-8, UPC-A, EAN-13, GTIN-14): mod-10 check digit, weights 3,1 from the right.</summary>
public static class Ean
{
    public static bool IsGs1Length(string code) => code.Length is 8 or 12 or 13 or 14 && code.All(char.IsAsciiDigit);

    /// <summary>Check digit for the given digits (everything except the check digit itself).</summary>
    public static char CheckDigit(string body)
    {
        int sum = 0;
        for (int i = 0; i < body.Length; i++)
            sum += (body[body.Length - 1 - i] - '0') * (i % 2 == 0 ? 3 : 1);
        return (char)('0' + (10 - sum % 10) % 10);
    }

    /// <summary>True when the code is not a GS1-length number, or is one with a correct check digit.</summary>
    public static bool HasValidCheck(string code) => !IsGs1Length(code) || code[^1] == CheckDigit(code[..^1]);
}

/// <summary>
/// Weighing-scale label: prefix + item code (fixed length) + a flag digit (1 = value is a price, otherwise a weight)
/// + the value, divided by <see cref="Divisor"/> when it is a weight. Format string: "prefix|codeLength|valueLength|divisor".
/// </summary>
public sealed record ScaleBarcodeFormat(string Prefix, int CodeLength, int ValueLength, decimal Divisor)
{
    public static ScaleBarcodeFormat Parse(string? format)
    {
        var parts = (format ?? "").Split('|');
        if (parts.Length < 4) return new ScaleBarcodeFormat("", 0, 0, 1000);
        return new ScaleBarcodeFormat(parts[0],
            int.TryParse(parts[1], out var c) ? c : 0,
            int.TryParse(parts[2], out var v) ? v : 0,
            decimal.TryParse(parts[3], System.Globalization.CultureInfo.InvariantCulture, out var d) && d > 0 ? d : 1000);
    }

    public override string ToString() => $"{Prefix}|{CodeLength}|{ValueLength}|{Divisor.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
}

public sealed record ScaleBarcode(string ItemCode, bool IsPrice, decimal Value);

public static class ScaleBarcodeParser
{
    public static ScaleBarcode? TryParse(string? barcode, ScaleBarcodeFormat f)
    {
        if (string.IsNullOrWhiteSpace(barcode) || f.CodeLength <= 0 || string.IsNullOrEmpty(f.Prefix)) return null;
        barcode = barcode.Trim();
        if (!barcode.StartsWith(f.Prefix, StringComparison.Ordinal)) return null;
        int pos = f.Prefix.Length;
        if (barcode.Length < pos + f.CodeLength + 2) return null;
        string code = barcode.Substring(pos, f.CodeLength).Trim();
        char flag = barcode[pos + f.CodeLength];
        string raw = barcode[(pos + f.CodeLength + 1)..];
        if (f.ValueLength > 0 && raw.Length > f.ValueLength) raw = raw[..f.ValueLength];
        if (code.Length == 0 || !decimal.TryParse(raw, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var value)) return null;
        bool isPrice = flag == '1';
        return new ScaleBarcode(code, isPrice, isPrice ? value : value / f.Divisor);
    }
}
