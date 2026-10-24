using System.Diagnostics;

namespace Granary.MpvSpike;

/// <summary>
/// Scripted run: wait for playback, seek, pause, resize, fullscreen, optional subtitle and token
/// rotation, with metrics and screenshots after each step. Results go to the spike log.
/// </summary>
internal sealed class SelfTest(MainWindow window, SpikeOptions options)
{
    private static readonly string[] MetricProperties =
    [
        "current-vo", "current-gpu-context", "hwdec-current", "video-codec",
        "video-params/pixelformat", "video-params/w", "video-params/h", "video-params/primaries",
        "video-params/gamma", "video-params/sig-peak", "video-target-params/gamma",
        "video-target-params/primaries", "container-fps", "estimated-vf-fps", "display-fps",
        "frame-drop-count", "decoder-frame-drop-count", "vo-delayed-frame-count", "avsync",
        "track-list/count", "chapter-list/count", "chapter", "demuxer-via-network", "time-pos",
        "track-list/0/type", "track-list/0/ff-index", "track-list/0/src-id",
        "track-list/1/type", "track-list/1/ff-index", "track-list/1/src-id", "track-list/1/lang",
        "track-list/2/type", "track-list/2/ff-index", "track-list/2/src-id", "track-list/2/lang",
        "track-list/3/type", "track-list/3/ff-index", "track-list/3/src-id", "track-list/3/lang", "track-list/3/codec",
        "chapter-list/1/title", "chapter-list/1/time",
    ];

    public async Task RunAsync()
    {
        var player = window.Player!;
        var clock = Stopwatch.StartNew();
        try
        {
            double? started = await WaitForAsync(() => player.GetDouble("time-pos") is > 1.0, 30);
            SpikeLog.Write("selftest", started is null ? "FAIL playback did not start within 30 s" : $"playing after {clock.ElapsedMilliseconds} ms");
            LogMetrics("start");
            await ShotAsync("01-windowed");

            player.Command("seek", "12", "absolute");
            await Task.Delay(1500);
            SpikeLog.Write("selftest", $"after seek to 12: time-pos={player.GetDouble("time-pos"):F2}");
            await ShotAsync("02-after-seek");

            player.SetProperty("pause", "yes");
            await Task.Delay(800);
            await ShotAsync("03-paused");
            player.SetProperty("pause", "no");

            long dropsBefore = player.GetInt64("frame-drop-count") ?? 0;
            foreach (var (w, h) in new[] { (1280, 720), (820, 460), (1600, 900) })
            {
                window.ResizeTo(w, h);
                await Task.Delay(1200);
                await ShotAsync($"04-resized-{w}x{h}");
            }
            SpikeLog.Write("selftest", $"frame drops during resizing: {(player.GetInt64("frame-drop-count") ?? 0) - dropsBefore}");

            window.SetFullscreen(true);
            await Task.Delay(2500);
            LogMetrics("fullscreen");
            await ShotAsync("05-fullscreen");
            window.SetFullscreen(false);
            await Task.Delay(1500);
            await ShotAsync("06-restored");

            if (options.SubtitleUrl is not null)
            {
                await SubtitleAsync(player);
            }
            if (options.RotateTokenFile is not null)
            {
                await RotateTokenAsync(player);
            }
            LogMetrics("end");
            SpikeLog.Write("selftest", $"DONE in {clock.ElapsedMilliseconds} ms");
        }
        catch (Exception ex)
        {
            SpikeLog.Write("selftest", $"FAIL exception: {ex}");
        }
        finally
        {
            window.DispatcherQueue.TryEnqueue(window.Close);
        }
    }

    private async Task SubtitleAsync(MpvPlayer player)
    {
        int rc = player.Command("sub-add", options.SubtitleUrl!, "select", "external-vtt");
        SpikeLog.Write("selftest", $"sub-add rc={rc}");
        player.Command("seek", "3", "absolute");
        await Task.Delay(1500);
        SpikeLog.Write("selftest", $"subtitle track: sid={player.GetString("sid")} codec={player.GetString("current-tracks/sub/codec")} external={player.GetString("current-tracks/sub/external")} text='{player.GetString("sub-text")}'");
        await ShotAsync("07-external-subtitle");
    }

    // Simulates an access-token refresh during playback: the new header is set, the old token is
    // revoked on the test server, and we check whether playback survives with and without a reload.
    private async Task RotateTokenAsync(MpvPlayer player)
    {
        string newHeader = "Authorization: Bearer token-b";
        int rc = player.SetProperty("http-header-fields", newHeader);
        SpikeLog.Write("selftest", $"rotate: set http-header-fields (token-b) rc={rc}");
        await File.WriteAllTextAsync(options.RotateTokenFile!, "token-b\n");
        SpikeLog.Write("selftest", "rotate: server now accepts only token-b");

        player.Command("seek", "20", "absolute");
        await Task.Delay(3000);
        double? posAfterSeek = player.GetDouble("time-pos");
        SpikeLog.Write("selftest", $"rotate: after seek without reload time-pos={posAfterSeek:F2} idle={player.GetString("idle-active")} eof={player.GetString("eof-reached")}");

        double resumeAt = player.GetDouble("time-pos") ?? 20;
        var clock = Stopwatch.StartNew();
        rc = player.Command("loadfile", options.Source!, "replace", "-1", $"start={resumeAt.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        double? resumed = await WaitForAsync(() => player.GetDouble("time-pos") is double p && Math.Abs(p - resumeAt) < 3 && player.GetString("core-idle") == "no", 15);
        SpikeLog.Write("selftest", $"rotate: reload with token-b rc={rc} resumed={(resumed is not null)} after {clock.ElapsedMilliseconds} ms at {player.GetDouble("time-pos"):F2}");
        await ShotAsync("08-after-token-rotation");
    }

    private void LogMetrics(string label)
    {
        var player = window.Player!;
        foreach (string name in MetricProperties)
        {
            SpikeLog.Write("metric", $"{label} {name}={player.GetString(name) ?? "<unavailable>"}");
        }
        SpikeLog.Write("metric", $"{label} surface={window.SurfaceDescription}");
    }

    private Task ShotAsync(string name) =>
        window.CaptureAsync(Path.Combine(options.ShotsDir, $"{window.ModeTag}-{name}.png"));

    private static async Task<double?> WaitForAsync(Func<bool> condition, int timeoutSeconds)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed.TotalSeconds < timeoutSeconds)
        {
            if (condition())
            {
                return clock.Elapsed.TotalSeconds;
            }
            await Task.Delay(100);
        }
        return null;
    }
}
