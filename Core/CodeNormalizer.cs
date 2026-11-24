using System.Text.RegularExpressions;

namespace EmbyHistory.Core;

/// <summary>Conservative extraction of media codes from titles and file names.</summary>
public static class CodeNormalizer
{
    // Amateur / uncensored studios encode the release date as the number: 080809-134, 122920_001.
    private static readonly Regex DatedRelease = new(
        @"(?<![0-9])(?<date>\d{6})[-_](?<number>\d{2,4})(?![0-9])",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
    private static readonly Regex Fc2 = new(
        @"(?<![A-Z0-9])FC2[-_ ]?(?:PPV[-_ ]?)?(\d{2,10})(?![A-Z0-9])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
    private static readonly Regex Heyzo = new(
        @"(?<![A-Z0-9])HEYZO(?:[-_ ]?(?:HD|LT|FULL))*[-_ ]?(\d{3,4})(?![A-Z0-9])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
    // Tokyo Hot uses n1234 / k1234 as the whole code.
    private static readonly Regex TokyoHot = new(
        @"(?<![A-Z0-9])(?<series>[NK])(?<number>\d{3,4})(?![A-Z0-9])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
    // Blue-ray style releases put a letter in the serial: MKBD-S97, SAV-HK03.
    private static readonly Regex SeriesWithLetter = new(
        @"(?<![A-Z0-9])(?<prefix>[A-Z][A-Z0-9]{1,6})[-_](?<suffix>[A-Z]{1,2}\d{1,4})(?![A-Z0-9])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
    private static readonly Regex Series = new(
        @"(?<![A-Z0-9])(?<prefix>(?=[A-Z0-9]{1,12}-)[A-Z0-9]*[A-Z][A-Z0-9]*)[-_](?<number>\d{2,8})(?![A-Z0-9])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
    private static readonly HashSet<string> Noise = new(StringComparer.OrdinalIgnoreCase)
    {
        "X264", "X265", "H264", "H265", "H266", "AVC", "HEVC", "AV1", "VP9", "MPEG4",
        "HD", "FHD", "UHD", "HDR", "HDR10", "SDR", "BLURAY", "BDRIP", "WEB", "WEBDL", "REMUX",
        "AAC", "AC3", "DTS", "DDP", "MP3", "CD", "DISC", "DVD", "PART", "CHAPTER", "VOL", "NO"
    };
    // Series prefixes that are product/format labels rather than a studio code.
    private static readonly HashSet<string> NoiseSuffix = new(StringComparer.OrdinalIgnoreCase)
    {
        "UNCENSORED", "TOKYO", "CARIBBEAN", "CARIBBEANCOM", "1PONDO", "10MUSUME", "HEYZO"
    };

    public static string Extract(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var fc2 = Fc2.Match(text);
        if (fc2.Success) return "FC2-PPV-" + fc2.Groups[1].Value;
        var heyzo = Heyzo.Match(text);
        if (heyzo.Success) return "HEYZO-" + heyzo.Groups[1].Value;
        var dated = DatedRelease.Match(text);
        if (dated.Success)
        {
            var number = dated.Groups["number"].Value;
            var rest = text[(dated.Index + dated.Length)..].TrimStart();
            // "202401-1080p" is a date plus a resolution label, not a release code.
            var resolution = number is "360" or "480" or "720" or "1080" or "1440" or "2160";
            if (!(resolution && rest.StartsWith("p", StringComparison.OrdinalIgnoreCase)))
                return dated.Groups["date"].Value + "-" + number;
        }
        var tokyo = TokyoHot.Match(text);
        if (tokyo.Success) return tokyo.Groups["series"].Value.ToUpperInvariant() + tokyo.Groups["number"].Value;
        foreach (Match match in SeriesWithLetter.Matches(text))
        {
            var prefix = match.Groups["prefix"].Value.ToUpperInvariant();
            if (!Noise.Contains(prefix) && !NoiseSuffix.Contains(prefix))
                return prefix + "-" + match.Groups["suffix"].Value.ToUpperInvariant();
        }
        foreach (Match match in Series.Matches(text))
        {
            var prefix = match.Groups["prefix"].Value.ToUpperInvariant();
            if (!Noise.Contains(prefix) && !NoiseSuffix.Contains(prefix))
                return prefix + "-" + match.Groups["number"].Value;
        }
        return "";
    }
}
