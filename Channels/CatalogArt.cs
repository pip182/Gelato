using System.Security;
using System.Security.Cryptography;
using System.Text;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Gelato.Channels;

/// <summary>
/// The image of a catalog folder in <see cref="CatalogChannel"/>: a card with an icon for what
/// kind of list the catalog is (popular, top, by year, ...), its name and whether it holds movies
/// or shows. Drawn as an SVG, which Jellyfin renders itself, and kept in its cache folder.
/// </summary>
/// <remarks>
/// A card is named by a hash of its content, so a card that changes gets a path of its own:
/// Jellyfin keeps its resized copies of an image by path.
/// </remarks>
public sealed class CatalogArt(IApplicationPaths appPaths, ILogger<CatalogArt> log)
{
    private const int Width = 400;
    private const int Height = 600;

    /// <summary>
    /// The room kept clear around the card's contents. Its text starts at the left gutter: Jellyfin
    /// sizes an SVG by what it draws, and Svg.Skia measures centred text as if it started at its
    /// anchor, so a centred name would make the card wider than it is.
    /// </summary>
    private const int Gutter = 40;

    /// <summary>An icon as the inner markup of a 24x24, stroked SVG, and its card's colours.</summary>
    private sealed record Style(string Icon, string From, string To);

    // Icons after Lucide (ISC licence), drawn on a 24x24 grid.
    private static readonly Style Popular = new(
        """<path d="M8.5 14.5A2.5 2.5 0 0 0 11 12c0-1.38-.5-2-1-3-1.07-2.14-.22-4.05 2-6 .5 2.5 2 4.9 4 6.5 2 1.6 3 3.5 3 5.5a7 7 0 1 1-14 0c0-1.15.43-2.29 1-3a2.5 2.5 0 0 0 2.5 2.5z"/>""",
        "#7c1d0f",
        "#ea580c"
    );
    private static readonly Style Top = new(
        """<path d="M6 9H4.5a2.5 2.5 0 0 1 0-5H6"/><path d="M18 9h1.5a2.5 2.5 0 0 0 0-5H18"/><path d="M4 22h16"/><path d="M10 14.66V17c0 .55-.47.98-.97 1.21C7.85 18.75 7 20.24 7 22"/><path d="M14 14.66V17c0 .55.47.98.97 1.21C16.15 18.75 17 20.24 17 22"/><path d="M18 2H6v7a6 6 0 0 0 12 0V2Z"/>""",
        "#713f12",
        "#ca8a04"
    );
    private static readonly Style Trending = new(
        """<polyline points="22 7 13.5 15.5 8.5 10.5 2 17"/><polyline points="16 7 22 7 22 13"/>""",
        "#064e3b",
        "#059669"
    );
    private static readonly Style Year = new(
        """<rect x="3" y="4" width="18" height="18" rx="2"/><path d="M16 2v4"/><path d="M8 2v4"/><path d="M3 10h18"/>""",
        "#1e3a8a",
        "#2563eb"
    );
    private static readonly Style Language = new(
        """<circle cx="12" cy="12" r="10"/><path d="M12 2a14.5 14.5 0 0 0 0 20 14.5 14.5 0 0 0 0-20"/><path d="M2 12h20"/>""",
        "#134e4a",
        "#0d9488"
    );
    private static readonly Style Genre = new(
        """<path d="M12.59 2.59A2 2 0 0 0 11.17 2H4a2 2 0 0 0-2 2v7.17a2 2 0 0 0 .59 1.42l8.7 8.7a2.43 2.43 0 0 0 3.42 0l6.58-6.58a2.43 2.43 0 0 0 0-3.42z"/><circle cx="7.5" cy="7.5" r="1"/>""",
        "#4c1d95",
        "#7c3aed"
    );
    private static readonly Style Rated = new(
        """<polygon points="12 2 15.09 8.26 22 9.27 17 14.14 18.18 21.02 12 17.77 5.82 21.02 7 14.14 2 9.27 8.91 8.26 12 2"/>""",
        "#78350f",
        "#d97706"
    );
    private static readonly Style New = new(
        """<path d="M12 3l1.9 5.8L20 11l-6.1 2.2L12 19l-1.9-5.8L4 11l6.1-2.2z"/><path d="M19 3v4"/><path d="M17 5h4"/>""",
        "#831843",
        "#db2777"
    );
    private static readonly Style Movies = new(
        """<rect x="3" y="3" width="18" height="18" rx="2"/><path d="M7 3v18"/><path d="M3 7.5h4"/><path d="M3 12h18"/><path d="M3 16.5h4"/><path d="M17 3v18"/><path d="M17 7.5h4"/><path d="M17 16.5h4"/>""",
        "#1f2937",
        "#475569"
    );
    private static readonly Style Shows = new(
        """<rect x="2" y="7" width="20" height="15" rx="2"/><polyline points="17 2 12 7 7 2"/>""",
        "#1f2937",
        "#475569"
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

    /// <summary>The card's path, drawn first when there is none. Null when it cannot be written.</summary>
    public string? Get(string type, string id, string name, bool movies)
    {
        var svg = Draw(name, movies, StyleOf(id, name, movies));
        var path = Path.Combine(appPaths.CachePath, "gelato", "catalog-art", $"{Hash(svg)}.svg");
        if (File.Exists(path))
            return path;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // Written beside its place and moved there, so a card half written is never used.
            var partial = path + ".partial";
            File.WriteAllText(partial, svg);
            File.Move(partial, path, overwrite: true);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(
                "Gelato: no image for catalog {Type}/{Id}: {Error}",
                type,
                id,
                ex.Message
            );
            return null;
        }
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

    private static string Draw(string name, bool movies, Style style)
    {
        var title = SecurityElement.Escape(name.Trim());
        // As large as fits between the card's gutters, a bold letter taken as 0.68 of its size
        // wide: a long name gets smaller letters, not a cut.
        var size = Math.Clamp(
            (int)((Width - 2 * Gutter) / (Math.Max(name.Trim().Length, 1) * 0.68)),
            20,
            44
        );

        return $"""
            <svg xmlns="http://www.w3.org/2000/svg" width="{Width}" height="{Height}" viewBox="0 0 {Width} {Height}">
              <defs>
                <linearGradient id="bg" x1="0" y1="0" x2="1" y2="1">
                  <stop offset="0" stop-color="{style.To}"/>
                  <stop offset="1" stop-color="{style.From}"/>
                </linearGradient>
              </defs>
              <rect width="{Width}" height="{Height}" fill="url(#bg)"/>
              <g transform="translate(170 150) scale(9)" fill="none" stroke="#ffffff" stroke-opacity="0.07" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round">{style.Icon}</g>
              <circle cx="{Gutter + 56}" cy="{Gutter
                + 56}" r="56" fill="#ffffff" fill-opacity="0.14"/>
              <g transform="translate({Gutter + 20} {Gutter
                + 20}) scale(3)" fill="none" stroke="#ffffff" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round">{style.Icon}</g>
              <text x="{Gutter}" y="{Height
                - Gutter
                - size
                - 16}" font-family="Noto Sans, DejaVu Sans, sans-serif" font-weight="600" font-size="18" letter-spacing="5" fill="#ffffff" fill-opacity="0.75">{(
                movies ? "MOVIES" : "SHOWS"
            )}</text>
              <text x="{Gutter}" y="{Height
                - Gutter
                - 6}" font-family="Noto Sans, DejaVu Sans, sans-serif" font-weight="700" font-size="{size}" fill="#ffffff">{title}</text>
            </svg>
            """;
    }

    private static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
}
