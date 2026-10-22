using MediaBrowser.Model.Dlna;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Gelato.Filters;

/// <summary>
/// Loosens the Roku app's H.264 limits in its playback info request, so a stream it cannot play
/// directly is remuxed rather than re-encoded, and re-encoded well when it has to be.
/// </summary>
/// <remarks>
/// The Roku app caps H.264 at 10 Mbps, and Jellyfin applies that cap to the whole transcoding
/// request, HEVC included: a 25 Mbps 4K HEVC stream the Roku decodes itself is then re-encoded to
/// 10 Mbps 1080p H.264 because only its audio needed converting. Raising the H.264 cap to the
/// app's own HEVC cap lets the video be copied. The app also lists the H.264 profiles baseline
/// first, and Jellyfin encodes the first one it is given; high is put first.
/// </remarks>
public sealed class RokuProfileFilter : IAsyncActionFilter
{
    private const string RokuProfileName = "Official Roku Client";

    public Task OnActionExecutionAsync(ActionExecutingContext ctx, ActionExecutionDelegate next)
    {
        if (
            ctx.GetActionName() == "GetPostedPlaybackInfo"
            && FindProfile(ctx) is { Name: RokuProfileName } profile
        )
        {
            Loosen(profile);
        }

        return next();
    }

    private static DeviceProfile? FindProfile(ActionExecutingContext ctx) =>
        ctx
            .ActionArguments.Values.Select(v =>
                v?.GetType().GetProperty("DeviceProfile")?.GetValue(v) as DeviceProfile
            )
            .FirstOrDefault(p => p is not null);

    private static void Loosen(DeviceProfile profile)
    {
        var video = profile.CodecProfiles.Where(c => c.Type == CodecType.Video).ToArray();
        var hevcCap = video
            .Where(c => c.Codec == "hevc")
            .SelectMany(c => c.Conditions)
            .Where(c => c.Property == ProfileConditionValue.VideoBitrate)
            .Select(c => long.TryParse(c.Value, out var v) ? v : 0)
            .DefaultIfEmpty(0)
            .Max();

        foreach (var condition in video.Where(c => c.Codec == "h264").SelectMany(c => c.Conditions))
        {
            if (
                condition.Property == ProfileConditionValue.VideoBitrate
                && hevcCap > 0
                && long.TryParse(condition.Value, out var cap)
                && cap < hevcCap
            )
            {
                condition.Value = hevcCap.ToString();
            }
            else if (condition.Property == ProfileConditionValue.VideoProfile)
            {
                var profiles = condition.Value.Split('|');
                condition.Value = string.Join(
                    '|',
                    profiles.OrderBy(p =>
                        p.Equals("high", StringComparison.OrdinalIgnoreCase) ? 0 : 1
                    )
                );
            }
        }
    }
}
