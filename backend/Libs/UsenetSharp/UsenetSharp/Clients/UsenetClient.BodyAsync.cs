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
                    pauseWriterThreshold: 1024 * 1024,      // 1MB
                    resumeWriterThreshold: 512 * 1024,       // 512KB
                    minimumSegmentSize: 65536                // 64KB
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

    // Phase 3: Chunk-based reading through StreamReader for maximum throughput
    private async Task ReadBodyToPipeAsync(PipeWriter writer, CancellationToken cancellationToken, Action onFinally)
    {
        CancellationTokenSource? cts = null;
        try
        {
            if (_reader == null)
            {
                await writer.CompleteAsync();
                return;
            }

            cts = CreateCtsWithTimeout(cancellationToken);
            var charBuffer = new char[131072]; // 128KB char buffer
            var byteBuffer = System.Buffers.ArrayPool<byte>.Shared.Rent(262144); // 256KB byte buffer for conversion
            try
            {
                int byteBufferPos = 0;
                int byteBufferLen = 0;
                bool shouldWrite = true;
                int totalBytesWritten = 0;
                const int FlushThreshold = 262144; // Flush every 256KB
                bool foundTerminator = false;

                while (!cts.Token.IsCancellationRequested && !foundTerminator)
                {
                    // Refill byte buffer if needed
                    if (byteBufferPos >= byteBufferLen)
                    {
                        // Read a large chunk of characters from StreamReader
                        var charsRead = await _reader.ReadAsync(charBuffer, 0, charBuffer.Length);
                        if (charsRead == 0) break; // EOF

                        // Convert Latin1 chars to bytes
                        byteBufferLen = 0;
                        for (int i = 0; i < charsRead; i++)
                        {
                            byteBuffer[byteBufferLen++] = (byte)charBuffer[i];
                        }
                        byteBufferPos = 0;
                    }

                    // Scan for terminator: \r\n.\r\n
                    var chunk = new ReadOnlySpan<byte>(byteBuffer, byteBufferPos, byteBufferLen - byteBufferPos);
                    var terminatorPos = FindTerminator(chunk);

                    int dataLen;
                    if (terminatorPos >= 0)
                    {
                        // Found terminator - write data up to (but not including) terminator
                        dataLen = terminatorPos;
                        foundTerminator = true;
                    }
                    else
                    {
                        // No terminator - write all available data
                        dataLen = chunk.Length;
                    }

                    if (shouldWrite && dataLen > 0)
                    {
                        // Write data to pipe with dot-unescaping
                        var dataToWrite = new ReadOnlySpan<byte>(byteBuffer, byteBufferPos, dataLen);
                        WriteDataToPipe(dataToWrite, writer);
                        totalBytesWritten += dataLen;

                        // Flush periodically for backpressure
                        if (totalBytesWritten >= FlushThreshold)
                        {
                            var result = await writer.FlushAsync(cts.Token);
                            if (result.IsCompleted || result.IsCanceled)
                            {
                                shouldWrite = false;
                            }
                            totalBytesWritten = 0;
                        }
                    }

                    // Advance buffer position
                    byteBufferPos += dataLen;
                }

                // Final flush
                if (totalBytesWritten > 0 && shouldWrite)
                {
                    await writer.FlushAsync(cts.Token);
                }
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(byteBuffer);
            }
        }
        catch (OperationCanceledException) when (cts != null && cts.Token.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            lock (this)
            {
                _backgroundException = ExceptionDispatchInfo.Capture(new TimeoutException("Timeout reading body from NNTP stream."));
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
            cts?.Dispose();
            onFinally.Invoke();
        }
    }
}
