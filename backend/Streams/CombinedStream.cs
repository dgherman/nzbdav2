using System.Buffers;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Streams;

/// <summary>
/// Combines multiple streams into a single seekable stream.
/// Supports efficient seeking across parts without downloading intermediate data.
/// </summary>
public class CombinedStream : Stream
{
    private readonly List<StreamPart> _parts;
    private readonly long[] _cumulativeOffsets; // Start offset of each part
    private readonly long _totalLength;

    private int _currentPartIndex = -1;
    private Stream? _currentStream;
    private long _position;
    private bool _isDisposed;
    private bool _partRequiresLoading;

    // Pre-warms the next part's stream while the current one is still being read, so a forward
    // sequential crossing of a part boundary (a RAR volume, for a multipart file) finds a stream
    // that is already fetching instead of starting cold. This is separate from _streamCache below:
    // that cache is only ever populated by Seek() switching parts, never by the natural end-of-part
    // advance in ReadAsync, so it does nothing for sequential forward playback on its own.
    private Task? _prewarmTask;
    private int _prewarmedForPartIndex = -1;

    // Drives all pre-warm background work — deliberately NOT the per-ReadAsync-call CancellationToken.
    // A single triggering read's token can cancel (e.g. that specific request winds down) without the
    // pre-warmed part's construction being torn down: that construction is memoized onto the part's
    // StreamPart and a LATER, unrelated read depends on it too, so cancelling it because one read
    // happened to be in flight when it started would poison a shared result for every read after it.
    // Cancelled (and best-effort awaited) from Dispose/DisposeAsync so a pre-warm never outlives this
    // CombinedStream holding a connection slot for nobody.
    private readonly CancellationTokenSource _prewarmCts = new();

    // How close to a part's end (in bytes) triggers pre-warming the next part. Reuses the ring-buffer
    // size MemoryBudget already derives for one concurrent stream slot, rather than inventing a new
    // unbudgeted constant: it's the same "how much runway before a stream needs to be delivering data"
    // figure the rest of the streaming path is sized against.
    private static readonly long PrewarmTailBytes = MemoryBudget.RingBufferBytes;

    // Cache recently used streams to avoid re-creating them on seeks
    private readonly Dictionary<int, CachedStream> _streamCache = new();
    private readonly int _maxCachedStreams;
    private const int CacheExpirationSeconds = 30;

    public CombinedStream(IEnumerable<Task<Stream>> streams)
    {
        // Legacy constructor for backward compatibility - non-seekable mode
        _parts = new List<StreamPart>();
        _maxCachedStreams = 3;
        int index = 0;
        foreach (var streamTask in streams)
        {
            // For existing tasks, we just wrap them in a factory that returns the task
            _parts.Add(new StreamPart(() => streamTask, -1, index++)); // -1 = unknown length
        }
        _cumulativeOffsets = new long[_parts.Count];
        _totalLength = -1; // Unknown length
    }

    public CombinedStream(List<(Func<Task<Stream>> StreamFactory, long Length)> parts, int maxCachedStreams = 3)
    {
        _parts = new List<StreamPart>();
        _maxCachedStreams = maxCachedStreams;
        _cumulativeOffsets = new long[parts.Count];

        long offset = 0;
        var isTotalLengthKnown = true;
        for (int i = 0; i < parts.Count; i++)
        {
            _cumulativeOffsets[i] = offset;
            // Lazy loading: Store the factory, don't invoke it yet!
            _parts.Add(new StreamPart(parts[i].StreamFactory, parts[i].Length, i));
            
            if (parts[i].Length < 0)
            {
                isTotalLengthKnown = false;
            }
            else
            {
                offset += parts[i].Length;
            }
        }
        _totalLength = isTotalLengthKnown ? offset : -1;
    }

    public override bool CanRead => true;
    public override bool CanSeek => _totalLength >= 0; // Seekable if we know total length
    public override bool CanWrite => false;

    public override long Length => _totalLength >= 0
        ? _totalLength
        : throw new NotSupportedException("Length not available for legacy non-seekable streams");

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        return Task.Run(() => ReadAsync(buffer, offset, count)).GetAwaiter().GetResult();
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (count == 0) return 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            // Ensure we have a current stream loaded
            if (_currentStream == null)
            {
                if (_partRequiresLoading)
                {
                    // Seek occurred, load the target part
                    await LoadPartAsync(_currentPartIndex, cancellationToken).ConfigureAwait(false);
                    _partRequiresLoading = false;
                    if (_currentStream == null) return 0;
                }
                else if (_currentPartIndex < 0)
                {
                    // First read - load first part
                    await LoadPartAsync(0, cancellationToken).ConfigureAwait(false);
                    if (_currentStream == null) return 0;
                }
                else
                {
                    // Current stream was exhausted - try next part
                    if (_currentPartIndex + 1 >= _parts.Count) return 0; // No more parts
                    await LoadPartAsync(_currentPartIndex + 1, cancellationToken).ConfigureAwait(false);
                    if (_currentStream == null) return 0;
                }
            }

