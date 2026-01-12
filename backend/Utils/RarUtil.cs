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

    private static List<IRarHeader> GetRarHeaders(Stream stream, string? password)
    {
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
}