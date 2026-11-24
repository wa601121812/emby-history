using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace EmbyHistory.Core;

/// <summary>
/// Episode history needs a three-level layout (series -> season -> episode) because that is the only
/// shape Emby will index as an Episode. These are pure naming/NFO rules so they can be tested
/// without Skia or a running server.
/// </summary>
public static class TvArchivePlan
{
    private static readonly char[] Invalid = Path.GetInvalidFileNameChars()
        .Concat(new[] { ':', '*', '?', '"', '<', '>', '|', '\\', '/' }).Distinct().ToArray();

    /// <summary>Filesystem-safe name that stays stable for the same title.</summary>
    public static string SafeName(string? value)
    {
        var text = (value ?? "").Normalize(NormalizationForm.FormKC);
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (char.IsControl(c)) { builder.Append(' '); continue; }
            builder.Append(Invalid.Contains(c) ? ' ' : c);
        }
        // Collapse runs of whitespace, then trim dots/spaces that Windows rejects at the end.
        var cleaned = string.Join(" ", builder.ToString().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries));
        cleaned = cleaned.Trim('.', ' ').Trim();
        if (cleaned.Length > 90) cleaned = cleaned[..90].TrimEnd('.', ' ');
        return cleaned.Length == 0 ? "Unknown" : cleaned;
    }

    public static string SeriesFolder(MediaSnapshot snapshot)
    {
        var name = SafeName(string.IsNullOrWhiteSpace(snapshot.SeriesName) ? snapshot.Name : snapshot.SeriesName);
        return snapshot.SeriesYear is int year && year > 1800 ? name + " (" + year.ToString(CultureInfo.InvariantCulture) + ")" : name;
    }

    public static string SeasonFolder(int season) => "Season " + season.ToString("00", CultureInfo.InvariantCulture);

    /// <summary>Episode stub/NFObasename, e.g. "Some Show - S01E05".</summary>
    public static string EpisodeBase(MediaSnapshot snapshot)
    {
        var name = SafeName(string.IsNullOrWhiteSpace(snapshot.SeriesName) ? snapshot.Name : snapshot.SeriesName);
        var season = snapshot.SeasonNumber ?? 0;
        var episode = snapshot.EpisodeNumber ?? 0;
        return name + " - S" + season.ToString("00", CultureInfo.InvariantCulture) +
               "E" + episode.ToString("00", CultureInfo.InvariantCulture);
    }

    public static string SeriesNfo(MediaSnapshot snapshot, string seriesKey, DateTime firstSeen, DateTime lastSeen, int seasonCount, int episodeCount)
    {
        var name = SafeName(string.IsNullOrWhiteSpace(snapshot.SeriesName) ? snapshot.Name : snapshot.SeriesName);
        var show = new XElement("tvshow",
            new XElement("title", "[历史] " + name),
            new XElement("sorttitle", "[历史] " + name),
            new XElement("plot", Plot(firstSeen, lastSeen, null, seasonCount, episodeCount)),
            new XElement("lockdata", "true"),
            new XElement("uniqueid", new XAttribute("type", "embyhistory"), new XAttribute("default", "true"), seriesKey),
            new XElement("tag", "EmbyHistory"), new XElement("tag", "已移出"));
        if (snapshot.SeriesYear is int year && year > 1800) show.Add(new XElement("year", year));
        AddProviderIds(show, snapshot.SeriesProviderIds);
        return new XDocument(new XDeclaration("1.0", "utf-8", null), show).ToString();
    }

    public static string SeasonNfo(int season, DateTime firstSeen, DateTime lastSeen, int episodeCount) =>
        SeasonNfo(season, firstSeen, lastSeen, episodeCount, null);

    public static string SeasonNfo(int season, DateTime firstSeen, DateTime lastSeen, int episodeCount, string? seriesKey)
    {
        var node = new XElement("season",
            new XElement("title", "[历史] 第 " + season.ToString(CultureInfo.InvariantCulture) + " 季"),
            new XElement("seasonnumber", season.ToString(CultureInfo.InvariantCulture)),
            new XElement("plot", Plot(firstSeen, lastSeen, season, 0, episodeCount)),
            new XElement("lockdata", "true"),
            new XElement("tag", "EmbyHistory"), new XElement("tag", "已移出"));
        if (!string.IsNullOrEmpty(seriesKey)) node.Add(new XElement("uniqueid", new XAttribute("type", "embyhistory"),
            new XAttribute("default", "true"), seriesKey + "-s" + season.ToString(CultureInfo.InvariantCulture)));
        return new XDocument(new XDeclaration("1.0", "utf-8", null), node).ToString();
    }

    public static string EpisodeNfo(HistoryRecord record)
    {
        var s = record.Snapshot;
        var season = s.SeasonNumber ?? 0;
        var number = s.EpisodeNumber ?? 0;
        var title = string.IsNullOrWhiteSpace(s.Name) ? ("第 " + number.ToString(CultureInfo.InvariantCulture) + " 集") : s.Name;
        var node = new XElement("episodedetails",
            new XElement("title", "[历史] " + Xml(title)),
            new XElement("season", season.ToString(CultureInfo.InvariantCulture)),
            new XElement("episode", number.ToString(CultureInfo.InvariantCulture)),
            new XElement("plot", Plot(record.FirstSeenUtc, record.LastSeenUtc, null, 0, 0) + "\n\n" + Xml(s.Overview)),
            new XElement("lockdata", "true"),
            new XElement("uniqueid", new XAttribute("type", "embyhistory"), new XAttribute("default", "true"), record.Key),
            new XElement("tag", "EmbyHistory"), new XElement("tag", "已移出"));
        if (s.Year is int year) node.Add(new XElement("year", year.ToString(CultureInfo.InvariantCulture)));
        if (!string.IsNullOrWhiteSpace(s.SeriesName)) node.Add(new XElement("showtitle", "[历史] " + Xml(s.SeriesName)));
        AddProviderIds(node, s.ProviderIds);
        foreach (var genre in s.Genres) node.Add(new XElement("genre", Xml(genre)));
        foreach (var actor in s.Actors) node.Add(new XElement("actor", new XElement("name", Xml(actor.Name)), new XElement("role", Xml(actor.Role))));
        return new XDocument(new XDeclaration("1.0", "utf-8", null), node).ToString();
    }

    // Series provider ids must never be copied onto an episode. The local archive id stays default.
    private static void AddProviderIds(XElement node, Dictionary<string, string> ids)
    {
        foreach (var pair in ids.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            var type = pair.Key.Trim().ToLowerInvariant();
            if (type == "embyhistory" || string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(pair.Value)) continue;
            node.Add(new XElement("uniqueid", new XAttribute("type", Xml(type)), new XAttribute("default", "false"), Xml(pair.Value)));
            // Native Emby NFO readers also recognize these legacy provider tags. Emit both forms
            // for compatibility, with the same object-scoped value (never a parent series ID).
            if (type is "tmdb" or "tvdb" or "imdb") node.Add(new XElement(type + "id", Xml(pair.Value)));
        }
    }

    private static string Plot(DateTime firstSeen, DateTime lastSeen, int? season, int seasonCount, int episodeCount)
    {
        var text = new StringBuilder("【永久历史记录：已移出源库，无可播放文件】\n");
        text.Append("首次记录（非真实首次入库）：").Append(firstSeen.ToString("u")).Append('\n');
        text.Append("最后在库：").Append(lastSeen.ToString("u")).Append('\n');
        if (season is int value) text.Append("季：第 ").Append(value.ToString(CultureInfo.InvariantCulture)).Append(" 季\n");
        if (seasonCount > 0) text.Append("已移出季数：").Append(seasonCount.ToString(CultureInfo.InvariantCulture)).Append('\n');
        if (episodeCount > 0) text.Append("已移出集数：").Append(episodeCount.ToString(CultureInfo.InvariantCulture)).Append('\n');
        return text.ToString();
    }

    private static string Xml(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var builder = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                builder.Append(c).Append(value[++i]);
                continue;
            }
            builder.Append(System.Xml.XmlConvert.IsXmlChar(c) ? c : ' ');
        }
        return builder.ToString();
    }
}
