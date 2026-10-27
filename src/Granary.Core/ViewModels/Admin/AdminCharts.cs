using Granary.Core.Api.Admin;

namespace Granary.Core.ViewModels.Admin;

/// <summary>A column of the playback chart: the sessions started in its bucket, by how they play.</summary>
/// <param name="DirectStream">The server's remux starts, in the operator's word, as the web's chart calls them.</param>
public sealed record AdminPlaybackColumn(DateTimeOffset Start, int Direct, int DirectStream, int Transcode)
{
    public int Total => Direct + DirectStream + Transcode;
}

/// <summary>A sampled rate over a window, one value a step; null where the sampler wrote nothing, so the line breaks there.</summary>
public sealed record AdminRateSeries(DateTimeOffset From, TimeSpan Step, IReadOnlyList<double?> Values)
{
    public static readonly AdminRateSeries Empty = new(default, TimeSpan.FromMinutes(1), []);

    public double Peak => Values.Count == 0 ? 0 : Values.Max(value => value ?? 0);

    public bool HasSamples => Values.Any(value => value is not null);
}

/// <summary>
/// The dashboard's charts shaped as the web shapes them (<c>playbackActivitySeries.ts</c>, <c>timeseriesSeries.ts</c>):
/// windows filled in on the server's own bucket grid, so quiet buckets keep their place on the axis.
/// </summary>
public static class AdminCharts
{
    /// <summary>The sampler writes a row a minute; an older last sample means it stopped, and "now" is unknown.</summary>
    public static readonly TimeSpan SampleFreshFor = TimeSpan.FromMinutes(2);

    private const int MaxBuckets = 10_000;
    private const long HourMs = 3_600_000;
    private const int DefaultBucketSeconds = 3600;
    private const int DefaultResolutionSeconds = 60;

    /// <summary>
    /// The columns of the last <paramref name="hours"/>, oldest first, quiet buckets as zero. Both edges sit on the
    /// bucket grid, so the first column is the one that holds <c>now - hours</c>, partial or not.
    /// </summary>
    public static IReadOnlyList<AdminPlaybackColumn> PlaybackColumns(AdminDashboardPlaybackActivity? activity, int hours, DateTimeOffset now)
    {
        long step = (activity is { BucketSeconds: > 0 } ? activity.BucketSeconds : DefaultBucketSeconds) * 1000L;
        long nowMs = now.ToUnixTimeMilliseconds();
        long to = FloorTo(nowMs, step);
        long from = Math.Min(to, FloorTo(nowMs - Math.Max(0, hours) * HourMs, step));
        int count = (int)Math.Min((to - from) / step + 1, MaxBuckets);
        var columns = new AdminPlaybackColumn[count];
        for (int i = 0; i < count; i++)
        {
            columns[i] = new AdminPlaybackColumn(DateTimeOffset.FromUnixTimeMilliseconds(from + i * step), 0, 0, 0);
        }
        foreach (var bucket in activity?.Buckets ?? [])
        {
            long t = FloorTo(bucket.Hour.ToUnixTimeMilliseconds(), step);
            if (t >= from && (t - from) / step < count)
            {
                columns[(t - from) / step] = columns[(t - from) / step] with
                {
                    Direct = bucket.Direct,
                    DirectStream = bucket.Remux,
                    Transcode = bucket.Transcode,
                };
            }
        }
        return columns;
    }

    /// <summary>
    /// A sampled value over the series' window in its resolution, in megabits a second. Minutes the sampler never wrote
    /// (a restart) are null rather than zero: a gap breaks the line, a quiet minute draws a real zero.
    /// </summary>
    public static AdminRateSeries Rate(AdminDashboardTimeseries? series, Func<AdminDashboardTimeseriesPoint, double> select)
    {
        ArgumentNullException.ThrowIfNull(select);
        if (series is null)
        {
            return AdminRateSeries.Empty;
        }
        int resolution = series.ResolutionSeconds > 0 ? series.ResolutionSeconds : DefaultResolutionSeconds;
        var step = TimeSpan.FromSeconds(resolution);
        if (series.To < series.From)
        {
            // A malformed window: whatever samples came, in order.
            return new AdminRateSeries(series.Points.FirstOrDefault()?.T ?? default, step, [.. series.Points.Select(p => (double?)select(p))]);
        }
        long stepMs = resolution * 1000L;
        long from = FloorTo(series.From.ToUnixTimeMilliseconds(), stepMs);
        long to = FloorTo(series.To.ToUnixTimeMilliseconds(), stepMs);
        int count = (int)Math.Min((to - from) / stepMs + 1, MaxBuckets);
        var values = new double?[count];
        foreach (var point in series.Points)
        {
            long t = point.T.ToUnixTimeMilliseconds();
            if (t >= from && (t - from) / stepMs < count)
            {
                double value = select(point);
                values[(t - from) / stepMs] = double.IsFinite(value) ? value : null;
            }
        }
        return new AdminRateSeries(DateTimeOffset.FromUnixTimeMilliseconds(from), step, values);
    }

    /// <summary>The last sample while it still says "now" (<see cref="SampleFreshFor"/>); null once the sampler fell behind.</summary>
    public static AdminDashboardTimeseriesPoint? LatestFresh(AdminDashboardTimeseries? series, DateTimeOffset now) =>
        series?.Points.Count > 0 && series.Points[^1] is var last && now - last.T <= SampleFreshFor ? last : null;

    /// <summary>
    /// The line chart's axis from zero: about <paramref name="count"/> ticks on human numbers (1, 2 or 5 times a power of
    /// ten) up to the first at or above <paramref name="max"/>, as the web's <c>niceTicks</c>. A flat zero reads up to 1.
    /// </summary>
    public static IReadOnlyList<double> NiceTicks(double max, int count = 3)
    {
        double hi = double.IsFinite(max) && max > 0 ? max : 1;
        int target = Math.Max(2, count);
        double step = NiceNumber(NiceNumber(hi, roundToNearest: false) / (target - 1), roundToNearest: true);
        int decimals = Math.Clamp(-(int)Math.Floor(Math.Log10(step)), 0, 10);
        double end = Math.Ceiling(hi / step) * step;
        var ticks = new List<double>();
        for (int i = 0; i <= 1000; i++)
        {
            double value = Math.Round(i * step, decimals, MidpointRounding.AwayFromZero);
            ticks.Add(value);
            if (value >= end)
            {
                break;
            }
        }
        return ticks;
    }

    private static double NiceNumber(double range, bool roundToNearest)
    {
        double exponent = Math.Floor(Math.Log10(range));
        double fraction = range / Math.Pow(10, exponent);
        double nice = roundToNearest
            ? fraction < 1.5 ? 1 : fraction < 3 ? 2 : fraction < 7 ? 5 : 10
            : fraction <= 1 ? 1 : fraction <= 2 ? 2 : fraction <= 5 ? 5 : 10;
        return nice * Math.Pow(10, exponent);
    }

    private static long FloorTo(long ms, long step) => (long)Math.Floor(ms / (double)step) * step;
}
