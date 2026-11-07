using Granary.Core.Api.Playback;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Playback;

// Why a title is transcoded on this PC, for the log: what the client told the server it plays, and what the server
// chose and why. The plan's reason is an open registry (docs/api-contract.md, 8.5); it is logged, never branched on.
public sealed partial class ServerPlayback
{
    private void LogReport(PlaybackClientReport report)
    {
        var capabilities = report.ClientCapabilities;
        bool mapsHdr = report.ClientPlaybackContext.Deliveries.TryGetValue(DeliveryClasses.OriginalHttp, out var original)
            && original.ValidatedClaims.Contains(DeliveryClaims.ClientManagedDynamicRangeV1);
        // The claim covers the files this start may get, so it speaks for this title only.
        _logger.LogInformation("This PC plays {Codecs} ({Hardware} in hardware); HDR originals of this title are {Hdr}",
            Listed(capabilities.CodecsVideo), Listed(capabilities.CodecsVideoHardware),
            mapsHdr ? "played directly" : "tone-mapped by the server");
    }

    private void LogDecision(PlaybackPlan plan)
    {
        var recipe = plan.EffectiveRecipe;
        _logger.LogInformation(
            "The server's reason: {Reason}; it sends {Codec} {Width}x{Height} {Range} (Dolby Vision: {DolbyVision}; warnings: {Warnings})",
            Named(plan.DecisionReason), Named(recipe.VideoCodec), recipe.Width, recipe.Height, Named(recipe.DynamicRange),
            Named(plan.Claims.Video.DolbyVisionReason), Listed([.. plan.DegradationWarnings.Select(w => w.Code)]));
    }

    private static string Named(string? value) => string.IsNullOrEmpty(value) ? "none" : value;

    private static string Listed(IReadOnlyList<string> values) => values.Count == 0 ? "none" : string.Join(", ", values);
}
