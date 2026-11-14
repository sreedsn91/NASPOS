using System.Globalization;
using System.Text;

namespace NasPos.Application.Printing;

/// <summary>Code 128 (set B) encoder rendering to SVG. Used for labels and invoice barcodes.</summary>
public static class Code128
{
    private static readonly string[] Patterns =
    [
        "212222", "222122", "222221", "121223", "121322", "131222", "122213", "122312", "132212", "221213", "221312", "231212", "112232",
        "122132", "122231", "113222", "123122", "123221", "223211", "221132", "221231", "213212", "223112", "312131", "311222", "321122",
        "321221", "312212", "322112", "322211", "212123", "212321", "232121", "111323", "131123", "131321", "112313", "132113", "132311",
        "211313", "231113", "231311", "112133", "112331", "132131", "113123", "113321", "133121", "313121", "211331", "231131", "213113",
        "213311", "213131", "311123", "311321", "331121", "312113", "312311", "332111", "314111", "221411", "431111", "111224", "111422",
        "121124", "121421", "141122", "141221", "112214", "112412", "122114", "122411", "142112", "142211", "241211", "221114", "413111",
        "241112", "134111", "111242", "121142", "121241", "114212", "124112", "124211", "411212", "421112", "421211", "212141", "214121",
        "412121", "111143", "111341", "131141", "114113", "114311", "411113", "411311", "113141", "114131", "311141", "411131", "211412",
        "211214", "211232", "2331112",
    ];

    /// <summary>Bar/space module widths for the text (start B, data, checksum, stop).</summary>
    public static List<int> Modules(string text)
    {
        var codes = new List<int> { 104 };
        foreach (char ch in text)
        {
            if (ch < 32 || ch > 126) throw new ArgumentException("Code 128-B supports printable ASCII only.");
            codes.Add(ch - 32);
        }
        int checksum = codes[0];
        for (int i = 1; i < codes.Count; i++) checksum += codes[i] * i;
        codes.Add(checksum % 103);
        codes.Add(106);
        return codes.SelectMany(c => Patterns[c].Select(p => p - '0')).ToList();
    }

    public static string Svg(string text, double height = 40, double moduleWidth = 1, bool showText = false, double fontSize = 8)
    {
        var modules = Modules(text);
        double quiet = 10 * moduleWidth;
        double width = modules.Sum() * moduleWidth + quiet * 2;
        double totalHeight = showText ? height + fontSize + 2 : height;
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {width:0.##} {totalHeight:0.##}\" width=\"{width:0.##}\" height=\"{totalHeight:0.##}\">");
        sb.Append(CultureInfo.InvariantCulture, $"<rect width=\"{width:0.##}\" height=\"{totalHeight:0.##}\" fill=\"#fff\"/>");
        double x = quiet;
        for (int i = 0; i < modules.Count; i++)
        {
            double w = modules[i] * moduleWidth;
            if (i % 2 == 0) sb.Append(CultureInfo.InvariantCulture, $"<rect x=\"{x:0.##}\" y=\"0\" width=\"{w:0.##}\" height=\"{height:0.##}\" fill=\"#000\"/>");
            x += w;
        }
        if (showText)
            sb.Append(CultureInfo.InvariantCulture, $"<text x=\"{width / 2:0.##}\" y=\"{totalHeight - 1:0.##}\" font-family=\"monospace\" font-size=\"{fontSize:0.##}\" text-anchor=\"middle\">{System.Security.SecurityElement.Escape(text)}</text>");
        sb.Append("</svg>");
        return sb.ToString();
    }
}

public static class AmountInWords
{
    private static readonly string[] Ones =
        ["", "One", "Two", "Three", "Four", "Five", "Six", "Seven", "Eight", "Nine", "Ten", "Eleven", "Twelve", "Thirteen", "Fourteen",
         "Fifteen", "Sixteen", "Seventeen", "Eighteen", "Nineteen"];
    private static readonly string[] Tens = ["", "", "Twenty", "Thirty", "Forty", "Fifty", "Sixty", "Seventy", "Eighty", "Ninety"];

    /// <summary>Indian numbering: "Rupees One Lakh Twenty Thousand and Fifty Paise Only".</summary>
    public static string Rupees(decimal amount)
    {
        amount = Math.Round(Math.Abs(amount), 2, MidpointRounding.AwayFromZero);
        long rupees = (long)Math.Truncate(amount);
        int paise = (int)((amount - rupees) * 100);
        string words = rupees == 0 ? "Zero" : Convert(rupees);
        string result = $"Rupees {words}";
        if (paise > 0) result += $" and {Two(paise)} Paise";
        return result + " Only";
    }

    private static string Convert(long n)
    {
        var parts = new List<string>();
        long crore = n / 10000000; n %= 10000000;
        long lakh = n / 100000; n %= 100000;
        long thousand = n / 1000; n %= 1000;
        long hundred = n / 100; n %= 100;
        if (crore > 0) parts.Add(Convert(crore) + " Crore");
        if (lakh > 0) parts.Add(Two((int)lakh) + " Lakh");
        if (thousand > 0) parts.Add(Two((int)thousand) + " Thousand");
        if (hundred > 0) parts.Add(Ones[hundred] + " Hundred");
        if (n > 0) parts.Add(Two((int)n));
        return string.Join(" ", parts);
    }

    private static string Two(int n) => n < 20 ? Ones[n] : Tens[n / 10] + (n % 10 > 0 ? " " + Ones[n % 10] : "");
}
