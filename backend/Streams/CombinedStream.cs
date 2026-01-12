using System.Buffers;

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
        return ReadAsync(buffer, offset, count).GetAwaiter().GetResult();
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
            if (readCount > 0) return readCount;

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

        _currentStream?.Dispose();
        _currentStream = null;

        // Dispose all cached streams
        foreach (var cached in _streamCache.Values)
        {
            cached.Stream.Dispose();
        }
        _streamCache.Clear();

        // Dispose all loaded streams
        foreach (var part in _parts)
        {
            if (part.IsTaskCreated && part.GetStreamTask().IsCompletedSuccessfully)
            {
                part.GetStreamTask().Result?.Dispose();
            }
        }

        _isDisposed = true;
    }

    public override async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;

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

        // Dispose all loaded streams
        foreach (var part in _parts)
        {
            if (part.IsTaskCreated && part.GetStreamTask().IsCompletedSuccessfully)
            {
                var stream = part.GetStreamTask().Result;
                if (stream != null)
                    await stream.DisposeAsync().ConfigureAwait(false);
            }
        }

        _isDisposed = true;
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
