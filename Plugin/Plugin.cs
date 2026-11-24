using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace EmbyHistory;

public sealed class Plugin : BasePlugin<Configuration>, IHasWebPages, IHasThumbImage
{
    private const string ThumbResource = "EmbyHistory.Assets.plugin-icon.png";
    public static Plugin? Instance { get; private set; }
    public override string Name => "Emby History";
    public override string Description => "永久保存入库历史，并生成可搜索的已移出影片离线卡片。默认禁用，需显式选择源库并配置历史库权限。";
    public override Guid Id => new("82d39a44-2b97-4d7d-8fa6-94b8f7f5aa01");
    private readonly IApplicationPaths _paths;
    public string HistoryDataPath => Path.Combine(_paths.DataPath, "emby-history");
    public Plugin(IApplicationPaths paths, IXmlSerializer xmlSerializer) : base(paths, xmlSerializer)
    {
        _paths = paths;
        Instance = this;
    }

    public ImageFormat ThumbImageFormat => ImageFormat.Png;

    /// <summary>The icon is embedded in the assembly: nothing extra to ship or lose on upgrade.</summary>
    public Stream GetThumbImage()
    {
        var stream = typeof(Plugin).Assembly.GetManifestResourceStream(ThumbResource);
        if (stream == null) throw new InvalidOperationException("Embedded plugin icon is missing: " + ThumbResource);
        return stream;
    }

    public IEnumerable<PluginPageInfo> GetPages() => new[]
    {
        new PluginPageInfo { Name = "EmbyHistory", EmbeddedResourcePath = "EmbyHistory.Configuration.config.html" },
        // Emby loads the page controller through the same configuration-page endpoint, and picks
        // the MIME type from the ".js" suffix; without it require() refuses the module.
        new PluginPageInfo { Name = "EmbyHistory.js", EmbeddedResourcePath = "EmbyHistory.Configuration.config.js" }
    };
}
