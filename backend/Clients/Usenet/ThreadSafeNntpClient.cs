using System.Diagnostics;
using System.Collections.Immutable;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Services;
using NzbWebDAV.Streams;
using Usenet.Nzb;
using UsenetSharp.Clients;
using UsenetSharp.Models;
using UsenetSharp.Streams;
using Serilog;

namespace NzbWebDAV.Clients.Usenet;

public class ThreadSafeNntpClient : INntpClient
{
    private readonly UsenetClient _client;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly BandwidthService? _bandwidthService;
    private readonly int _providerIndex;
    private string? _currentGroup;
    private BufferToEndStream? _activeBufferStream;

    public ThreadSafeNntpClient(BandwidthService? bandwidthService = null, int providerIndex = -1)
    {
        _client = new UsenetClient();
        _bandwidthService = bandwidthService;
        _providerIndex = providerIndex;
    }

    public Task<bool> ConnectAsync(string host, int port, bool useSsl, CancellationToken cancellationToken)
    {
        // Reset state on connect
        return Synchronized(async () => {
            _currentGroup = null;
            await _client.ConnectAsync(host, port, useSsl, cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken);
    }

    public Task<bool> AuthenticateAsync(string user, string pass, CancellationToken cancellationToken)
    {
        return Synchronized(async () => {
            var response = await _client.AuthenticateAsync(user, pass, cancellationToken).ConfigureAwait(false);
            return response.ResponseCode == 281;
        }, cancellationToken);
    }

    public Task<UsenetStatResponse> StatAsync(string segmentId, CancellationToken cancellationToken)
    {
        return Synchronized(() => _client.StatAsync(new SegmentId(segmentId), cancellationToken), cancellationToken, recordLatency: true);
    }

    public Task<UsenetDateResponse> DateAsync(CancellationToken cancellationToken)
    {
        return Synchronized(() => _client.DateAsync(cancellationToken), cancellationToken, recordLatency: true);
    }

    public Task<UsenetArticleHeaders> GetArticleHeadersAsync(string segmentId, CancellationToken cancellationToken)
    {
        return Synchronized(async () =>
        {
            var headResponse = await _client.HeadAsync(new SegmentId(segmentId), cancellationToken).ConfigureAwait(false);
            if (headResponse.ResponseCode != 221 || headResponse.ArticleHeaders == null) 
                throw new UsenetArticleNotFoundException(segmentId);
            
            // Convert Dictionary to ImmutableDictionary<string, ImmutableHashSet<string>>
            // UsenetSharp returns Dictionary<string, string> where value is the full header value.
            // The old Usenet library had ImmutableHashSet for multi-value headers.
            // For now, we wrap the single string in a set.
            var headers = headResponse.ArticleHeaders.Headers.ToImmutableDictionary(
                kvp => kvp.Key,
                kvp => ImmutableHashSet.Create(kvp.Value)
            );
            
            return new UsenetArticleHeaders(headers);
        }, cancellationToken, recordLatency: true);
    }

    public async Task<YencHeaderStream> GetSegmentStreamAsync
    (
        string segmentId,
        bool includeHeaders,
        CancellationToken cancellationToken
    )
    {
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // We use a manual tracking here instead of Synchronized because we need to handle the stream disposal
            // and semaphore release carefully.
            var start = Stopwatch.GetTimestamp();
            
            Stream innerStream;
            UsenetArticleHeaders? articleHeaders = null;

            if (includeHeaders)
            {
                var response = await _client.ArticleAsync(new SegmentId(segmentId), null, cancellationToken).ConfigureAwait(false);
                if (response.ResponseCode != 220 || response.Stream == null) 
                    throw new UsenetArticleNotFoundException(segmentId);
                innerStream = response.Stream;
                if (response.ArticleHeaders != null)
                {
                    var headers = response.ArticleHeaders.Headers.ToImmutableDictionary(
                        kvp => kvp.Key,
                        kvp => ImmutableHashSet.Create(kvp.Value)
                    );
                    articleHeaders = new UsenetArticleHeaders(headers);
                }
            }
            else
            {
                var response = await _client.BodyAsync(new SegmentId(segmentId), null, cancellationToken).ConfigureAwait(false);
                if (response.ResponseCode != 222 || response.Stream == null) 
                    throw new UsenetArticleNotFoundException(segmentId);
                innerStream = response.Stream;
            }

            if (_bandwidthService != null && _providerIndex >= 0)
            {
                var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                _bandwidthService.RecordLatency(_providerIndex, (int)elapsedMs);
            }

            // Wrap in YencStream for decoding
            var yencStream = new YencStream(innerStream);
            
            // Read headers eagerly to ensure it's valid yEnc
            var yencHeader = await yencStream.GetYencHeadersAsync(cancellationToken).ConfigureAwait(false);
            if (yencHeader == null) throw new InvalidDataException("Missing yEnc headers");

            _activeBufferStream = new BufferToEndStream(((Stream)yencStream).OnDispose(OnDispose));

            return new YencHeaderStream(
                yencHeader,
                articleHeaders,
                _activeBufferStream
            );


            void OnDispose()
            {
                try
                {
                    _semaphore.Release();
                }
                catch (ObjectDisposedException) { }
            }
        }
        catch (Exception ex)
        {
            if (ex is UsenetArticleNotFoundException || ex is System.IO.IOException || ex is TimeoutException || ex is UsenetSharp.Exceptions.UsenetException || ex is ObjectDisposedException || ex is OperationCanceledException)
            {
                try { _semaphore.Release(); } catch (ObjectDisposedException) { }
                throw;
            }

            Log.Error(ex, "An unhandled error occurred in GetSegmentStreamAsync.");
            try { _semaphore.Release(); } catch (ObjectDisposedException) { }
            throw;
        }
    }

    public async Task<UsenetYencHeader> GetSegmentYencHeaderAsync(string segmentId, CancellationToken cancellationToken)
    {
        // Optimized implementation: Abort connection after reading header to save bandwidth.
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var start = Stopwatch.GetTimestamp();
            
            var response = await _client.BodyAsync(new SegmentId(segmentId), null, cancellationToken).ConfigureAwait(false);
            if (response.ResponseCode != 222 || response.Stream == null) 
                throw new UsenetArticleNotFoundException(segmentId);

            if (_bandwidthService != null && _providerIndex >= 0)
            {
                var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                _bandwidthService.RecordLatency(_providerIndex, (int)elapsedMs);
            }

            using var stream = response.Stream;
            using var yencStream = new YencStream(stream);
            
            var header = await yencStream.GetYencHeadersAsync(cancellationToken).ConfigureAwait(false);
            if (header == null) throw new InvalidDataException("Missing yEnc headers");

            // ABORT: Dispose the client to kill the socket and stop downloading the rest of the body.
            // This poisons the connection pool item, forcing a new connection next time.
            _client.Dispose();
            
            return header;
        }
        catch (Exception)
        {
            // If anything fails, or we dispose, we must release semaphore.
            // If we disposed _client, future calls will fail, effectively retiring this instance.
            throw;
        }
        finally
        {
            try { _semaphore.Release(); } catch (ObjectDisposedException) { }
        }
    }

