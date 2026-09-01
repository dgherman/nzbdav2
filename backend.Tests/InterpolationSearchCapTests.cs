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
    /// A COHERENT (monotonic, physically valid) size table can still degrade interpolation search
    /// toward its O(n) worst case: many uniform tiny segments followed by one huge outlier. Every
    /// round here genuinely narrows the search range by exactly one index -- this is real linear
    /// degradation, not range exhaustion or an inconsistent probe response -- so hitting the cap
    /// proves the cap is load-bearing, not a formality.
    ///
    /// <see cref="ProbeCapMirror"/> tiny leading segments is the exact count that needs 65 rounds
    /// to converge with NO cap (verified by simulating the same guess/narrow arithmetic
    /// <see cref="InterpolationSearch.Find"/> uses, in Python, outside this test). With the real
    /// 64-round cap in place, round 65 never gets a probe: it throws immediately, so exactly 64
    /// probes happen before <see cref="NzbWebDAV.Exceptions.SeekPositionNotFoundException"/> fires.
    /// Asserting the EXACT probe count (not just "it throws" or "it throws below some loose bound")
    /// is what makes this fail if the cap is removed or its value changes: remove the cap and this
    /// layout converges on the real 65th probe instead of throwing (<c>Assert.ThrowsAsync</c>
    /// fails); raise or lower the cap and the probe count no longer matches
    /// <see cref="ProbeCapMirror"/>.
    ///
    /// <see cref="ProbeCapMirror"/> mirrors <c>InterpolationSearch.MaxProbeRounds</c>, which is
    /// `private` and so not visible here; keep the two in sync if that constant ever changes.
    /// </summary>
    [Fact]
    public async Task NonConvergingLayout_HitsTheProbeCapExactly_ThenFailsFast()
    {
        const int probeCapMirror = 64;
        const int tinySegmentCount = probeCapMirror; // see doc comment: this exact count needs 65 rounds uncapped
        const long tinySegmentSize = 100;
        const long hugeTailSize = 50_000_000;

        var sizes = new long[tinySegmentCount + 1];
        for (var i = 0; i < tinySegmentCount; i++) sizes[i] = tinySegmentSize;
        sizes[tinySegmentCount] = hugeTailSize;

        var offsets = new long[sizes.Length];
        long running = 0;
        for (var i = 0; i < sizes.Length; i++)
        {
            offsets[i] = running;
            running += sizes[i];
        }
        var totalBytes = running;

        var probeCount = 0;
        ValueTask<LongRange> CountingProbe(int index)
        {
            probeCount++;
            return new ValueTask<LongRange>(new LongRange(offsets[index], offsets[index] + sizes[index]));
        }

        // Target the huge tail segment itself -- the position that forces the pathological
        // one-index-per-round elimination all the way through the tiny leading segments.
        var targetByte = offsets[tinySegmentCount] + 1;

        var ex = await Assert.ThrowsAsync<NzbWebDAV.Exceptions.SeekPositionNotFoundException>(() =>
            InterpolationSearch.Find(
                targetByte,
                new LongRange(0, sizes.Length),
                new LongRange(0, totalBytes),
                CountingProbe,
                CancellationToken.None));

        Assert.Equal(probeCapMirror, probeCount);
        Assert.Contains($"within {probeCapMirror} probes", ex.Message);
    }
}
