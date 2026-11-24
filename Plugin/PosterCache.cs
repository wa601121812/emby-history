using EmbyHistory.Core;
namespace EmbyHistory;

/// <summary>
/// Copies the source sidecar into the plugin's private cache while the source still exists.
/// A poster is decoration: capture never fails the recording path, it degrades to "no poster".
/// </summary>
internal static class PosterCache
{
    private const long MaxBytes = 8 * 1024 * 1024;

    public static void Capture(MediaSnapshot snapshot, string dataPath)
    {
        try
        {
            if (!long.TryParse(snapshot.ItemId, out var id) || id <= 0 || snapshot.ItemId != id.ToString())
            {
                snapshot.PosterPath = "";
                return;
            }
            var directory = Path.Combine(dataPath, "posters");
            ArchiveWriter.EnsureSafeDirectory(directory);
            var target = Path.Combine(directory, snapshot.ItemId + ".image");
            ArchiveWriter.RejectLink(target);
            var source = snapshot.PosterPath;
            if (string.IsNullOrEmpty(source) || !File.Exists(source) || new FileInfo(source).Length > MaxBytes)
            {
                snapshot.PosterPath = File.Exists(target) ? target : "";
                return;
            }
            if (source != target && (!File.Exists(target) || File.GetLastWriteTimeUtc(source) > File.GetLastWriteTimeUtc(target)))
                Copy(source, target);
            snapshot.PosterPath = File.Exists(target) ? target : "";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Unreadable sidecar, full disk, permission change or a replaced/symlinked cache entry:
            // keep the media record and fall back to the text-only card.
            snapshot.PosterPath = "";
        }
    }

    /// <summary>
    /// Series artwork is a separate, shared image: capture it once per series so the series card
    /// still has a picture after every episode file is gone.
    /// </summary>
    public static void CaptureSeries(MediaSnapshot snapshot, string dataPath)
    {
        try
        {
            if (string.IsNullOrEmpty(snapshot.SeriesPosterPath)) return;
            var identity = SnapshotTools.SeriesIdentity(snapshot);
            if (identity.Length == 0) { snapshot.SeriesPosterPath = ""; return; }
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(identity))).ToLowerInvariant()[..32];
            var directory = Path.Combine(dataPath, "posters");
            ArchiveWriter.EnsureSafeDirectory(directory);
            var target = Path.Combine(directory, "series-" + hash + ".image");
            ArchiveWriter.RejectLink(target);
            var source = snapshot.SeriesPosterPath;
            if (!File.Exists(source) || new FileInfo(source).Length > MaxBytes)
            {
                snapshot.SeriesPosterPath = File.Exists(target) ? target : "";
                return;
            }
            if (source != target && (!File.Exists(target) || File.GetLastWriteTimeUtc(source) > File.GetLastWriteTimeUtc(target)))
                Copy(source, target);
            snapshot.SeriesPosterPath = File.Exists(target) ? target : "";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            snapshot.SeriesPosterPath = "";
        }
    }

    private static void Copy(string source, string target)
    {
        var temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                input.CopyTo(output);
                output.Flush(true);
            }
            File.Move(temp, target, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
