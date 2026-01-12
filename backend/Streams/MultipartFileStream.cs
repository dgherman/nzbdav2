using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Streams;

public class MultipartFileStream : Stream
{
    private bool _isDisposed;
    private readonly UsenetStreamingClient _client;
    private readonly MultipartFile _multipartFile;
    private readonly ConnectionUsageContext _usageContext;
    private Stream? _currentStream;
    private long _currentPartEnd = 0;
    private long _position = 0;

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _multipartFile.FileSize;

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public MultipartFileStream(MultipartFile multipartFile, UsenetStreamingClient client, ConnectionUsageContext? usageContext = null)
    {
        _multipartFile = multipartFile;
        _client = client;
        _usageContext = usageContext ?? new ConnectionUsageContext(ConnectionUsageType.Unknown);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        return ReadAsync(buffer, offset, count).GetAwaiter().GetResult();
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (count == 0) return 0;
        while (_position < Length && !cancellationToken.IsCancellationRequested)
        {
            // If we don't have a current stream, get it.
            if (_currentStream == null)
            {
                var searchResult = GetCurrentStreamInfo();
                _currentStream = searchResult.Stream;
                _currentPartEnd = searchResult.FoundByteRange.EndExclusive;
            }

            // read from our current stream
            var readCount = await _currentStream.ReadAsync
            (
                buffer.AsMemory(offset, count),
                cancellationToken
            ).ConfigureAwait(false);
            
            if (readCount > 0)
            {
                _position += readCount;
                return readCount;
            }

            // If we couldn't read anything from our current stream,
            // it's time to advance to the next stream.
            // We advance position to the end of the current part to avoid infinite loops
            // if the underlying stream is shorter than advertised in metadata.
            _position = Math.Max(_position, _currentPartEnd);
            
            await _currentStream.DisposeAsync().ConfigureAwait(false);
            _currentStream = null;
        }

        return 0;
    }

    private (Stream Stream, LongRange FoundByteRange) GetCurrentStreamInfo()
    {
        var searchResult = InterpolationSearch.Find(
            _position,
            new LongRange(0, _multipartFile.FileParts.Count),
            new LongRange(0, Length),
            guess => _multipartFile.FileParts[guess].ByteRange
        );

        var filePart = _multipartFile.FileParts[searchResult.FoundIndex];
        var segmentSizes = filePart.NzbFile.Segments.Select(x => x.Size).ToArray();
        var stream = _client.GetFileStream(filePart.NzbFile.GetSegmentIds(), filePart.PartSize, 1, _usageContext, segmentSizes: segmentSizes);
        stream.Seek(_position - searchResult.FoundByteRange.StartInclusive, SeekOrigin.Begin);
        return (stream, searchResult.FoundByteRange);
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        var absoluteOffset = origin == SeekOrigin.Begin ? offset
            : origin == SeekOrigin.Current ? _position + offset
            : throw new InvalidOperationException("SeekOrigin must be Begin or Current.");
        if (_position == absoluteOffset) return _position;
        _position = absoluteOffset;
        _currentStream?.Dispose();
        _currentStream = null;
        return _position;
    }

    public override void Flush()
    {
        _currentStream?.Flush();
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
        _isDisposed = true;
    }

    public override async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        if (_currentStream != null) await _currentStream.DisposeAsync().ConfigureAwait(false);
        _isDisposed = true;
        GC.SuppressFinalize(this);
    }
}