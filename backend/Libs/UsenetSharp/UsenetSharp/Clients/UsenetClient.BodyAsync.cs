using System.IO.Pipelines;
using System.Runtime.ExceptionServices;
using System.Text;
using UsenetSharp.Models;

namespace UsenetSharp.Clients;

public partial class UsenetClient
{
    public Task<UsenetBodyResponse> BodyAsync(SegmentId segmentId, CancellationToken cancellationToken)
    {
        return BodyAsync(segmentId, null, cancellationToken);
    }

    public async Task<UsenetBodyResponse> BodyAsync
    (
        SegmentId segmentId,
        Action<ArticleBodyResult>? onConnectionReadyAgain,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await _commandLock.WaitAsync(cancellationToken);
        }
        catch
        {
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved);
            throw;
        }

        var isReadBodyToPipeAsyncStarted = false;

        try
        {
            ThrowIfUnhealthy();
            ThrowIfNotConnected();

            // Send BODY command with message-id
            await WriteLineAsync($"BODY <{segmentId}>".AsMemory(), _cts.Token);
            var response = await ReadLineAsync(_cts.Token);
            var responseCode = ParseResponseCode(response);

            // Article retrieved - body follows
            if (responseCode == (int)UsenetResponseType.ArticleRetrievedBodyFollows)
            {
                // Create a pipe for streaming the body data
                var pipe = new Pipe(new PipeOptions(
                    pauseWriterThreshold: long.MaxValue,
                    resumeWriterThreshold: long.MaxValue - 1
                ));

                // Start background task to read the body and write to pipe
                isReadBodyToPipeAsyncStarted = true;
                _ = ReadBodyToPipeAsync(pipe.Writer, _cts.Token, () =>
                {
                    pipe.Writer.Complete();
                    _commandLock.Release();
                    onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
                });

                // Return immediately with the stream and headers
                return new UsenetBodyResponse
                {
                    SegmentId = segmentId,
                    ResponseCode = responseCode,
                    ResponseMessage = response!,
                    Stream = pipe.Reader.AsStream(),
                };
            }

            return new UsenetBodyResponse()
            {
                ResponseCode = responseCode,
                ResponseMessage = response!,
                SegmentId = segmentId,
                Stream = null
            };
        }
        finally
        {
            if (!isReadBodyToPipeAsyncStarted)
            {
                _commandLock.Release();
                onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved);
            }
        }
    }

    private async Task ReadBodyToPipeAsync(PipeWriter writer, CancellationToken cancellationToken, Action onFinally)
    {
        try
        {
            if (_reader == null)
            {
                await writer.CompleteAsync();
                return;
            }

            var shouldWrite = true;
            var lineCount = 0;
            const int FlushBatchSize = 128; // Increased for better throughput

            // Read lines until we encounter the termination sequence (single dot on a line)
            while (!cancellationToken.IsCancellationRequested)
            {
                // Check if reader is still valid (could be disposed during execution)
                if (_reader == null)
                {
                    break;
                }

                var line = await ReadLineAsync(cancellationToken);

                if (line == null)
                {
                    // End of stream
                    break;
                }

                // Check for NNTP termination sequence (single dot)
                if (line.Length == 1 && line[0] == '.')
                {
                    break;
                }

                if (!shouldWrite) continue;

                // NNTP escaping: Lines starting with ".." should have the first dot removed
                ReadOnlySpan<char> lineSpan = line.AsSpan();
                if (lineSpan.Length >= 2 && lineSpan[0] == '.' && lineSpan[1] == '.')
                {
                    lineSpan = lineSpan.Slice(1);
                }

                // Fast write to pipe (direct cast char to byte for Latin1)
                var span = writer.GetSpan(lineSpan.Length + 2);
                for (int i = 0; i < lineSpan.Length; i++)
                {
                    span[i] = (byte)lineSpan[i];
                }
                span[lineSpan.Length] = (byte)'\r';
                span[lineSpan.Length + 1] = (byte)'\n';
                writer.Advance(lineSpan.Length + 2);

                lineCount++;

                // Batch flushes for better performance
                if (lineCount >= FlushBatchSize)
                {
                    var result = await RunWithTimeoutAsync(writer.FlushAsync, cancellationToken);
                    if (result.IsCompleted || result.IsCanceled)
                    {
                        shouldWrite = false;
                    }
                    lineCount = 0;
                }
            }

            // Final flush for any remaining data
            if (lineCount > 0 && shouldWrite)
            {
                await RunWithTimeoutAsync(writer.FlushAsync, cancellationToken);
            }
        }
        catch (NullReferenceException)
        {
            // Connection was disposed while reading
        }
        catch (Exception e)
        {
            lock (this)
            {
                _backgroundException = ExceptionDispatchInfo.Capture(e);
            }
        }
        finally
        {
            onFinally.Invoke();
        }
    }
}
