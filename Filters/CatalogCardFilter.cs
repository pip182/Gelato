using Gelato.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Gelato.Filters;

/// <summary>
/// Answers a request for a catalog folder's card that asks for a poster-shaped image with the
/// card's poster-shaped twin (<see cref="CatalogArt.TallOf"/>).
/// </summary>
/// <remarks>
/// Clients lay a folder out by the shape its image has, and fit two square cards to a row; the
/// Roku app shows every folder as a poster, asks for one 295 by 440, and cut the square card at
/// its sides. What the request asks for tells them apart, whatever the client. The twin is kept
/// as the folder's box image, which no client shows of its own accord, and the request is turned
/// to it, so Jellyfin sizes and keeps it as it does any image.
/// </remarks>
public sealed class CatalogCardFilter(ILibraryManager libraryManager, CatalogChannel channel)
    : IAsyncResourceFilter
{
    /// <summary>How much taller than wide an image must be asked for to be taken for a poster.</summary>
    private const double PosterShape = 1.2;

    public async Task OnResourceExecutionAsync(
        ResourceExecutingContext ctx,
        ResourceExecutionDelegate next
    )
    {
        if (
            ctx.ActionDescriptor
                is ControllerActionDescriptor
                {
                    ActionName: "GetItemImage" or "GetItemImageByIndex" or "GetItemImage2"
                }
            && AsksForPoster(ctx)
            && ctx.RouteData.Values.TryGetValue("imageType", out var type)
            && string.Equals(type?.ToString(), "Primary", StringComparison.OrdinalIgnoreCase)
            && Guid.TryParse(ctx.RouteData.Values["itemId"]?.ToString(), out var id)
            && libraryManager.GetItemById(id) is Folder folder
            && folder.ChannelId.Equals(channel.ChannelId)
            && CatalogArt.TallOf(folder.GetImageInfo(ImageType.Primary, 0)?.Path) is { } tall
        )
        {
            // The card changes with its catalog's first posters, and its twin with it.
            if (folder.GetImageInfo(ImageType.Box, 0)?.Path != tall)
            {
                folder.SetImagePath(ImageType.Box, tall);
                await folder
                    .UpdateToRepositoryAsync(
                        ItemUpdateType.ImageUpdate,
                        ctx.HttpContext.RequestAborted
                    )
                    .ConfigureAwait(false);
            }

            ctx.RouteData.Values["imageType"] = nameof(ImageType.Box);
        }

        await next();
    }

    /// <summary>
    /// Whether the request asks for an image clearly taller than wide, by the largest or the
    /// filled size it names, in its path (GetItemImage2) or its query.
    /// </summary>
    private static bool AsksForPoster(ResourceExecutingContext ctx)
    {
        foreach (var (h, w) in new[] { ("maxHeight", "maxWidth"), ("fillHeight", "fillWidth") })
            if (Size(ctx, h) is { } height && Size(ctx, w) is { } width && width > 0)
                return height > width * PosterShape;

        return false;
    }

    private static int? Size(ResourceExecutingContext ctx, string name) =>
        int.TryParse(
            ctx.RouteData.Values.TryGetValue(name, out var routed)
                ? routed?.ToString()
                : ctx.HttpContext.Request.Query[name].ToString(),
            out var size
        )
            ? size
            : null;
}
