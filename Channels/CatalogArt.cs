using System.Collections.Concurrent;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Gelato.Channels;

/// <summary>
/// The image of a catalog folder in <see cref="CatalogChannel"/>: a card with an icon for what
/// kind of list the catalog is (popular, top, by year, ...), its name, a line saying what such a
/// list is and whether it holds movies or shows. Drawn as an SVG, which Jellyfin renders itself, and kept in its cache folder.
/// </summary>
/// <remarks>
/// A card is named by a hash of its content, so a card that changes gets a path of its own:
/// Jellyfin keeps its resized copies of an image by path.
/// </remarks>
public sealed class CatalogArt(
    IApplicationPaths appPaths,
    IHttpClientFactory http,
    ILogger<CatalogArt> log
)
{
    /// <summary>How many of a catalog's first posters fan out across the bottom of its card.</summary>
    private const int Posters = 3;

    /// <summary>The size posters are embedded at: twice what the card shows them at.</summary>
    private const int PosterWidth = 264;
    private const int PosterHeight = 396;

    /// <summary>
    /// How long a catalog's posters are kept before they are asked for again: the catalog listing
    /// is asked for every three hours, and its first titles change more slowly than that.
    /// </summary>
    private static readonly TimeSpan PostersFor = TimeSpan.FromHours(6);

    private readonly ConcurrentDictionary<
        string,
        (IReadOnlyList<string> Uris, DateTime Until)
    > _posters = new();

    // Square: Jellyfin's clients lay out cards by their image's shape, and fit fewer, larger
    // square cards to a row than poster-shaped ones.
    private const int Width = 600;
    private const int Height = 600;

    /// <summary>The height of a card's poster-shaped twin: 2:3, as posters are.</summary>
    private const int TallHeight = 900;

    /// <summary>The room kept clear on either side of the name.</summary>
    private const int Gutter = 40;

    /// <summary>
    /// The width of the coloured frame round a card's dark panel: 5 pixels on a phone, where a
    /// card of 600 is shown about 180 wide.
    /// </summary>
    private const int Frame = 16;

    /// <summary>
    /// The corner radius cards are shown with, in the card's pixels: the catalogs' rounded by
    /// the server's custom CSS (1.25em), the channel's by the clients' own (0.2em).
    /// </summary>
    private const int CardCorner = 66;
    private const int ChannelCorner = 12;

    private const int MaxTitleSize = 80;
    private const int MinTitleSize = 32;
    private const int MaxTaglineSize = 30;
    private const int MinTaglineSize = 20;

    private const string FontFamilies = "Noto Sans, DejaVu Sans, sans-serif";

    /// <summary>
    /// An icon as the inner markup of a 24x24, stroked SVG, its card's colours and a line saying
    /// what such a list is.
    /// </summary>
    private sealed record Style(string Icon, string From, string To, string Tagline);

    // Icons after Lucide (ISC licence), drawn on a 24x24 grid.
    private static readonly Style Popular = new(
        """<path d="M8.5 14.5A2.5 2.5 0 0 0 11 12c0-1.38-.5-2-1-3-1.07-2.14-.22-4.05 2-6 .5 2.5 2 4.9 4 6.5 2 1.6 3 3.5 3 5.5a7 7 0 1 1-14 0c0-1.15.43-2.29 1-3a2.5 2.5 0 0 0 2.5 2.5z"/>""",
        "#7c1d0f",
        "#ea580c",
        "What everyone's watching"
    );
    private static readonly Style Top = new(
        """<path d="M6 9H4.5a2.5 2.5 0 0 1 0-5H6"/><path d="M18 9h1.5a2.5 2.5 0 0 0 0-5H18"/><path d="M4 22h16"/><path d="M10 14.66V17c0 .55-.47.98-.97 1.21C7.85 18.75 7 20.24 7 22"/><path d="M14 14.66V17c0 .55.47.98.97 1.21C16.15 18.75 17 20.24 17 22"/><path d="M18 2H6v7a6 6 0 0 0 12 0V2Z"/>""",
        "#713f12",
        "#ca8a04",
        "The most seeded right now"
    );
    private static readonly Style Trending = new(
        """<polyline points="22 7 13.5 15.5 8.5 10.5 2 17"/><polyline points="16 7 22 7 22 13"/>""",
        "#064e3b",
        "#059669",
        "Climbing the charts"
    );
    private static readonly Style Year = new(
        """<rect x="3" y="4" width="18" height="18" rx="2"/><path d="M16 2v4"/><path d="M8 2v4"/><path d="M3 10h18"/>""",
        "#1e3a8a",
        "#2563eb",
        "Browse by release year"
    );
    private static readonly Style Language = new(
        """<circle cx="12" cy="12" r="10"/><path d="M12 2a14.5 14.5 0 0 0 0 20 14.5 14.5 0 0 0 0-20"/><path d="M2 12h20"/>""",
        "#134e4a",
        "#0d9488",
        "From around the world"
    );
    private static readonly Style Genre = new(
        """<path d="M12.59 2.59A2 2 0 0 0 11.17 2H4a2 2 0 0 0-2 2v7.17a2 2 0 0 0 .59 1.42l8.7 8.7a2.43 2.43 0 0 0 3.42 0l6.58-6.58a2.43 2.43 0 0 0 0-3.42z"/><circle cx="7.5" cy="7.5" r="1"/>""",
        "#4c1d95",
        "#7c3aed",
        "Browse by genre"
    );
    private static readonly Style Rated = new(
        """<polygon points="12 2 15.09 8.26 22 9.27 17 14.14 18.18 21.02 12 17.77 5.82 21.02 7 14.14 2 9.27 8.91 8.26 12 2"/>""",
        "#78350f",
        "#d97706",
        "The highest rated"
    );
    private static readonly Style New = new(
        """<path d="M12 3l1.9 5.8L20 11l-6.1 2.2L12 19l-1.9-5.8L4 11l6.1-2.2z"/><path d="M19 3v4"/><path d="M17 5h4"/>""",
        "#831843",
        "#db2777",
        "Fresh releases"
    );
    private static readonly Style Movies = new(
        """<rect x="3" y="3" width="18" height="18" rx="2"/><path d="M7 3v18"/><path d="M3 7.5h4"/><path d="M3 12h18"/><path d="M3 16.5h4"/><path d="M17 3v18"/><path d="M17 7.5h4"/><path d="M17 16.5h4"/>""",
        "#1f2937",
        "#475569",
        "Browse the catalog"
    );
    private static readonly Style Shows = new(
        """<rect x="2" y="7" width="20" height="15" rx="2"/><polyline points="17 2 12 7 7 2"/>""",
        "#1f2937",
        "#475569",
        "Browse the catalog"
    );

    // The folders the catalogs are grouped in; their line names the catalogs inside.
    private static readonly Style MoviesGroup = new(
        Movies.Icon,
        "#312e81",
        "#6366f1",
        "Browse the catalog"
    );
    private static readonly Style ShowsGroup = new(
        Shows.Icon,
        "#701a75",
        "#c026d3",
        "Browse the catalog"
    );

    // Lucide's ice-cream-cone.
    private static readonly Style Gelato = new(
        """<path d="m7 11 4.08 10.35a1 1 0 0 0 1.84 0L17 11"/><path d="M17 7A5 5 0 0 0 7 7"/><path d="M17 7a2 2 0 0 1 0 4H7a2 2 0 0 1 0-4"/>""",
        "#6d28d9",
        "#ec4899",
        "Browse your addon's catalogs"
    );

    /// <summary>What a catalog's name or id says about it, the first match winning.</summary>
    private static readonly (string[] Words, Style Style)[] Kinds =
    [
        (["trending", "hot"], Trending),
        (["popular"], Popular),
        (["top", "best", "seeded"], Top),
        (["rated", "rating", "imdb"], Rated),
        (["year", "decade"], Year),
        (["language", "country", "region"], Language),
        (["genre"], Genre),
        (["new", "latest", "recent", "release", "upcoming"], New),
    ];

    /// <summary>
    /// The card's path, drawn first when there is none. Null when it cannot be written. Its first
    /// posters are part of it, so a catalog whose first titles changed gets a card of its own.
    /// </summary>
    public async Task<string?> GetAsync(
        GelatoStremioProvider stremio,
        string type,
        string id,
        string name,
        bool movies
    )
    {
        var posters = await PostersAsync(stremio, type, id).ConfigureAwait(false);
        return WriteCard(
            movies ? "MOVIES" : "SHOWS",
            name,
            StyleOf(id, name, movies),
            posters,
            $"catalog {type}/{id}"
        );
    }

    /// <summary>
    /// The card of the folder a kind's catalogs are grouped in: Movies or Shows, the catalogs it
    /// holds named under it, and the first one's posters.
    /// </summary>
    public async Task<string?> GetGroupAsync(
        GelatoStremioProvider stremio,
        bool movies,
        IReadOnlyList<(string Type, string Id, string Name)> catalogs
    )
    {
        var posters =
            catalogs.Count > 0
                ? await PostersAsync(stremio, catalogs[0].Type, catalogs[0].Id)
                    .ConfigureAwait(false)
                : [];
        var style = (movies ? MoviesGroup : ShowsGroup) with
        {
            Tagline = string.Join(" · ", catalogs.Select(c => c.Name)),
        };
        return WriteCard(
            "CATALOGS",
            movies ? "Movies" : "Shows",
            style,
            posters,
            $"the {(movies ? "movie" : "show")} catalogs"
        );
    }

    /// <summary>
    /// The square card, written with its poster-shaped twin beside it (<see cref="TallOf"/>).
    /// </summary>
    private string? WriteCard(
        string heading,
        string name,
        Style style,
        IReadOnlyList<string> posters,
        string what
    ) =>
        Write(
            Draw(Width, Height, CardCorner, heading, name, style, posters),
            what,
            Draw(Width, TallHeight, CardCorner, heading, name, style, posters)
        );

    /// <summary>
    /// The poster-shaped twin of a card, for a client that shows folders as posters and asks for
    /// an image of that shape (the Roku app): the square card would be cut at its sides. Null
    /// when <paramref name="card"/> is no card or has none.
    /// </summary>
    public static string? TallOf(string? card)
    {
        if (
            card is null
            || !card.EndsWith(".svg", StringComparison.Ordinal)
            || Path.GetFileName(Path.GetDirectoryName(card)) != "catalog-art"
        )
            return null;

        var tall = card[..^4] + "-tall.svg";
        return File.Exists(tall) ? tall : null;
    }

    /// <summary>
    /// The catalog's first posters as data URIs, shrunk: an SVG Jellyfin renders may reference
    /// nothing outside itself. None when the addon or the posters cannot be reached; the card is
    /// then drawn without them.
    /// </summary>
    private async Task<IReadOnlyList<string>> PostersAsync(
        GelatoStremioProvider stremio,
        string type,
        string id
    )
    {
        var key = $"{type}:{id}";
        if (_posters.TryGetValue(key, out var known) && known.Until > DateTime.UtcNow)
            return known.Uris;

        try
        {
            var urls = (await stremio.GetCatalogMetasAsync(id, type).ConfigureAwait(false))
                .Select(m => m.Poster)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct()
                .Take(Posters)
                .ToList();
            var uris = (await Task.WhenAll(urls.Select(u => PosterAsync(u!))).ConfigureAwait(false))
                .OfType<string>()
                .ToList();
            _posters[key] = (uris, DateTime.UtcNow.Add(PostersFor));
            return uris;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            log.LogDebug("Gelato: no posters for catalog {Key}: {Error}", key, ex.Message);
            return known.Uris ?? [];
        }
    }

    private async Task<string?> PosterAsync(string url)
    {
        try
        {
            var client = http.CreateClient(nameof(CatalogArt));
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.UserAgent.TryParseAdd(GelatoPlugin.UserAgent);
            var bytes = await client.GetByteArrayAsync(url).ConfigureAwait(false);
            return "data:image/jpeg;base64," + Convert.ToBase64String(Shrink(bytes));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            log.LogDebug("Gelato: poster {Url} could not be downloaded: {Error}", url, ex.Message);
            return null;
        }
    }

    /// <summary>A poster as a JPEG <see cref="PosterWidth"/> wide; as it came when it cannot be.</summary>
    private static byte[] Shrink(byte[] poster)
    {
        try
        {
            return ShrinkWithSkia(poster) ?? poster;
        }
        catch (Exception ex)
            when (ex is TypeLoadException or FileNotFoundException or DllNotFoundException)
        {
            return poster;
        }
    }

    // Kept apart so a SkiaSharp that cannot be loaded fails the call, not the caller's compile.
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining
    )]
    private static byte[]? ShrinkWithSkia(byte[] poster)
    {
        using var source = SkiaSharp.SKBitmap.Decode(poster);
        if (source is null)
            return null;
        using var shrunk = source.Resize(
            new SkiaSharp.SKImageInfo(PosterWidth, PosterHeight),
            new SkiaSharp.SKSamplingOptions(
                SkiaSharp.SKFilterMode.Linear,
                SkiaSharp.SKMipmapMode.Linear
            )
        );
        if (shrunk is null)
            return null;
        using var image = SkiaSharp.SKImage.FromBitmap(shrunk);
        using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, 82);
        return data.ToArray();
    }

    /// <summary>
    /// The channel's own card, the same way drawn: square for its primary image and 16:9 for its
    /// thumb, which home screens show it by.
    /// </summary>
    public string? GetChannel(bool thumb) =>
        Write(
            thumb
                ? Draw(960, 540, ChannelCorner, "CATALOGS", "Gelato", Gelato, [])
                : Draw(Width, Height, ChannelCorner, "CATALOGS", "Gelato", Gelato, []),
            "the catalog channel"
        );

    private string? Write(string svg, string what, string? tall = null)
    {
        var path = Path.Combine(appPaths.CachePath, "gelato", "catalog-art", $"{Hash(svg)}.svg");
        var tallPath = path[..^4] + "-tall.svg";
        if (File.Exists(path) && (tall is null || File.Exists(tallPath)))
            return path;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // The twin first: a card is taken to have one once it is there.
            if (tall is not null)
                WriteWhole(tallPath, tall);
            WriteWhole(path, svg);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning("Gelato: no image for {What}: {Error}", what, ex.Message);
            return null;
        }
    }

    /// <summary>Written beside its place and moved there, so a card half written is never used.</summary>
    private static void WriteWhole(string path, string svg)
    {
        var partial = path + ".partial";
        File.WriteAllText(partial, svg);
        File.Move(partial, path, overwrite: true);
    }

    private static Style StyleOf(string id, string name, bool movies)
    {
        foreach (var (words, style) in Kinds)
            if (words.Any(w => Says(name, w) || Says(id, w)))
                return style;
        return movies ? Movies : Shows;
    }

    /// <summary>Whether <paramref name="text"/> holds <paramref name="word"/> as a word of its own.</summary>
    private static bool Says(string text, string word)
    {
        var at = 0;
        while ((at = text.IndexOf(word, at, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var end = at + word.Length;
            if (
                (at == 0 || !char.IsLetter(text[at - 1]))
                && (end == text.Length || !char.IsLetter(text[end]))
            )
                return true;
            at = end;
        }
        return false;
    }

    /// <summary>
    /// A card <paramref name="width"/> by <paramref name="height"/>: <paramref name="heading"/>
    /// spaced wide at the top, the style's icon, <paramref name="name"/> and the style's line, and
    /// <paramref name="posters"/> fanned out across the bottom when there are any.
    /// </summary>
    private static string Draw(
        int width,
        int height,
        int corner,
        string heading,
        string name,
        Style style,
        IReadOnlyList<string> posters
    )
    {
        // Posters take the bottom of the card, and what is above them moves up and shrinks.
        var peek = posters.Count > 0;
        // A poster-shaped card has room for larger posters, and keeps what is above them at its top.
        var tall = height > width;

        // The card's colour shows as a frame round a panel 80% darker, its corners following the
        // card's own (a frame of even width).
        var panel = width - 2 * Frame;
        var panelHeight = height - 2 * Frame;
        var panelCorner = Math.Max(corner - Frame, 4);

        // Laid out for a square card; another keeps it centred, or at its top above the posters.
        var centre = width / 2;
        var dy = tall && peek ? 50 : (height - Height) / 2;

        var text = name.Trim();
        var (titleX, titleSize) = Fit(text, width, MaxTitleSize, MinTitleSize, bold: true);
        var (taglineX, taglineSize) = Fit(
            style.Tagline,
            width,
            MaxTaglineSize,
            MinTaglineSize,
            bold: false
        );
        // The name hangs from just under the icon, the line saying what the list is under it.
        var titleY = dy + (peek ? 222 : 300) + titleSize;
        var taglineY = titleY + (peek ? 46 : 58);
        var title = SecurityElement.Escape(text);
        var tagline = SecurityElement.Escape(style.Tagline);

        // The heading tops the card, its letters spaced wide, between two rules running out to
        // the gutters.
        const int headingSize = 46;
        const int headingSpacing = 16;
        var headingY = dy + (peek ? 92 : 108);
        var ruleY = headingY - 17;
        // Each letter drawn on its own: Svg.Skia does not space letters by letter-spacing.
        var advances = heading.Select(c => Measure(c.ToString(), headingSize, bold: true)).ToList();
        var headingWidth = (int)(advances.Sum() + (heading.Length - 1) * headingSpacing);
        var headingX = (width - headingWidth) / 2;
        var letters = new StringBuilder();
        var letterX = (float)headingX;
        for (var i = 0; i < heading.Length; i++)
        {
            letters.Append(
                $"""<text x="{letterX:0.#}" y="{headingY}" font-family="{FontFamilies}" font-weight="800" font-size="{headingSize}" fill="#ffffff" fill-opacity="0.92">{heading[i]}</text>"""
            );
            letterX += advances[i] + headingSpacing;
        }
        var ruleEnd = headingX - 28;
        var ruleStart = headingX + headingWidth + 28;
        var ruleRight = width - Gutter;

        // The icon in a disc under the heading, and, where no posters are, large and faint in the
        // bottom right corner.
        var discY = dy + (peek ? 168 : 212);
        var discR = peek ? 50 : 66;
        var iconScale = peek ? 2.6 : 3.4;
        var iconX = centre - 12 * iconScale;
        var iconY = discY - 12 * iconScale;
        var mark = peek
            ? ""
            : $"""<g transform="translate({width - 230} {height - 230}) scale(9)" fill="none" stroke="#ffffff" stroke-opacity="0.08" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round">{style.Icon}</g>""";
        var fan = Fan(posters, centre, height - Frame, large: tall);

        return $"""
            <svg xmlns="http://www.w3.org/2000/svg" width="{width}" height="{height}" viewBox="0 0 {width} {height}">
              <defs>
                <linearGradient id="bg" x1="0" y1="0" x2="1" y2="1">
                  <stop offset="0" stop-color="{Muted(style.To)}"/>
                  <stop offset="1" stop-color="{Muted(style.From)}"/>
                </linearGradient>
                <radialGradient id="glow" cx="0.5" cy="0.3" r="0.6">
                  <stop offset="0" stop-color="{Muted(style.To)}" stop-opacity="0.45"/>
                  <stop offset="1" stop-color="{Muted(style.To)}" stop-opacity="0"/>
                </radialGradient>
                <clipPath id="panel">
                  <rect x="{Frame}" y="{Frame}" width="{panel}" height="{panelHeight}" rx="{panelCorner}"/>
                </clipPath>
                <linearGradient id="fade" x1="0" y1="0" x2="0" y2="1">
                  <stop offset="0" stop-color="#000000" stop-opacity="0"/>
                  <stop offset="1" stop-color="#000000" stop-opacity="0.8"/>
                </linearGradient>
                <pattern id="dots" width="22" height="22" patternUnits="userSpaceOnUse">
                  <circle cx="3" cy="3" r="1.6" fill="#ffffff" fill-opacity="0.09"/>
                </pattern>
              </defs>
              <rect width="{width}" height="{height}" fill="url(#bg)"/>
              <rect x="{Frame}" y="{Frame}" width="{panel}" height="{panelHeight}" rx="{panelCorner}" fill="#000000" fill-opacity="0.8"/>
              <rect x="{Frame}" y="{Frame}" width="{panel}" height="{panelHeight}" rx="{panelCorner}" fill="url(#glow)"/>
              <rect x="{Frame}" y="{Frame}" width="{panel}" height="{panelHeight}" rx="{panelCorner}" fill="url(#dots)"/>
              {mark}
              {fan}
              <line x1="{Gutter}" y1="{ruleY}" x2="{ruleEnd}" y2="{ruleY}" stroke="#ffffff" stroke-opacity="0.45" stroke-width="2" stroke-linecap="round"/>
              <line x1="{ruleStart}" y1="{ruleY}" x2="{ruleRight}" y2="{ruleY}" stroke="#ffffff" stroke-opacity="0.45" stroke-width="2" stroke-linecap="round"/>
              {letters}
              <circle cx="{centre}" cy="{discY}" r="{discR}" fill="#ffffff" fill-opacity="0.16" stroke="#ffffff" stroke-opacity="0.25" stroke-width="2"/>
              <g transform="translate({iconX:0.#} {iconY:0.#}) scale({iconScale})" fill="none" stroke="#ffffff" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round">{style.Icon}</g>
              <text x="{titleX}" y="{titleY}" font-family="{FontFamilies}" font-weight="700" font-size="{titleSize}" fill="#ffffff">{title}</text>
              <text x="{taglineX}" y="{taglineY}" font-family="{FontFamilies}" font-weight="400" font-size="{taglineSize}" fill="#ffffff" fill-opacity="0.85">{tagline}</text>
            </svg>
            """;
    }

    /// <summary>
    /// The posters fanned out above <paramref name="bottom"/>: the first upright in the middle and
    /// in front, the next two tilted out to its sides, and all fading into the panel's dark.
    /// </summary>
    private static string Fan(IReadOnlyList<string> posters, int centre, int bottom, bool large)
    {
        if (posters.Count == 0)
            return "";

        // Drawn back to front: the sides, then the middle one over them.
        var places = new List<(string Uri, int X, int Y, int W, int H, int Tilt)>();
        var (sideWidth, sideHeight, sideOffset) = large ? (200, 290, 165) : (120, 168, 128);
        var (mainWidth, mainHeight) = large ? (240, 360) : (132, 192);
        var fade = large ? 220 : 150;
        for (var i = 1; i < posters.Count; i++)
        {
            var side = i == 1 ? -1 : 1;
            var x = centre + side * sideOffset - sideWidth / 2;
            places.Add((posters[i], x, bottom - sideHeight, sideWidth, sideHeight, side * 9));
        }
        places.Add(
            (posters[0], centre - mainWidth / 2, bottom - mainHeight, mainWidth, mainHeight, 0)
        );

        var fan = new StringBuilder("""<g clip-path="url(#panel)">""");
        foreach (var (uri, x, y, w, h, tilt) in places)
        {
            var cx = x + w / 2;
            var cy = y + h / 2;
            fan.Append(
                $"""<g transform="rotate({tilt} {cx} {cy})"><rect x="{x + 6}" y="{y + 8}" width="{w}" height="{h}" fill="#000000" fill-opacity="0.45"/><image x="{x}" y="{y}" width="{w}" height="{h}" preserveAspectRatio="xMidYMin slice" href="{uri}"/><rect x="{x}" y="{y}" width="{w}" height="{h}" fill="none" stroke="#ffffff" stroke-opacity="0.25" stroke-width="2"/></g>"""
            );
        }
        // The fade starts above the tallest poster's middle and is darkest at the panel's edge.
        fan.Append(
            $"""<rect x="0" y="{bottom - fade}" width="{centre * 2}" height="{fade}" fill="url(#fade)"/></g>"""
        );
        return fan.ToString();
    }

    /// <summary>
    /// The size a line is drawn at, as large as fits between the card's gutters (a long line gets
    /// smaller letters, not a cut), and where it starts to stand centred.
    /// </summary>
    /// <remarks>
    /// Centred by hand: Svg.Skia measures text centred with text-anchor as if it started at its
    /// anchor, and Jellyfin sizes an SVG by what it draws, so a centred line would make the card
    /// wider than it is.
    /// </remarks>
    private static (int X, int Size) Fit(string text, int width, int max, int min, bool bold)
    {
        var size = Math.Clamp(
            (int)(max * (width - 2 * Gutter) / Math.Max(Measure(text, max, bold), 1)),
            min,
            max
        );
        return ((int)Math.Max(Gutter, (width - Measure(text, size, bold)) / 2), size);
    }

    /// <summary>
    /// How wide a line is drawn at <paramref name="size"/>, by the typeface Svg.Skia picks for
    /// <see cref="FontFamilies"/>. A letter taken as 0.68 of its size wide (0.58 when not bold)
    /// when Jellyfin's SkiaSharp cannot be used.
    /// </summary>
    private static float Measure(string text, float size, bool bold)
    {
        try
        {
            return MeasureWithSkia(text, size, bold);
        }
        catch (Exception ex)
            when (ex is TypeLoadException or FileNotFoundException or DllNotFoundException)
        {
            return text.Length * size * (bold ? 0.68f : 0.58f);
        }
    }

    // Kept apart so a SkiaSharp that cannot be loaded fails the call, not the caller's compile.
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining
    )]
    private static float MeasureWithSkia(string text, float size, bool bold)
    {
        var style = bold ? SkiaSharp.SKFontStyle.Bold : SkiaSharp.SKFontStyle.Normal;
        var typeface =
            FontFamilies
                .Split(',', StringSplitOptions.TrimEntries)
                .Select(f => SkiaSharp.SKFontManager.Default.MatchFamily(f, style))
                .FirstOrDefault(t => t is not null)
            ?? SkiaSharp.SKTypeface.Default;
        using var font = new SkiaSharp.SKFont(typeface, size);
        return font.MeasureText(text);
    }

    /// <summary>How much of a colour's saturation a card keeps: its colours are muted.</summary>
    private const double Saturation = 0.7;

    /// <summary><paramref name="hex"/> (#rrggbb) with its saturation scaled by <see cref="Saturation"/>.</summary>
    private static string Muted(string hex)
    {
        var rgb = Convert.FromHexString(hex[1..]).Select(c => c / 255d).ToArray();
        var (max, min) = (rgb.Max(), rgb.Min());
        var l = (max + min) / 2;
        var d = max - min;
        if (d == 0)
            return hex;

        var s = d / (1 - Math.Abs(2 * l - 1));
        var h =
            max == rgb[0] ? (rgb[1] - rgb[2]) / d % 6
            : max == rgb[1] ? (rgb[2] - rgb[0]) / d + 2
            : (rgb[0] - rgb[1]) / d + 4;

        // Back from hue, saturation and lightness, the saturation scaled.
        var c = (1 - Math.Abs(2 * l - 1)) * s * Saturation;
        var x = c * (1 - Math.Abs(((h % 2) + 2) % 2 - 1));
        var m = l - c / 2;
        var (r, g, b) = ((int)Math.Floor((h + 6) % 6)) switch
        {
            0 => (c, x, 0d),
            1 => (x, c, 0d),
            2 => (0d, c, x),
            3 => (0d, x, c),
            4 => (x, 0d, c),
            _ => (c, 0d, x),
        };
        return $"#{(int)Math.Round((r + m) * 255):x2}{(int)Math.Round((g + m) * 255):x2}{(int)Math.Round((b + m) * 255):x2}";
    }

    private static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
}
