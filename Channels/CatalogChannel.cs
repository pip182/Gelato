using System.Security.Cryptography;
using System.Text;
using Gelato.Config;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Gelato.Channels;

/// <summary>
/// The addon's catalogs as a Jellyfin channel, browsed live the way Stremio's Discover does:
/// nothing is imported to see them. Each catalog is a folder holding one page of the addon's
/// answer, and a "More" folder at its end holds the next one.
/// </summary>
/// <remarks>
/// Jellyfin asks a channel for a whole folder and keeps the answer for three hours; it never
/// passes the start index or limit the client asked for (ChannelManager.GetChannelItemsInternal).
/// A page per folder is therefore the only paging a channel has.
/// <para>
/// Jellyfin keeps what the channel lists as items of its own, with ids it hashes from the external
/// ids given here. Such an item is no Gelato item: opening it opens the title it stands for, which
/// InsertActionFilter finds in the library or puts in, the way it does for a search result. The
/// external id carries everything that needs (<see cref="GetMeta"/>), so it works across restarts.
/// </para>
/// </remarks>
public sealed class CatalogChannel : IChannel, IHasCacheKey
{
    public const string ChannelName = "Gelato Catalogs";

    /// <summary>The position "More" takes, after every title of a page.</summary>
    private const int MorePosition = 1_000_000;

    private const string CatalogPrefix = "c";
    private const string ItemPrefix = "i";

    private static int _dataVersion;

    private readonly ILibraryManager _libraryManager;
    private readonly IMemoryCache _cache;
    private readonly ILogger<CatalogChannel> _log;
    private readonly Lazy<Guid> _channelId;

    public CatalogChannel(
        ILibraryManager libraryManager,
        IMemoryCache cache,
        ILogger<CatalogChannel> log
    )
    {
        _libraryManager = libraryManager;
        _cache = cache;
        _log = log;
        // ChannelManager names its channel entity after the channel: the hash of "Channel " + Name.
        _channelId = new Lazy<Guid>(() =>
            libraryManager.GetNewItemId("Channel " + ChannelName, typeof(Channel))
        );
        // A changed addon or setting lists other catalogs, or the same ones filtered otherwise.
        // A new data version puts Jellyfin's three-hour cache of every folder out of use.
        GelatoPlugin.ConfigurationChanged += _ => Interlocked.Increment(ref _dataVersion);
    }

    /// <summary>The id of the channel entity Jellyfin keeps for this channel.</summary>
    public Guid ChannelId => _channelId.Value;

    public string Name => ChannelName;

    public string Description => "Browse the catalogs of your AIOStreams addon.";

    public string DataVersion => Volatile.Read(ref _dataVersion).ToString();

    public string HomePageUrl => "https://github.com/lostb1t/Gelato";

    public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;

    public InternalChannelFeatures GetChannelFeatures() =>
        new()
        {
            MediaTypes = [ChannelMediaType.Video],
            ContentTypes = [ChannelMediaContentType.Movie, ChannelMediaContentType.Episode],
        };

    public bool IsEnabledFor(string userId) =>
        Guid.TryParse(userId, out var id)
        && !string.IsNullOrWhiteSpace(
            GelatoPlugin.Instance!.Configuration.GetEffectiveConfig(id).Url
        );

