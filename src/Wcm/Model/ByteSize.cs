using System.Globalization;
using System.Text.RegularExpressions;

namespace Wcm.Model;

public static partial class ByteSize
{
    [GeneratedRegex(@"^\s*([\d.,]+)\s*([kmgt]?i?b?)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex SizeRx();

    /// <summary>Parses "50MB", "1.5 GB", "2048". Binary units unless decimal is requested (docker prints 1000-based).</summary>
    public static long Parse(string? text, bool decimalUnits = false)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var m = SizeRx().Match(text);
        if (!m.Success) throw new FormatException($"Bad size: '{text}'");

        var value = double.Parse(m.Groups[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
        var unit = m.Groups[2].Value.ToUpperInvariant();
        double k = decimalUnits ? 1000 : 1024;
        var mul = unit.Length == 0 ? 1 : unit[0] switch
        {
            'K' => k,
            'M' => k * k,
            'G' => k * k * k,
            'T' => k * k * k * k,
            _ => 1
        };
        return (long)(value * mul);
    }

    public static bool TryParse(string? text, out long bytes, bool decimalUnits = false)
    {
        try { bytes = Parse(text, decimalUnits); return true; }
        catch (FormatException) { bytes = 0; return false; }
    }

    public static string Format(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes;
        var i = 0;
        while (Math.Abs(v) >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{bytes} B" : v.ToString(v >= 100 ? "0" : v >= 10 ? "0.0" : "0.00", CultureInfo.InvariantCulture) + " " + units[i];
    }
}
