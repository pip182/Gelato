using Gelato.Channels;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Gelato.Filters;

/// <summary>
/// Lists a folder of the catalog channel in the addon's order, whatever order the client asks for.
/// </summary>
/// <remarks>
/// A catalog's order is what it says: most popular first, newest first. Clients sort a folder by
/// name unless told otherwise, which turns "Popular" into an alphabetical list. The channel puts
/// each title's place in its index number (<see cref="CatalogChannel"/>), and the listing is
/// sorted by it.
/// </remarks>
public sealed class CatalogChannelOrderFilter(CatalogChannel channel) : IAsyncActionFilter
{
    public Task OnActionExecutionAsync(ActionExecutingContext ctx, ActionExecutionDelegate next)
    {
        if (IsChannelListing(ctx))
        {
            ctx.ActionArguments["sortBy"] = new[] { ItemSortBy.IndexNumber };
            ctx.ActionArguments["sortOrder"] = new[] { SortOrder.Ascending };
        }

        return next();
    }

    private bool IsChannelListing(ActionExecutingContext ctx) =>
        ctx.GetActionName() switch
        {
            "GetChannelItems" => Argument(ctx, "channelId") is Guid id
                && id.Equals(channel.ChannelId),
            "GetItems" or "GetItemsByUserIdLegacy" => Argument(ctx, "parentId") is Guid parent
                && channel.IsChannelFolder(parent),
            _ => false,
        };

    private static object? Argument(ActionExecutingContext ctx, string name) =>
        ctx.ActionArguments.TryGetValue(name, out var value) ? value : null;
}
