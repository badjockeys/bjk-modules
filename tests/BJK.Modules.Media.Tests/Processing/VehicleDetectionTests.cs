using BJK.Modules.Media.Infrastructure.Processing;
using Xunit;

namespace BJK.Modules.Media.Tests.Processing;

/// <summary>Whether a vehicle was seen, for holding posts with none (owner, 23 Oct 2026).</summary>
public sealed class VehicleDetectionTests
{
    [Theory]
    [InlineData(new[] { 0, 0 }, 0)]
    [InlineData(new[] { 0, 4 }, 4)]
    [InlineData(new[] { 2, -1 }, 2)]
    [InlineData(new[] { 0, -1 }, null)]
    [InlineData(new[] { -1 }, null)]
    [InlineData(new int[0], null)]
    public void A_video_counts_vehicles_over_its_segments(int[] segments, int? expected) =>
        // -1 stands for a segment where vehicles weren't looked for.
        Assert.Equal(expected, MediaProcessor.VehicleFramesOf(segments.Select(s => s < 0 ? (int?)null : s)));
}
