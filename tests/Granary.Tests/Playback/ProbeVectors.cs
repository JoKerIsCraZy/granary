using Granary.Core.Playback;

namespace Granary.Tests.Playback;

/// <summary>A probe_cases entry: arrivals [t, bytes] after the first byte, and when the body ended cleanly (null: it ran on).</summary>
public sealed record ProbeCase(SpeedTestMode Mode, long? TargetKbps, long[][] Chunks, long? End, SpeedTestResult? Expect, long? Stop);

/// <summary>
/// <c>probe_cases</c> of the server's buffering-quality vectors (docs/design/schemas/buffering-quality/v1/vectors.json),
/// the oracle every client replays its speed test arithmetic against. Generated from the file; do not edit by hand.
/// </summary>
public static class ProbeVectors
{
    public static TheoryData<string, ProbeCase> Cases => new()
    {
        { "quick_stops_once_a_window_beats_the_target", new ProbeCase(SpeedTestMode.Quick, 115000, [[0, 5187500], [750, 27562500], [1250, 29687500], [1750, 30250000], [2250, 30250000], [2750, 30250000]], null, new SpeedTestResult(115000, true, 27562500, 500), 1000) },
        { "quick_ramp_window_never_counts", new ProbeCase(SpeedTestMode.Quick, 115000, [[0, 12500000], [750, 6250000], [1250, 6250000], [1750, 6250000], [2250, 6250000], [2750, 6250000]], null, new SpeedTestResult(100000, false, 12500000, 1000), 3000) },
        { "quick_target_on_a_later_window", new ProbeCase(SpeedTestMode.Quick, 115000, [[0, 3125000], [750, 6875000], [1250, 7500000], [1750, 7500000], [2250, 7500000], [2750, 7500000]], null, new SpeedTestResult(115000, true, 7500000, 500), 1500) },
        { "quick_settles_below_the_target", new ProbeCase(SpeedTestMode.Quick, 115000, [[0, 1250000], [750, 3000000], [1250, 3000000], [1750, 3000000], [2250, 3000000], [2750, 3000000]], null, new SpeedTestResult(48000, false, 6000000, 1000), 3000) },
        { "quick_without_a_target_runs_to_its_limit", new ProbeCase(SpeedTestMode.Quick, null, [[0, 9375000], [750, 18750000], [1250, 18750000], [1750, 18750000], [2250, 18750000], [2750, 18750000]], null, new SpeedTestResult(300000, false, 37500000, 1000), 3000) },
        { "quick_silence_counts", new ProbeCase(SpeedTestMode.Quick, 115000, [[0, 2500000], [750, 2500000], [1250, 2500000], [1750, 2500000], [2250, 2500000]], null, new SpeedTestResult(20000, false, 2500000, 1000), 3000) },
        { "quick_transfer_ends_early", new ProbeCase(SpeedTestMode.Quick, null, [[0, 5000000], [750, 5000000], [1100, 1000000]], 1300, new SpeedTestResult(80000, false, 5000000, 500), 1300) },
        { "too_short_to_measure", new ProbeCase(SpeedTestMode.Quick, 115000, [[0, 2000000]], 450, null, null) },
        { "accurate_reports_the_final_three_seconds", new ProbeCase(SpeedTestMode.Accurate, 115000, [[0, 5187500], [750, 27562500], [1250, 29687500], [1750, 30250000], [2250, 30000000], [2750, 30000000], [3250, 30000000], [3750, 30000000], [4250, 30000000], [4750, 30000000], [5250, 30000000], [5750, 30000000], [6250, 30000000], [6750, 30000000], [7250, 30000000], [7750, 30625000], [8250, 29375000], [8750, 30000000], [9250, 30625000], [9750, 29375000]], null, new SpeedTestResult(480000, false, 180000000, 3000), 10000) },
        { "accurate_fast_link_is_a_lower_bound", new ProbeCase(SpeedTestMode.Accurate, null, [[0, 50000000], [750, 125000000], [1250, 125000000], [1750, 125000000], [2250, 125000000], [2750, 125000000], [3250, 125000000], [3750, 125000000], [4100, 75000000]], 4300, new SpeedTestResult(1000000, true, 750000000, 3000), 4300) },
        { "quick_whole_transfer_before_window_1_reaches_the_target", new ProbeCase(SpeedTestMode.Quick, 115000, [[0, 10000000], [50, 7500000], [100, 7500000], [150, 7500000], [200, 7500000], [250, 7500000], [300, 7500000], [350, 7500000], [400, 7500000], [450, 7500000], [500, 7500000], [550, 7500000], [600, 7500000]], 600, new SpeedTestResult(115000, true, 100000000, 600), 600) },
        { "quick_whole_transfer_without_a_target_is_a_lower_bound", new ProbeCase(SpeedTestMode.Quick, null, [[0, 10000000], [100, 10000000], [200, 10000000], [300, 10000000], [400, 10000000], [500, 10000000], [600, 10000000], [700, 10000000], [800, 10000000], [900, 10000000]], 900, new SpeedTestResult(888888, true, 100000000, 900), 900) },
        { "accurate_whole_transfer_on_a_ten_gigabit_link", new ProbeCase(SpeedTestMode.Accurate, null, [[0, 62500000], [50, 62500000], [100, 62500000], [150, 62500000], [200, 62500000], [250, 62500000], [300, 62500000], [350, 62500000], [400, 62500000], [450, 62500000], [500, 62500000], [550, 62500000], [600, 62500000], [650, 62500000], [700, 62500000], [750, 62500000]], 800, new SpeedTestResult(1000000, true, 1000000000, 800), 800) },
    };
}
