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
            const int FlushBatchSize = 32; // Flush every 32 lines for better throughput

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
                if (line == ".")
                {
                    break;
                }

                if (!shouldWrite) continue;

                // NNTP escaping: Lines starting with ".." should have the first dot removed
                // Use ReadOnlySpan to avoid string allocation from Substring
                ReadOnlySpan<char> lineSpan = line.AsSpan();
                if (lineSpan.Length >= 2 && lineSpan[0] == '.' && lineSpan[1] == '.')
                {
                    lineSpan = lineSpan.Slice(1);
                }

                // Write the line to the pipe using Latin1 to preserve byte values 0-255
                var byteCount = Encoding.Latin1.GetByteCount(lineSpan) + 2; // +2 for CRLF
                var span = writer.GetSpan(byteCount);
                var written = Encoding.Latin1.GetBytes(lineSpan, span);
                span[written++] = (byte)'\r';
                span[written++] = (byte)'\n';
                writer.Advance(written);

                lineCount++;

                // Batch flushes for better performance - flush every N lines instead of every line
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
            // Connection was disposed while reading - treat as graceful shutdown
            // Don't store in _backgroundException as this is expected during cleanup
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