    public async Task<long> GetFileSizeAsync(NzbFile file, CancellationToken cancellationToken)
    {
        if (file.Segments.Count == 0) return 0;
        var header = await GetSegmentYencHeaderAsync(file.Segments[^1].MessageId.Value, cancellationToken)
            .ConfigureAwait(false);
        return header.PartOffset + header.PartSize;
    }

    public async Task WaitForReady(CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_activeBufferStream != null)
            {
                // Wait for background draining to finish
                try
                {
                    await _activeBufferStream.PumpTask.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // If we timeout waiting for drain, the connection is dirty
                    throw new IOException("Timeout or cancellation while waiting for connection to drain.");
                }
                catch (Exception ex)
                {
                    throw new IOException("Error while waiting for connection to drain.", ex);
                }

                if (!_activeBufferStream.IsFullyDrained)
                {
                    // If it finished but not due to EOF (e.g. error or early stop), the connection is dirty
                    throw new IOException("Connection was not fully drained to EOF.");
                }

                _activeBufferStream = null;
            }
        }
        finally
        {
            try { _semaphore.Release(); } catch (ObjectDisposedException) { }
        }
    }

    public Task<UsenetGroupResponse> GroupAsync(string group, CancellationToken cancellationToken)
    {
        return Synchronized(() => _client.GroupAsync(group, cancellationToken), cancellationToken, recordLatency: true);
    }

    public Task<long> DownloadArticleBodyAsync(string group, long articleId, CancellationToken cancellationToken)
    {
        // This method was used to download body and return size.
        // Not used in critical path?
        // We can implement it using BodyAsync and counting bytes.
        return Synchronized(async () =>
        {
            // UsenetSharp doesn't support article ID as long? 
            // SegmentId supports string.
            // NNTP "BODY article-id" can be numeric.
            // SegmentId constructor takes string.
            
            var response = await _client.BodyAsync(new SegmentId(articleId.ToString()), null, cancellationToken).ConfigureAwait(false);
            if (response.ResponseCode != 222 || response.Stream == null) 
                throw new Exception($"Article {articleId} not found");

            using var stream = response.Stream;
            // Drain and count
            var buffer = new byte[8192];
            long size = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                size += read;
            }
            return size;
        }, cancellationToken);
    }

    private Task<T> Synchronized<T>(Func<Task<T>> run, CancellationToken cancellationToken, bool recordLatency = false)
    {
        return SynchronizedInternal(run, cancellationToken, recordLatency);
    }

    private async Task<T> SynchronizedInternal<T>(Func<Task<T>> run, CancellationToken cancellationToken, bool recordLatency = false)
    {
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var start = Stopwatch.GetTimestamp();
            var result = await run().ConfigureAwait(false);
            if (recordLatency && _bandwidthService != null && _providerIndex >= 0)
            {
                var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                _bandwidthService.RecordLatency(_providerIndex, (int)elapsedMs);
            }
            return result;
        }
        catch (Exception ex) when (ex is OperationCanceledException || ex is TimeoutException || ex is UsenetSharp.Exceptions.UsenetException)
        {
            // If network error, dispose
            Dispose();
            throw;
        }
        finally
        {
            try
            {
                _semaphore.Release();
            }
            catch (ObjectDisposedException)
            {
                // Ignore
            }
        }
    }

    public void Dispose()
    {
        _client.Dispose();
        _semaphore.Dispose();
    }
}