using System.Security.Cryptography;
using System.Text;
using Gelato.Config;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Gelato.Channels;

/// <summary>
/// The addon's catalogs as a Jellyfin channel, browsed live the way Stremio's Discover does:
/// nothing is imported to see them. The movie catalogs and the show catalogs are each grouped in a
/// folder; each catalog is a folder holding one page of the addon's answer, and a "More" folder at
/// its end holds the next one.
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

    /// <summary>How many titles a catalog folder holds before its "More" folder.</summary>
    private const int PageSize = 50;

    /// <summary>The most of the addon's pages asked for to fill one of the channel's.</summary>
    private const int MaxAddonPages = 5;

    /// <summary>The position "More" takes, after every title of a page.</summary>
    private const int MorePosition = 1_000_000;

    private const string GroupPrefix = "g";
    private const string CatalogPrefix = "c";
    private const string ItemPrefix = "i";

    /// <summary>
    /// Starts from the time the server started: Jellyfin keeps its cache of a folder on disk, and
    /// a counter starting from 0 again would make a listing made before a restart (by other
    /// settings, or an older Gelato) look current.
    /// </summary>
    private static long _dataVersion = DateTime.UtcNow.Ticks;

    private readonly ILibraryManager _libraryManager;
    private readonly IMemoryCache _cache;
    private readonly ILogger<CatalogChannel> _log;
    private readonly CatalogArt _art;
    private readonly Lazy<Guid> _channelId;

    public CatalogChannel(
        ILibraryManager libraryManager,
        IMemoryCache cache,
        ILogger<CatalogChannel> log,
        CatalogArt art
    )
    {
        _libraryManager = libraryManager;
        _cache = cache;
        _log = log;
        _art = art;
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

    /// <summary>The channel's card (<see cref="CatalogArt.GetChannel"/>).</summary>
    public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken ct) =>
        Task.FromResult(
            _art.GetChannel(thumb: type is ImageType.Thumb) is { } path
                ? new DynamicImageResponse
                {
                    Path = path,
                    Protocol = MediaProtocol.File,
                    Format = ImageFormat.Svg,
                    HasImage = true,
                }
                : new DynamicImageResponse { HasImage = false }
        );

    public IEnumerable<ImageType> GetSupportedChannelImages() =>
        [ImageType.Primary, ImageType.Thumb];

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
            {
                var top = await ListTopAsync(stremio, cfg).ConfigureAwait(false);
                RemoveUnlisted(top);
                return Result(top);
            }

            if (TryParseGroup(query.FolderId, out var kind))
                return Result(
                    await ListCatalogsAsync(
                            stremio,
                            (await CatalogsAsync(stremio, cfg).ConfigureAwait(false))
                                .Where(c => KindOf(c.Type) == kind)
                                .ToList(),
                            grouped: true
                        )
                        .ConfigureAwait(false)
                );

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
    /// The channel's first level: a Movies and a Shows folder, each holding its kind's catalogs,
    /// so a client shows two cards to choose between, not every catalog side by side. With
    /// catalogs of one kind only, those catalogs themselves.
    /// </summary>
    private async Task<List<ChannelItemInfo>> ListTopAsync(
        GelatoStremioProvider stremio,
        PluginConfiguration cfg
    )
    {
        var catalogs = await CatalogsAsync(stremio, cfg).ConfigureAwait(false);
        var kinds = catalogs.Select(c => KindOf(c.Type)!.Value).Distinct().ToList();
        if (kinds.Count < 2)
            return await ListCatalogsAsync(stremio, catalogs, grouped: false).ConfigureAwait(false);

        var groups = new List<ChannelItemInfo>();
        foreach (var kind in kinds)
        {
            var movies = kind is StremioMediaType.Movie;
            var ofKind = catalogs
                .Where(c => KindOf(c.Type) == kind)
                .Select(c => (c.Type, c.Id, c.Name))
                .ToList();
            var externalId = Encode(GroupPrefix, movies ? "movie" : "series");
            var card = await _art.GetGroupAsync(stremio, movies, ofKind).ConfigureAwait(false);
            if (card is not null)
                await PointFolderAtAsync(externalId, card).ConfigureAwait(false);

            groups.Add(
                new ChannelItemInfo
                {
                    Id = externalId,
                    Name = movies ? "Movies" : "Shows",
                    Type = ChannelItemType.Folder,
                    FolderType = ChannelFolderType.Container,
                    IndexNumber = groups.Count,
                    ImageUrl = card,
                }
            );
        }

        return groups;
    }

    /// <summary>
    /// Takes the items Jellyfin keeps at the channel's first level out where the level no longer
    /// lists them: the catalogs that were there before they were grouped, or the other way round.
    /// </summary>
    /// <remarks>
    /// Jellyfin removes an unlisted item only when it is in the page the client asked for, so a
    /// first look at the channel asking for one item left the rest for the three hours the listing
    /// is kept. A catalog taken out comes back where it is listed now, under the same id.
    /// </remarks>
    private void RemoveUnlisted(List<ChannelItemInfo> listed)
    {
        var ids = listed.Select(i => i.Id).ToHashSet();
        var unlisted = _libraryManager
            .GetItemList(new InternalItemsQuery { ParentId = ChannelId })
            .Where(i => !ids.Contains(i.ExternalId ?? ""))
            .ToList();

        foreach (var item in unlisted)
        {
            try
            {
                _libraryManager.DeleteItem(item, new DeleteOptions { DeleteFileLocation = false });
            }
            catch (Exception ex)
            {
                _log.LogWarning(
                    "Gelato: catalog folder {Name} could not be taken out: {Error}",
                    item.Name,
                    ex.Message
                );
            }
        }
    }

    /// <summary>
    /// The movie and series catalogs the addon lists, in its order. One switched off in the
    /// catalog settings is left out; one the settings do not know (a user's own addon) is shown.
    /// </summary>
    private static async Task<List<StremioCatalog>> CatalogsAsync(
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
            .ToList();
    }

    /// <summary>
    /// A folder for each of <paramref name="catalogs"/>, with a card for an image
    /// (<see cref="CatalogArt"/>). In a Movies or Shows folder a catalog goes by its name alone;
    /// side by side with the other kind's, its kind follows the name.
    /// </summary>
    private async Task<List<ChannelItemInfo>> ListCatalogsAsync(
        GelatoStremioProvider stremio,
        List<StremioCatalog> catalogs,
        bool grouped
    )
    {
        // A card that cannot be written leaves the folder without an image, not the channel
        // without its catalogs.
        var cards = await Task.WhenAll(
                catalogs.Select(c =>
                    _art.GetAsync(
                        stremio,
                        c.Type,
                        c.Id,
                        c.Name,
                        KindOf(c.Type) is StremioMediaType.Movie
                    )
                )
            )
            .ConfigureAwait(false);

        for (var i = 0; i < catalogs.Count; i++)
            if (cards[i] is { } card)
                await PointFolderAtAsync(
                        Encode(CatalogPrefix, catalogs[i].Type, catalogs[i].Id, "0"),
                        card
                    )
                    .ConfigureAwait(false);

        return catalogs
            .Select(
                (c, i) =>
                    new ChannelItemInfo
                    {
                        Id = Encode(CatalogPrefix, c.Type, c.Id, "0"),
                        Name = grouped
                            ? c.Name
                            : $"{c.Name} · {(KindOf(c.Type) is StremioMediaType.Movie ? "Movies" : "Shows")}",
                        Type = ChannelItemType.Folder,
                        FolderType = ChannelFolderType.Container,
                        IndexNumber = i,
                        ImageUrl = cards[i],
                    }
            )
            .ToList();
    }

    /// <summary>
    /// One page of a catalog, and a "More" folder for the next one when the catalog pages and this
    /// page did not reach its end. An addon's page is short (AIOStreams answers 20 titles), so a
    /// page here is made of as many of the addon's as it takes to hold <see cref="PageSize"/>.
    /// </summary>
    private static async Task<List<ChannelItemInfo>> ListPageAsync(
        GelatoStremioProvider stremio,
        PluginConfiguration cfg,
        string type,
        string id,
        int skip
    )
    {
        var manifest = await stremio.GetManifestAsync().ConfigureAwait(false);
        var pages =
            manifest?.Catalogs.FirstOrDefault(c => c.Id == id && c.Type == type) is { } catalog
            && catalog.Extra.Any(e =>
                string.Equals(e.Name, "skip", StringComparison.OrdinalIgnoreCase)
            );

        var metas = new List<StremioMeta>();
        var ended = false;
        for (var asked = 0; asked < MaxAddonPages && metas.Count < PageSize; asked++)
        {
            var offset = skip + metas.Count;
            var page = await stremio
                .GetCatalogMetasAsync(id, type, skip: offset > 0 ? offset : null)
                .ConfigureAwait(false);
            metas.AddRange(page);
            if (page.Count == 0 || !pages)
            {
                ended = page.Count == 0;
                break;
            }
        }

        var items = metas
            .Select((meta, i) => (meta, position: skip + i))
            .Where(x =>
                x.meta.IsValid()
                && x.meta.Type is StremioMediaType.Movie or StremioMediaType.Series
                && (!cfg.FilterUnreleased || x.meta.IsReleased(cfg.FilterUnreleasedBufferDays))
            )
            // An addon's pages can overlap where its list moved between two of them.
            .DistinctBy(x => x.meta.Id)
            .Select(x => ToChannelItem(x.meta, type, id, x.position))
            .ToList();

        if (pages && !ended && metas.Count > 0)
            items.Add(
                new ChannelItemInfo
                {
                    Id = Encode(CatalogPrefix, type, id, (skip + metas.Count).ToString()),
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

    /// <summary>
    /// Points a catalog folder Jellyfin already keeps at its card. Jellyfin sets a channel item's
    /// image only while the item has none, so a card drawn anew would not be shown.
    /// The folder's id is the one ChannelManager gives it (GetIdToHash and GetItemById).
    /// </summary>
    private async Task PointFolderAtAsync(string externalId, string card)
    {
        var id = _libraryManager.GetNewItemId(externalId + ChannelName + "16", typeof(Folder));
        if (
            _libraryManager.GetItemById(id) is not Folder folder
            || folder.GetImageInfo(ImageType.Primary, 0)?.Path == card
        )
            return;

        try
        {
            folder.SetImagePath(ImageType.Primary, card);
            await folder
                .UpdateToRepositoryAsync(ItemUpdateType.ImageUpdate, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                "Gelato: catalog folder {Name} keeps its old image: {Error}",
                folder.Name,
                ex.Message
            );
        }
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

    private static bool TryParseGroup(string externalId, out StremioMediaType kind)
    {
        kind = default;
        if (Decode(externalId) is not [GroupPrefix, var type] || KindOf(type) is not { } k)
            return false;

        kind = k;
        return true;
    }

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