            // Read from current stream
            var readCount = await _currentStream.ReadAsync(
                buffer.AsMemory(offset, count),
                cancellationToken
            ).ConfigureAwait(false);

            // Serilog.Log.Debug("[CombinedStream] ReadAsync: Pos={Position}, Part={PartIndex}, Requested={Count}, Read={ReadCount}", 
            //    _position, _currentPartIndex, count, readCount);

            _position += readCount;
            if (readCount > 0)
            {
                TriggerPrewarmIfNeeded();
                return readCount;
            }

            // Current stream is exhausted - dispose and try next part
            Serilog.Log.Debug("[CombinedStream] Part {PartIndex} exhausted. Moving to next.", _currentPartIndex);
            await _currentStream.DisposeAsync().ConfigureAwait(false);
            _parts[_currentPartIndex].ResetStreamTask(); // Allow recreation if seeking back
            _currentStream = null;
        }

        return 0;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        if (!CanSeek)
            throw new NotSupportedException("Seeking is not supported for streams with unknown length");

        // Calculate target position
        long targetPosition = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _totalLength + offset,
            _ => throw new ArgumentException("Invalid seek origin", nameof(origin))
        };

        Serilog.Log.Debug("[CombinedStream] Seek: Offset={Offset}, Origin={Origin}, Target={TargetPosition}, TotalLen={TotalLength}", 
            offset, origin, targetPosition, _totalLength);

        // Validate target position
        if (targetPosition < 0)
            targetPosition = 0;
        if (targetPosition > _totalLength)
            targetPosition = _totalLength;

        // If already at target position, nothing to do
        if (targetPosition == _position)
            return _position;

        // Find which part contains the target position
        int targetPartIndex = FindPartIndex(targetPosition);
        long offsetInPart = targetPosition - _cumulativeOffsets[targetPartIndex];

        Serilog.Log.Debug("[CombinedStream] Seek resolved: PartIndex={PartIndex}, OffsetInPart={OffsetInPart}", 
            targetPartIndex, offsetInPart);

        // If seeking within current part, try to seek in current stream
        if (targetPartIndex == _currentPartIndex && _currentStream != null && _currentStream.CanSeek)
        {
            _currentStream.Seek(offsetInPart, SeekOrigin.Begin);
            _position = targetPosition;
            return _position;
        }

        // Need to switch parts - cache current stream instead of disposing
        if (_currentStream != null && _currentPartIndex >= 0)
        {
            CacheStream(_currentPartIndex, _currentStream);
            _currentStream = null;
        }

        // Load the target part and seek within it
        _currentPartIndex = targetPartIndex;
        _position = targetPosition;
        _partRequiresLoading = true; // Signal ReadAsync to load this part

        // Don't actually load the stream until next Read() - lazy loading
        // Just record that we need to seek to offsetInPart when we load it
        _parts[_currentPartIndex].PendingSeekOffset = offsetInPart;

        return _position;
    }

    private int FindPartIndex(long position)
    {
        // Binary search to find which part contains this position
        int left = 0;
        int right = _parts.Count - 1;

        while (left < right)
        {
            int mid = left + (right - left + 1) / 2;
            if (_cumulativeOffsets[mid] <= position)
                left = mid;
            else
                right = mid - 1;
        }

        return left;
    }

    /// <summary>
    /// Kicks off background construction of the next part's stream once the current part's remaining
    /// bytes fall inside the pre-warm tail. Fire-and-forget: LoadPartAsync picks up whatever this
    /// produced (warm or not — <see cref="StreamPart.GetStreamTask"/> memoizes the factory call, so
    /// this and the eventual real load share the same task) when the boundary is actually crossed.
    /// </summary>
    private void TriggerPrewarmIfNeeded()
    {
        if (_isDisposed) return;
        if (_currentPartIndex < 0 || _currentStream == null) return;

        var nextIndex = _currentPartIndex + 1;
        if (nextIndex >= _parts.Count) return;
        if (_prewarmedForPartIndex == nextIndex) return;

        var currentPart = _parts[_currentPartIndex];
        if (currentPart.Length < 0) return; // unknown length (legacy non-seekable mode) — can't judge distance to the end

        var consumedInPart = _position - _cumulativeOffsets[_currentPartIndex];
        var remainingInPart = currentPart.Length - consumedInPart;
        if (remainingInPart > PrewarmTailBytes) return;

        _prewarmedForPartIndex = nextIndex;
        // Own token (see _prewarmCts) — NOT the triggering read's cancellationToken. That read's
        // token can cancel independently of this construction, which is memoized onto the part and
        // shared with whatever read actually crosses the boundary later.
        //
        // Captured into a local BEFORE Task.Run, not read as _prewarmCts.Token inside the lambda: the
        // lambda body only runs once a thread-pool thread picks it up, which can be well after this
        // method returns. If Dispose() runs in that gap it cancels and then (once the task it can see
        // has settled) disposes _prewarmCts — reading .Token on an already-disposed CTS from inside
        // the lambda would throw ObjectDisposedException before PrewarmPartAsync's own try/catch even
        // starts, leaving an unobserved faulted task. A token obtained here, before scheduling, stays
        // valid to read/pass around regardless of what happens to its source CTS afterward.
        var prewarmToken = _prewarmCts.Token;
        // Only the most recently scheduled prewarm is tracked here, but that's the only one Dispose()
        // ever needs to catch: TriggerPrewarmIfNeeded only fires for _currentPartIndex + 1, and
        // _currentPartIndex only reaches nextIndex once LoadPartAsync has already adopted nextIndex's
        // stream (via the same memoized StreamPart.GetStreamTask this triggers) as _currentStream — at
        // that point it's a live, in-use stream, not something only reachable through this field. So by
        // the time a second call here could ever overwrite this field, whatever the previous one
        // targeted has already become _currentStream and is covered by the normal disposal paths
        // below, not by this reference.
        _prewarmTask = Task.Run(() => PrewarmPartAsync(nextIndex, prewarmToken), CancellationToken.None);
    }

    private async Task PrewarmPartAsync(int partIndex, CancellationToken prewarmToken)
    {
        try
        {
            var stream = await _parts[partIndex].GetStreamTask().ConfigureAwait(false);
            if (_isDisposed || prewarmToken.IsCancellationRequested)
            {
                // Disposed (or cancelled) while warming — don't leave its background fetch running
                // unattended, and don't let it get adopted by a real read after us.
                await stream.DisposeAsync().ConfigureAwait(false);
                return;
            }
            if (stream is IWarmableStream warmable)
            {
                await warmable.WarmupAsync(prewarmToken).ConfigureAwait(false);
            }
            if (_isDisposed || prewarmToken.IsCancellationRequested)
            {
                // Disposed mid-warmup. WarmupAsync's own adopt-if-still-current check means the
                // constructed inner stream is either already disposed or was never adopted; nothing
                // further to clean up here, but this must not log as a normal successful warm.
                return;
            }
            Serilog.Log.Debug("[CombinedStream] Pre-warmed part {PartIndex} ahead of the boundary.", partIndex);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "[CombinedStream] Pre-warm of part {PartIndex} failed; will load at the boundary instead.", partIndex);
        }
    }

    private async Task LoadPartAsync(int partIndex, CancellationToken cancellationToken)
    {
        if (partIndex < 0 || partIndex >= _parts.Count)
        {
            _currentStream = null;
            return;
        }

        var part = _parts[partIndex];

        // Check cache first
        if (TryGetCachedStream(partIndex, out var cachedStream))
        {
            _currentStream = cachedStream;
            _currentPartIndex = partIndex;
        }
        else
        {
            _currentPartIndex = partIndex;

            try
            {
                // Add timeout safety: if the stream task is taking too long, the cancellationToken should cancel it
                // But we also check if cancellation is requested before waiting
                cancellationToken.ThrowIfCancellationRequested();

                // Use WaitAsync to ensure cancellation token is respected even if task is stuck
                _currentStream = await part.GetStreamTask().WaitAsync(cancellationToken).ConfigureAwait(false);

                if (_currentStream == null)
                {
                    throw new InvalidOperationException($"Stream task returned null for part {partIndex}");
                }
            }
            catch (OperationCanceledException)
            {
                Serilog.Log.Debug("[CombinedStream] Loading part {PartIndex} was canceled", partIndex);
                throw;
            }
            catch (TimeoutException ex)
            {
                Serilog.Log.Warning("[CombinedStream] Timeout loading part {PartIndex}: {Message}", partIndex, ex.Message);
                throw;
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "[CombinedStream] Error loading part {PartIndex}: {ExceptionType} - {Message}",
                    partIndex, ex.GetType().Name, ex.Message);
                throw;
            }
        }

        // If there's a pending seek offset, seek within the newly loaded stream
        if (part.PendingSeekOffset > 0 && _currentStream != null && _currentStream.CanSeek)
        {
            _currentStream.Seek(part.PendingSeekOffset, SeekOrigin.Begin);
            part.PendingSeekOffset = 0;
        }
        else if (part.PendingSeekOffset > 0)
        {
            // Stream doesn't support seeking - we need to discard bytes
            // This is slower but necessary for non-seekable underlying streams
            await DiscardBytesInternalAsync(part.PendingSeekOffset, cancellationToken).ConfigureAwait(false);
            part.PendingSeekOffset = 0;
        }
    }

    private async Task DiscardBytesInternalAsync(long count, CancellationToken cancellationToken)
    {
        if (count == 0 || _currentStream == null) return;

        var remaining = count;
        var throwaway = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            while (remaining > 0)
            {
                var toRead = (int)Math.Min(remaining, throwaway.Length);
                var read = await _currentStream.ReadAsync(
                    throwaway.AsMemory(0, toRead),
                    cancellationToken
                ).ConfigureAwait(false);

                remaining -= read;
                if (read == 0) break;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(throwaway);
        }
    }

    [Obsolete("Use Seek() instead for better performance")]
    public async Task DiscardBytesAsync(long count)
    {
        // Legacy method - just use Seek for better performance
        if (CanSeek)
        {
            Seek(count, SeekOrigin.Current);
        }
        else
        {
            // Fallback for non-seekable streams
            // We MUST load the part and discard from the current stream
            long totalDiscarded = 0;
            while (totalDiscarded < count)
            {
                if (_currentStream == null)
                {
                    await LoadPartAsync(Math.Max(0, _currentPartIndex), CancellationToken.None).ConfigureAwait(false);
                    if (_currentStream == null) break;
                }

                var toDiscard = count - totalDiscarded;
                var throwaway = ArrayPool<byte>.Shared.Rent(65536);
                try
                {
                    var read = await _currentStream.ReadAsync(
                        throwaway.AsMemory(0, (int)Math.Min(toDiscard, throwaway.Length)),
                        CancellationToken.None
                    ).ConfigureAwait(false);

                    if (read == 0)
                    {
                        // Current stream exhausted, move to next
                        await _currentStream.DisposeAsync().ConfigureAwait(false);
                        _currentStream = null;
                        _currentPartIndex++;
                        continue;
                    }

                    totalDiscarded += read;
                    _position += read;
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(throwaway);
                }
            }
        }
    }

    public override void Flush()
    {
        throw new NotSupportedException();
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException();
    }

    protected override void Dispose(bool disposing)
    {
        if (_isDisposed) return;
        if (!disposing) return;

        // Set before touching anything else so a concurrently-running PrewarmPartAsync's _isDisposed
        // checks (after each of its awaits) see this promptly instead of racing our cleanup below.
        _isDisposed = true;
        _prewarmCts.Cancel();

        // Best-effort: give an in-flight pre-warm a short bounded window to unwind — it observes the
        // cancellation / _isDisposed and disposes whatever it constructed — before moving on. Dispose
        // is a synchronous, must-not-hang API, so this is bounded rather than awaited indefinitely;
        // the "dispose all loaded streams" loop below is the backstop if it doesn't finish in time.
        var prewarmSettled = _prewarmTask == null;
        if (_prewarmTask != null)
        {
            try { prewarmSettled = _prewarmTask.Wait(TimeSpan.FromSeconds(2)); }
            catch { prewarmSettled = true; /* the task's own already-logged failure — settled, just faulted */ }
        }

        _currentStream?.Dispose();
        _currentStream = null;

        // Dispose all cached streams
        foreach (var cached in _streamCache.Values)
        {
            cached.Stream.Dispose();
        }
        _streamCache.Clear();

        // Dispose all loaded streams, including any part the pre-warm task above constructed but
        // never got adopted as _currentStream. Every stream type here guards its own Dispose /
        // DisposeAsync against double-invocation, so this is safe even if the pre-warm task already
        // disposed the same stream itself upon observing cancellation.
        foreach (var part in _parts)
        {
            if (part.IsTaskCreated && part.GetStreamTask().IsCompletedSuccessfully)
            {
                part.GetStreamTask().Result?.Dispose();
            }
        }

        // Only dispose the CTS once the pre-warm task has actually settled — a still-running task past
        // the bounded wait above may still be holding/observing its token (e.g. inside a Register()
        // call several frames down in NzbFileStream), and disposing out from under it can throw
        // ObjectDisposedException there. Cancellation was already requested either way, so an orphaned
        // task still unwinds; we just leave its small CTS for the GC instead of the task.
        if (prewarmSettled) _prewarmCts.Dispose();
    }

    public override async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;

        _isDisposed = true;
        _prewarmCts.Cancel();

        var prewarmSettled = _prewarmTask == null;
        if (_prewarmTask != null)
        {
            try
            {
                await _prewarmTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                prewarmSettled = true;
            }
            catch (TimeoutException) { /* still running past the bounded wait — leave it to unwind on its own */ }
            catch { prewarmSettled = true; /* the task's own already-logged failure — settled, just faulted */ }
        }

        if (_currentStream != null)
        {
            await _currentStream.DisposeAsync().ConfigureAwait(false);
            _currentStream = null;
        }

        // Dispose all cached streams
        foreach (var cached in _streamCache.Values)
        {
            await cached.Stream.DisposeAsync().ConfigureAwait(false);
        }
        _streamCache.Clear();

        // Dispose all loaded streams — see the comment in Dispose(bool) above about why this is safe
        // even for a part the pre-warm task already cleaned up itself.
        foreach (var part in _parts)
        {
            if (part.IsTaskCreated && part.GetStreamTask().IsCompletedSuccessfully)
            {
                var stream = part.GetStreamTask().Result;
                if (stream != null)
                    await stream.DisposeAsync().ConfigureAwait(false);
            }
        }

        // See the comment in Dispose(bool) above: only dispose the CTS once the pre-warm task has
        // actually settled, so we never pull its token out from under a still-running task.
        if (prewarmSettled) _prewarmCts.Dispose();
        GC.SuppressFinalize(this);
    }

    private void CacheStream(int partIndex, Stream stream)
    {
        if (_maxCachedStreams <= 0)
        {
            stream.Dispose();
            // Reset the stream task so a new stream can be created on next access
            _parts[partIndex].ResetStreamTask();
            return;
        }

        // Clean up expired entries
        var now = DateTime.UtcNow;
        var expiredKeys = _streamCache
            .Where(kvp => (now - kvp.Value.LastAccessed).TotalSeconds > CacheExpirationSeconds)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in expiredKeys)
        {
            if (_streamCache.TryGetValue(key, out var expired))
            {
                expired.Stream.Dispose();
                _streamCache.Remove(key);
            }
        }

        // If cache is full, remove oldest entry
        if (_streamCache.Count >= _maxCachedStreams)
        {
            var oldest = _streamCache.OrderBy(kvp => kvp.Value.LastAccessed).First();
            oldest.Value.Stream.Dispose();
            _streamCache.Remove(oldest.Key);
        }

        // Add to cache
        _streamCache[partIndex] = new CachedStream(stream, now);
    }

    private bool TryGetCachedStream(int partIndex, out Stream? stream)
    {
        if (_streamCache.TryGetValue(partIndex, out var cached))
        {
            var age = (DateTime.UtcNow - cached.LastAccessed).TotalSeconds;
            if (age <= CacheExpirationSeconds)
            {
                // Update last accessed time
                _streamCache[partIndex] = new CachedStream(cached.Stream, DateTime.UtcNow);
                stream = cached.Stream;
                return true;
            }
            else
            {
                // Expired - dispose and remove
                cached.Stream.Dispose();
                _streamCache.Remove(partIndex);
            }
        }

        stream = null;
        return false;
    }

    private class StreamPart
    {
        private readonly Func<Task<Stream>> _streamFactory;
        private Task<Stream>? _streamTask;

        public Task<Stream> GetStreamTask()
        {
            _streamTask ??= _streamFactory();
            return _streamTask;
        }

        public bool IsTaskCreated => _streamTask != null;

        /// <summary>
        /// Reset the stream task so a new stream will be created on next GetStreamTask() call.
        /// This is needed when streams are disposed without caching (maxCachedStreams=0).
        /// </summary>
        public void ResetStreamTask()
        {
            _streamTask = null;
        }

        public long Length { get; }
        public int Index { get; }
        public long PendingSeekOffset { get; set; }

        public StreamPart(Func<Task<Stream>> streamFactory, long length, int index)
        {
            _streamFactory = streamFactory;
            Length = length;
            Index = index;
            PendingSeekOffset = 0;
        }
    }

    private class CachedStream
    {
        public Stream Stream { get; }
        public DateTime LastAccessed { get; }

        public CachedStream(Stream stream, DateTime lastAccessed)
        {
            Stream = stream;
            LastAccessed = lastAccessed;
        }
    }
}
