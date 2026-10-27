using Granary.Core.Api.Admin;
using Granary.Core.ViewModels.Admin;

namespace Granary.Tests.Admin;

/// <summary>The dashboard's charts on the server's bucket grid, as the web's <c>playbackActivitySeries</c> and <c>timeseriesSeries</c>.</summary>
public sealed class AdminChartsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 30, 0, TimeSpan.Zero);

    [Fact]
    public void PlaybackColumns_FillQuietHours_AndCoverThePartialFirstOne()
    {
        var activity = new AdminDashboardPlaybackActivity
        {
            BucketSeconds = 3600,
            Buckets =
            [
                new(new DateTimeOffset(2026, 10, 27, 9, 0, 0, TimeSpan.Zero), 2, 1, 0),
                new(new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero), 0, 0, 3),
            ],
        };

        var columns = AdminCharts.PlaybackColumns(activity, 24, Now);

        // 12:30 yesterday falls in the 12:00 bucket, so 25 columns: 12:00 yesterday to 12:00 today.
        Assert.Equal(25, columns.Count);
        Assert.Equal(new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero), columns[0].Start);
        Assert.Equal(new AdminPlaybackColumn(new DateTimeOffset(2026, 10, 27, 9, 0, 0, TimeSpan.Zero), 2, 1, 0), columns[21]);
        Assert.Equal(3, columns[^1].Transcode);
        Assert.Equal(6, columns.Sum(c => c.Total));
    }

    [Fact]
    public void PlaybackColumns_FollowTheServersBucketWidth_AndDropBucketsOutsideTheWindow()
    {
        var activity = new AdminDashboardPlaybackActivity
        {
            BucketSeconds = 86_400,
            Buckets =
            [
                new(new DateTimeOffset(2026, 10, 25, 0, 0, 0, TimeSpan.Zero), 4, 0, 0),
                new(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), 9, 9, 9),
            ],
        };

        var columns = AdminCharts.PlaybackColumns(activity, 72, Now);

        Assert.Equal(4, columns.Count);
        Assert.Equal(4, columns[1].Direct);
        Assert.Equal(4, columns.Sum(c => c.Total));
    }

    [Fact]
    public void PlaybackColumns_WithoutAnAnswer_AreTheEmptyWindow()
    {
        var columns = AdminCharts.PlaybackColumns(null, 24, Now);

        Assert.Equal(25, columns.Count);
        Assert.All(columns, c => Assert.Equal(0, c.Total));
    }

    [Fact]
    public void Rate_BreaksTheLineWhereTheSamplerWroteNothing()
    {
        var from = new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
        var series = new AdminDashboardTimeseries
        {
            ResolutionSeconds = 60,
            From = from.AddSeconds(20),
            To = from.AddMinutes(4).AddSeconds(5),
            Points = [Point(from, 1500), Point(from.AddMinutes(1), 0), Point(from.AddMinutes(3), 12_000)],
        };

        var rate = AdminCharts.Rate(series, p => p.EgressKbps / 1000d);

        Assert.Equal(from, rate.From);
        Assert.Equal(TimeSpan.FromMinutes(1), rate.Step);
        Assert.Equal(new double?[] { 1.5, 0, null, 12, null }, rate.Values);
        Assert.Equal(12, rate.Peak);
        Assert.True(rate.HasSamples);
    }

    [Fact]
    public void Rate_OfAMalformedWindow_KeepsTheSamplesInOrder()
    {
        var at = new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
        var series = new AdminDashboardTimeseries { From = at, To = at.AddHours(-1), Points = [Point(at, 2000), Point(at.AddMinutes(1), 3000)] };

        Assert.Equal(new double?[] { 2, 3 }, AdminCharts.Rate(series, p => p.EgressKbps / 1000d).Values);
        Assert.False(AdminCharts.Rate(null, p => p.EgressKbps).HasSamples);
        Assert.Equal(0, AdminRateSeries.Empty.Peak);
    }

    [Fact]
    public void LatestFresh_IsUnknownOnceTheSamplerFellBehind()
    {
        var series = new AdminDashboardTimeseries { Points = [Point(Now.AddMinutes(-5), 100), Point(Now.AddSeconds(-90), 4000)] };

        Assert.Equal(4000, AdminCharts.LatestFresh(series, Now)!.EgressKbps);
        Assert.Null(AdminCharts.LatestFresh(series, Now.AddMinutes(1)));
        Assert.Null(AdminCharts.LatestFresh(new AdminDashboardTimeseries(), Now));
        Assert.Null(AdminCharts.LatestFresh(null, Now));
    }

    [Theory]
    [InlineData(133, new double[] { 0, 100, 200 })]
    [InlineData(12, new double[] { 0, 10, 20 })]
    [InlineData(2.5, new double[] { 0, 2, 4 })]
    [InlineData(0.37, new double[] { 0, 0.2, 0.4 })]
    [InlineData(0, new double[] { 0, 0.5, 1 })]
    public void NiceTicks_RoundToHumanNumbers(double max, double[] expected) => Assert.Equal(expected, AdminCharts.NiceTicks(max));

    private static AdminDashboardTimeseriesPoint Point(DateTimeOffset t, int egressKbps) => new(t, 1, 1, 0, 0, egressKbps, 0);
}
