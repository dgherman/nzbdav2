using NzbWebDAV.Exceptions;
using NzbWebDAV.Models;

namespace NzbWebDAV.Utils;

public static class InterpolationSearch
{
    public static Result Find
    (
        long searchByte,
        LongRange indexRangeToSearch,
        LongRange byteRangeToSearch,
        Func<int, LongRange> getByteRangeOfGuessedIndex
    )
    {
        return Find(
            searchByte,
            indexRangeToSearch,
            byteRangeToSearch,
            guess => new ValueTask<LongRange>(getByteRangeOfGuessedIndex(guess)),
            SigtermUtil.GetCancellationToken()
        ).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Hard cap on probe rounds (each round = one HEAD-equivalent fetch via
    /// <c>getByteRangeOfGuessedIndex</c>). Interpolation search is near-O(log log n) on segment
    /// sizes that vary smoothly, but a pathological layout (e.g. one wildly oversized segment
    /// among many tiny ones) can degrade guesses toward linear. This bounds worst-case fetch
    /// fan-out instead of trusting the shape of real-world data -- 64 rounds covers a uniform
    /// binary search over ~10^19 segments, far past any real NZB, so this only fires on a
    /// genuinely corrupt/adversarial size table.
    /// </summary>
    private const int MaxProbeRounds = 64;

    public static async Task<Result> Find
    (
        long searchByte,
        LongRange indexRangeToSearch,
        LongRange byteRangeToSearch,
        Func<int, ValueTask<LongRange>> getByteRangeOfGuessedIndex,
        CancellationToken cancellationToken
    )
    {
        var rounds = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (++rounds > MaxProbeRounds)
                throw new SeekPositionNotFoundException(
                    $"Corrupt file. Could not find byte position {searchByte} within {MaxProbeRounds} probes.");

            // make sure our search is even possible.
            if (!byteRangeToSearch.Contains(searchByte) || indexRangeToSearch.Count <= 0)
                throw new SeekPositionNotFoundException($"Corrupt file. Cannot find byte position {searchByte}.");

            // make a guess
            var searchByteFromStart = searchByte - byteRangeToSearch.StartInclusive;
            var bytesPerIndex = (double)byteRangeToSearch.Count / indexRangeToSearch.Count;
            var guessFromStart = (long)Math.Floor(searchByteFromStart / bytesPerIndex);
            var guessedIndex = (int)(indexRangeToSearch.StartInclusive + guessFromStart);
            var byteRangeOfGuessedIndex = await getByteRangeOfGuessedIndex(guessedIndex).ConfigureAwait(false);

            // make sure the result is within the range of our search space
            if (!byteRangeOfGuessedIndex.IsContainedWithin(byteRangeToSearch))
                throw new SeekPositionNotFoundException($"Corrupt file. Cannot find byte position {searchByte}.");

            // if we guessed too low, adjust our lower bounds in order to search higher next time
            if (byteRangeOfGuessedIndex.EndExclusive <= searchByte)
            {
                indexRangeToSearch = indexRangeToSearch with { StartInclusive = guessedIndex + 1 };
                byteRangeToSearch = byteRangeToSearch with { StartInclusive = byteRangeOfGuessedIndex.EndExclusive };
            }

            // if we guessed too high, adjust our upper bounds in order to search lower next time
            else if (byteRangeOfGuessedIndex.StartInclusive > searchByte)
            {
                indexRangeToSearch = indexRangeToSearch with { EndExclusive = guessedIndex };
                byteRangeToSearch = byteRangeToSearch with { EndExclusive = byteRangeOfGuessedIndex.StartInclusive };
            }

            // if we guessed correctly, we're done
            else if (byteRangeOfGuessedIndex.Contains(searchByte))
                return new Result(guessedIndex, byteRangeOfGuessedIndex);
        }
    }

    public record Result(int FoundIndex, LongRange FoundByteRange);
}