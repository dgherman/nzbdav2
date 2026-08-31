using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NzbWebDAV.Models;
using NzbWebDAV.Utils;
using Xunit;

namespace NzbWebDAV.Tests;

/// <summary>
/// Non-blocking follow-up from cross-review of issue #28: <see cref="InterpolationSearch"/> now
/// caps itself at 64 probe rounds (see <c>InterpolationSearch.MaxProbeRounds</c>) so a pathological
/// segment-size table can't fan a single seek out into unbounded HEAD-equivalent fetches. This pins
/// the OTHER side of that change: a legitimately irregular -- but not adversarial -- real-world
/// layout must still resolve well within the cap, and to the correct segment.
/// </summary>
public class InterpolationSearchCapTests
{
    /// <summary>
    /// 699 uniform 716,800-byte segments followed by one genuinely short 390,400-byte tail -- the
    /// real shape a large usenet-posted RAR volume's segment sizes take (this is fixture 2's own
    /// layout from issue #28: 698 full segments + a short remainder). Mild, single-outlier skew
    /// like this is what "legitimately irregular, not corrupt" looks like in practice; it must
    /// resolve well under the 64-round cap, unlike the pathological/adversarial layout below.
    /// </summary>
    [Fact]
    public async Task RealWorldSkewedLayout_ResolvesCorrectly_WellUnderTheProbeCap()
    {
        const int fullSegmentCount = 699;
        const long fullSegmentSize = 716_800;
        const long shortTailSize = 390_400;

        var sizes = new long[fullSegmentCount + 1];
        for (var i = 0; i < fullSegmentCount; i++) sizes[i] = fullSegmentSize;
        sizes[fullSegmentCount] = shortTailSize;

        var offsets = new long[sizes.Length];
        long running = 0;
        for (var i = 0; i < sizes.Length; i++)
        {
            offsets[i] = running;
            running += sizes[i];
        }
        var totalBytes = running;

        var probeCount = 0;
        ValueTask<LongRange> Probe(int index)
        {
            probeCount++;
            return new ValueTask<LongRange>(new LongRange(offsets[index], offsets[index] + sizes[index]));
        }

        // Target a byte just past the start of the short tail segment -- where the single outlier
        // in this layout actually bites.
        var targetByte = offsets[fullSegmentCount] + 1;

        var result = await InterpolationSearch.Find(
            targetByte,
            new LongRange(0, sizes.Length),
            new LongRange(0, totalBytes),
            Probe,
            CancellationToken.None);

        Assert.Equal(fullSegmentCount, result.FoundIndex);
        Assert.True(result.FoundByteRange.Contains(targetByte));
        Assert.True(probeCount < 64, $"expected well under the 64-round cap, used {probeCount}");
    }

    /// <summary>
    /// A genuinely corrupt/adversarial size table (one that never converges -- every probe reports
    /// a range that keeps missing) hits the cap and fails fast with the existing
    /// <see cref="NzbWebDAV.Exceptions.SeekPositionNotFoundException"/>, rather than looping forever.
    /// </summary>
    [Fact]
    public async Task NonConvergingLayout_FailsFastAtTheProbeCap_InsteadOfLoopingForever()
    {
        const int segmentCount = 1000;
        // Every segment reports the SAME tiny range, so a search for a byte outside it can never
        // narrow the index range: each probe's result satisfies neither "too low" nor "too high"
        // relative to a target far beyond it, forcing the search to keep re-probing without
        // shrinking the window it excludes.
        ValueTask<LongRange> Probe(int _) => new(new LongRange(0, 1));

        var probeCount = 0;
        ValueTask<LongRange> CountingProbe(int i) { probeCount++; return Probe(i); }

        await Assert.ThrowsAsync<NzbWebDAV.Exceptions.SeekPositionNotFoundException>(() =>
            InterpolationSearch.Find(
                searchByte: 500,
                new LongRange(0, segmentCount),
                new LongRange(0, segmentCount), // each segment "claims" range [0,1), sums don't add up to this
                CountingProbe,
                CancellationToken.None));

        Assert.True(probeCount <= 65, $"cap should stop it at ~64 rounds, used {probeCount}");
    }
}
