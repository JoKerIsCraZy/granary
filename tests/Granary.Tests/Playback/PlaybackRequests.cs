using System.Text.Json;
using Granary.Core.Api.Playback;

namespace Granary.Tests.Playback;

/// <summary>Valid request bodies to start from, and their wire JSON.</summary>
internal static class PlaybackRequests
{
    public const string AttemptId = "0b9c7f3e-6d5a-4f8e-9c1b-2a3d4e5f6a7b";

    public static readonly PlaybackDeviceFacts Facts = new()
    {
        Platform = "windows",
        OsVersion = "10.0.26100",
        HardwareVideoCodecs = ["h264", "hevc"],
        SoftwareVideoCodecs = ["h264", "hevc", "av1", "vp9"],
    };

    public static readonly PlaybackClientReport Report = PlaybackReportBuilder.Build(Facts, "0.1.0", "dev");

    public static PlaybackStartRequest Start() => new()
    {
        InstallationId = PlaybackPayloads.InstallationId,
        ClientFeatures = Report.ClientFeatures,
        FileId = "42",
        ProfileId = "p1",
        PlaybackAttemptId = AttemptId,
        StartPosition = 12.5,
        AudioTrackIndex = 0,
        ClientCapabilities = Report.ClientCapabilities,
        ClientPlaybackContext = Report.ClientPlaybackContext,
    };

    public static PlaybackReplanRequest Replan() => new()
    {
        InstallationId = PlaybackPayloads.InstallationId,
        ClientFeatures = Report.ClientFeatures,
        Operation = ReplanOperations.FailureRecovery,
        PlaybackAttemptId = AttemptId,
        ReplanRequestId = "6f1e2d3c-4b5a-4968-8776-655443322110",
        FailedPlanId = "plan:478677870860e5e5108c18bff749b34b",
        PlanAttemptId = "1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d",
        PlanAttemptKey = "v3:f0144c47fa349e3e",
        AttemptedPlanKeys = ["v3:f0144c47fa349e3e"],
        AttemptCount = 1,
        PositionSeconds = 300,
        Failure = new PlaybackFailure { Classification = "decoder_init_failed", DecoderName = "hevc_d3d11va" },
        ClientCapabilities = Report.ClientCapabilities,
        ClientPlaybackContext = Report.ClientPlaybackContext,
    };

    public static PlaybackRouteEvent FirstFrame() => new()
    {
        InstallationId = PlaybackPayloads.InstallationId,
        EventId = "55555555-5555-4555-8555-555555555555",
        PlaybackAttemptId = AttemptId,
        SessionId = PlaybackPayloads.SessionId,
        PlanId = "plan:478677870860e5e5108c18bff749b34b",
        PlanAttemptId = "1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d",
        PlanAttemptKey = "v3:f0144c47fa349e3e",
        Event = RouteEvents.FirstFrame,
        Diagnostics = new Dictionary<string, string> { [RouteDiagnostics.FirstFrameMs] = "840" },
    };

    public static string Json(PlaybackStartRequest request) => JsonSerializer.Serialize(request, PlaybackJsonContext.Default.PlaybackStartRequest);

    public static string Json(PlaybackReplanRequest request) => JsonSerializer.Serialize(request, PlaybackJsonContext.Default.PlaybackReplanRequest);

    public static string Json(PlaybackRouteEvent routeEvent) => JsonSerializer.Serialize(routeEvent, PlaybackJsonContext.Default.PlaybackRouteEvent);

    public static string Json(PlaybackStopRequest request) => JsonSerializer.Serialize(request, PlaybackJsonContext.Default.PlaybackStopRequest);

    public static string Json(PlaybackProgressRequest request) => JsonSerializer.Serialize(request, PlaybackJsonContext.Default.PlaybackProgressRequest);
}
