using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Streams;
using SharpCompress.Common.Rar.Headers;
using SharpCompress.IO;
using SharpCompress.Readers;

namespace NzbWebDAV.Utils;

public static class RarUtil
{
    public static async Task<List<IRarHeader>> GetRarHeadersAsync
    (
        Stream stream,
        string? password,
        CancellationToken ct
    )
    {
        // Wrap in a limit stream to prevent scanning the entire file if headers are missing
        // 100MB limit for multi-volume RAR files which can have headers at ~95MB+
        var maxBytes = 100 * 1024 * 1024;
        var limitedStream = new MaxBytesReadStream(stream, maxBytes, leaveOpen: true);

        await using var cancellableStream = new CancellableStream(limitedStream, ct, leaveOpen: true);
        return await Task.Run(() => GetRarHeaders(cancellableStream, password), ct).WaitAsync(ct).ConfigureAwait(false);
    }

    internal static List<IRarHeader> GetRarHeaders(Stream stream, string? password)
    {
        // issue #28 guard rail state: has an Archive header been read cleanly? For a RAR5 -hp
        // archive, Crypt precedes Archive and is metadata ONLY -- decoding it needs no password.
        // Archive is the first header actually encrypted with the password, so a wrong/missing
        // password fails right there. Only counting Archive (not Crypt alone) as "parsed cleanly"
        // keeps a genuine password failure from being relabeled as stream misalignment below.
        var markIteration = -1;
        var sawArchiveHeader = false;

        try
        {
            Serilog.Log.Debug("[RarUtil] GetRarHeaders starting. Stream position: {Position}, Length: {Length}, CanSeek: {CanSeek}",
                stream.Position, stream.Length, stream.CanSeek);

            var readerOptions = new ReaderOptions() { Password = password };
            var headerFactory = new RarHeaderFactory(StreamingMode.Seekable, readerOptions);
            var headers = new List<IRarHeader>();
            var iterationCount = 0;
            var lastPosition = stream.Position;

            Serilog.Log.Debug("[RarUtil] Starting to iterate through RAR headers");

            foreach (var header in headerFactory.ReadHeaders(stream))
            {
                iterationCount++;
                var currentPosition = stream.Position;

                // issue #28 guard rail: a well-formed RAR always yields Mark, then Archive -- or, for
                // a RAR5 -hp archive, Crypt then Archive. If the header right after Mark is neither
                // while a password was supplied, the byte stream feeding SharpCompress is already
                // misaligned at the front (a segment-size table that drifted from the real yEnc
                // article sizes). Fail fast rather than let the factory walk ciphertext to
                // end-of-stream -- that costs up to the 60s header-parse timeout per volume.
                if (header.HeaderType == HeaderType.Mark)
                {
                    markIteration = iterationCount;
                }
                else if (header.HeaderType == HeaderType.Archive)
                {
                    sawArchiveHeader = true;
                }
                else if (markIteration >= 0 && iterationCount == markIteration + 1
                         && password != null
                         && header.HeaderType is not (HeaderType.Archive or HeaderType.Crypt))
                {
                    throw new RarStreamMisalignedException(
                        $"RAR stream misaligned: header after MarkHeader was {header.HeaderType}, " +
                        "expected Archive or Crypt.");
                }

                // Detect infinite loop - if we've iterated 1000 times or position hasn't changed in 100 iterations
                if (iterationCount > 1000)
                {
                    Serilog.Log.Error("[RarUtil] INFINITE LOOP DETECTED! Exceeded 1000 iterations. Current position: {Position}, Last position: {LastPosition}",
                        currentPosition, lastPosition);
                    throw new InvalidOperationException($"RAR header reading stuck in infinite loop after {iterationCount} iterations");
                }

                if (iterationCount % 10 == 0)
                {
                    Serilog.Log.Debug("[RarUtil] Header iteration {Iteration}: Position {Position}/{Length}, HeaderType: {HeaderType}",
                        iterationCount, currentPosition, stream.Length, header.HeaderType);
                }

                Serilog.Log.Debug("[RarUtil] Processing header #{Iteration}: Type={HeaderType}, Position={Position}",
                    iterationCount, header.HeaderType, currentPosition);

                // add archive headers
                if (header.HeaderType is HeaderType.Archive or HeaderType.EndArchive)
                {
                    Serilog.Log.Debug("[RarUtil] Adding {HeaderType} header", header.HeaderType);
                    headers.Add(header);
                    lastPosition = currentPosition;
                    continue;
                }

                // skip comments
                if (header.HeaderType == HeaderType.Service)
                {
                    if (header.GetFileName() == "CMT")
                    {
                        var compressedSize = header.GetCompressedSize();
                        Serilog.Log.Debug("[RarUtil] Skipping comment header. Compressed size: {Size}", compressedSize);
                        var buffer = new byte[compressedSize];
                        _ = stream.Read(buffer, 0, buffer.Length);
                    }

                    lastPosition = currentPosition;
                    continue;
                }

                // we only care about file headers
                if (header.HeaderType != HeaderType.File)
                {
                    Serilog.Log.Debug("[RarUtil] Skipping non-file header: Type={HeaderType}", header.HeaderType);
                    lastPosition = currentPosition;
                    continue;
                }

                if (header.IsDirectory() || header.GetFileName() == "QO")
                {
                    Serilog.Log.Debug("[RarUtil] Skipping excluded file header: IsDirectory={IsDir}, FileName={FileName}",
                        header.IsDirectory(), header.GetFileName());
                    lastPosition = currentPosition;
                    continue;
                }

                // we only support stored files (compression method m0).
                var compressionMethod = header.GetCompressionMethod();
                Serilog.Log.Debug("[RarUtil] File header compression method: {Method}", compressionMethod);
                if (compressionMethod != 0)
                    throw new UnsupportedRarCompressionMethodException(
                        "Only rar files with compression method m0 are supported.");

                // TODO: support solid archives
                if (header.GetIsEncrypted() && header.GetIsSolid())
                    throw new Exception("Password-protected rar archives cannot be solid.");

                // add the headers
                Serilog.Log.Debug("[RarUtil] Adding file header: {FileName}", header.GetFileName());
                headers.Add(header);
                lastPosition = currentPosition;
            }

            Serilog.Log.Debug("[RarUtil] GetRarHeaders completed. Total headers found: {Count}, Total iterations: {Iterations}",
                headers.Count, iterationCount);

            return headers;
        }
        catch (RarStreamMisalignedException)
        {
            throw;
        }
        catch (Exception ex) when (password != null && sawArchiveHeader && IsLikelyMisalignment(ex))
        {
            // The Mark + Archive/Crypt headers at the front parsed cleanly, then a later header --
            // reached by seeking past the stored data -- decoded as garbage. On a password-protected
            // volume that is the issue #28 signature: the stream's segment offsets do not match the
            // real yEnc article sizes, so the seek landed on ciphertext or ran off the end. Surface
            // it as one clear, non-retryable error instead of the raw SharpCompress exception.
            Serilog.Log.Warning(
                "[RarUtil] Password-protected RAR header parse failed after the archive header at " +
                "stream position {Position}; treating as stream misalignment (issue #28): {Message}",
                stream.Position, ex.Message);
            throw new RarStreamMisalignedException(
                $"RAR stream misaligned while parsing password-protected volume headers: {ex.Message}");
        }
        catch (Exception e) when (e.TryGetInnerException<UsenetArticleNotFoundException>(out var missingArticleException))
        {
            Serilog.Log.Warning("[RarUtil] Missing article exception caught: {Message}", missingArticleException!.Message);
            throw missingArticleException!;
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "[RarUtil] Exception in GetRarHeaders at stream position {Position}: {Message}",
                stream.Position, ex.Message);
            throw;
        }
    }

    /// <summary>
    /// The exception shapes SharpCompress produces when it is handed bytes that are not a valid
    /// header -- the five faces of issue #28: "malformed vint", "Unknown Rar Header: N",
    /// "arithmetic overflow", "Unsupported crypto version of 99", "End of stream reached".
    /// </summary>
    private static bool IsLikelyMisalignment(Exception ex) => ex
        is SharpCompress.Common.InvalidFormatException
        or FormatException
        or OverflowException
        or EndOfStreamException
        or IndexOutOfRangeException
        or ArgumentOutOfRangeException;
}