    /// <summary>
    /// Users with an addon of their own see its catalogs: Jellyfin's cache of a folder is told
    /// apart by the addon, which is hashed so the cache's file names do not carry its config.
    /// </summary>
    public string? GetCacheKey(string? userId)
    {
        if (!Guid.TryParse(userId, out var id))
            return null;

        var url = GelatoPlugin.Instance!.Configuration.GetEffectiveConfig(id).Url;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..16];
    }

    public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken ct) =>
        Task.FromResult(new DynamicImageResponse { HasImage = false });

    public IEnumerable<ImageType> GetSupportedChannelImages() => [];

    public async Task<ChannelItemResult> GetChannelItems(
        InternalChannelItemQuery query,
        CancellationToken cancellationToken
    )
    {
        var cfg = GelatoPlugin.Instance!.GetConfig(query.UserId);
        if (cfg.Stremio is not { } stremio)
            return new ChannelItemResult();

        try
        {
            if (string.IsNullOrEmpty(query.FolderId))
                return Result(await ListCatalogsAsync(stremio, cfg).ConfigureAwait(false));

            // A series is a folder to Jellyfin, but it opens as the library's series, never as
            // channel content: it has nothing to list.
            if (TryParseCatalogFolder(query.FolderId, out var type, out var id, out var skip))
                return Result(
                    await ListPageAsync(stremio, cfg, type, id, skip).ConfigureAwait(false)
                );
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // An answer Jellyfin keeps for three hours: an addon that failed once would leave the
            // folder empty for that long. Throwing makes it ask again on the next visit.
            _log.LogWarning(
                "Gelato: catalog folder {Folder} could not be listed: {Error}",
                query.FolderId,
                ex.Message
            );
            throw;
        }

        return new ChannelItemResult();
    }

    /// <summary>
    /// The title a channel item stands for, or null when the id is no item of this channel. Read
    /// from the item's external id, so it answers after a restart too. Ids that are no channel item
    /// are remembered for a while: every id of every request is asked about.
    /// </summary>
    public StremioMeta? GetMeta(Guid itemId)
    {
        if (itemId.Equals(Guid.Empty) || _cache.TryGetValue($"catalogchannel-miss:{itemId}", out _))
            return null;

        if (_cache.TryGetValue($"catalogchannel:{itemId}", out StremioMeta? known))
            return known;

        var meta =
            _libraryManager.GetItemById(itemId) is { } item
            && item.ChannelId.Equals(ChannelId)
            && TryParseItem(item.ExternalId, out var parsed)
                ? parsed
                : null;

        if (meta is null)
            _cache.Set($"catalogchannel-miss:{itemId}", true, TimeSpan.FromMinutes(10));
        else
            _cache.Set($"catalogchannel:{itemId}", meta, TimeSpan.FromHours(6));

        return meta;
    }

    /// <summary>Whether the folder is this channel or one of its folders.</summary>
    public bool IsChannelFolder(Guid folderId) =>
        folderId.Equals(ChannelId)
        || (
            !folderId.Equals(Guid.Empty)
            && _libraryManager.GetItemById(folderId) is Folder folder
            && folder.ChannelId.Equals(ChannelId)
        );

    private static ChannelItemResult Result(List<ChannelItemInfo> items) =>
        new() { Items = items, TotalRecordCount = items.Count };

    /// <summary>
    /// The movie and series catalogs the addon lists, in its order. One switched off in the
    /// catalog settings is left out; one the settings do not know (a user's own addon) is shown.
    /// </summary>
    private static async Task<List<ChannelItemInfo>> ListCatalogsAsync(
        GelatoStremioProvider stremio,
        PluginConfiguration cfg
    )
    {
        var disabled = cfg.Catalogs.Where(c => !c.Enabled).Select(c => c.GetKey()).ToHashSet();

        var manifest = await stremio.GetManifestAsync().ConfigureAwait(false);
        if (manifest is null)
            throw new HttpRequestException("The addon's manifest could not be read.");

        return manifest
            .Catalogs.Where(c =>
                c.IsImportable()
                && KindOf(c.Type) is not null
                && !disabled.Contains($"{c.Type}:{c.Id}")
            )
            .Select(
                (c, i) =>
                    new ChannelItemInfo
                    {
                        Id = Encode(CatalogPrefix, c.Type, c.Id, "0"),
                        Name =
                            $"{c.Name} · {(KindOf(c.Type) is StremioMediaType.Movie ? "Movies" : "Shows")}",
                        Type = ChannelItemType.Folder,
                        FolderType = ChannelFolderType.Container,
                        IndexNumber = i,
                    }
            )
            .ToList();
    }

    /// <summary>
    /// One page of a catalog, and a "More" folder for the next one when the catalog pages and this
    /// page was not empty.
    /// </summary>
    private static async Task<List<ChannelItemInfo>> ListPageAsync(
        GelatoStremioProvider stremio,
        PluginConfiguration cfg,
        string type,
        string id,
        int skip
    )
    {
        var page = await stremio
            .GetCatalogMetasAsync(id, type, skip: skip > 0 ? skip : null)
            .ConfigureAwait(false);

        var items = page.Select((meta, i) => (meta, position: skip + i))
            .Where(x =>
                x.meta.IsValid()
                && x.meta.Type is StremioMediaType.Movie or StremioMediaType.Series
                && (!cfg.FilterUnreleased || x.meta.IsReleased(cfg.FilterUnreleasedBufferDays))
            )
            .Select(x => ToChannelItem(x.meta, type, id, x.position))
            .ToList();

        var manifest = await stremio.GetManifestAsync().ConfigureAwait(false);
        var pages =
            manifest?.Catalogs.FirstOrDefault(c => c.Id == id && c.Type == type) is { } catalog
            && catalog.Extra.Any(e =>
                string.Equals(e.Name, "skip", StringComparison.OrdinalIgnoreCase)
            );
        if (pages && page.Count > 0)
            items.Add(
                new ChannelItemInfo
                {
                    Id = Encode(CatalogPrefix, type, id, (skip + page.Count).ToString()),
                    Name = "More…",
                    Type = ChannelItemType.Folder,
                    FolderType = ChannelFolderType.Container,
                    IndexNumber = MorePosition,
                }
            );

        return items;
    }

    /// <remarks>
    /// The position is part of the id: Jellyfin copies a channel item's fields only when it first
    /// makes it, so a title that moved in the catalog would keep the place it had. It gets a new item
    /// in its new place instead, and Jellyfin drops the old one with the page it is no longer on.
    /// The provider ids stay off: the item is no copy of the title, and Gelato finds titles in the
    /// library by them.
    /// </remarks>
    private static ChannelItemInfo ToChannelItem(
        StremioMeta meta,
        string catalogType,
        string catalogId,
        int position
    )
    {
        var isSeries = meta.Type is StremioMediaType.Series;
        return new ChannelItemInfo
        {
            Id = Encode(
                ItemPrefix,
                catalogType,
                catalogId,
                position.ToString(),
                isSeries ? "series" : "movie",
                meta.Id,
                meta.ImdbId ?? ""
            ),
            Name = meta.GetName(),
            Overview = meta.Description ?? meta.Overview,
            ImageUrl = meta.Poster,
            Genres = meta.Genres ?? [],
            ProductionYear = meta.GetYear(),
            PremiereDate = meta.GetPremiereDate(),
            CommunityRating = meta.ImdbRating,
            IndexNumber = position,
            Type = isSeries ? ChannelItemType.Folder : ChannelItemType.Media,
            FolderType = isSeries ? ChannelFolderType.Series : ChannelFolderType.Container,
            MediaType = ChannelMediaType.Video,
            ContentType = isSeries
                ? ChannelMediaContentType.Episode
                : ChannelMediaContentType.Movie,
        };
    }

    private static StremioMediaType? KindOf(string catalogType) =>
        catalogType.ToLowerInvariant() switch
        {
            "movie" => StremioMediaType.Movie,
            "series" => StremioMediaType.Series,
            _ => null,
        };

    private static string Encode(params string[] parts) =>
        string.Join('/', parts.Select(Uri.EscapeDataString));

    private static string[] Decode(string? externalId) =>
        string.IsNullOrEmpty(externalId)
            ? []
            : externalId.Split('/').Select(Uri.UnescapeDataString).ToArray();

    private static bool TryParseCatalogFolder(
        string externalId,
        out string type,
        out string id,
        out int skip
    )
    {
        var parts = Decode(externalId);
        (type, id, skip) = ("", "", 0);
        if (parts is not [CatalogPrefix, var t, var i, var s] || !int.TryParse(s, out skip))
            return false;

        (type, id) = (t, i);
        return true;
    }

    private static bool TryParseItem(string? externalId, out StremioMeta? meta)
    {
        meta = Decode(externalId) switch
        {
            [ItemPrefix, _, _, _, var type, var id, var imdb] when id.Length > 0 => new StremioMeta
            {
                Id = id,
                Type = type == "series" ? StremioMediaType.Series : StremioMediaType.Movie,
                ImdbId = imdb.Length > 0 ? imdb : null,
            },
            _ => null,
        };
        return meta is not null;
    }
}
