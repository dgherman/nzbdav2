using System.Buffers;
using System.IO.Hashing;
using System.Text;
using RapidYencSharp;
using UsenetSharp.Models;

namespace UsenetSharp.Streams;

/// <summary>
/// A high-performance read-only stream that decodes yEnc-encoded content from an inner stream.
/// Uses chunked buffered reading and zero-allocation Span-based decoding.
/// </summary>
public class YencStream : FastReadOnlyNonSeekableStream
{
    private readonly Stream _innerStream;

    // Header state
    private bool _headersRead;
    private UsenetYencHeader? _yencHeaders;

    // Read buffer for chunked reading from stream (8KB chunks)
    private byte[]? _readBuffer;
    private int _readBufferPosition;
    private int _readBufferLength;

    // Decode buffer for decoded line data
    private byte[]? _decodeBuffer;
    private int _decodeBufferPosition;
    private int _decodeBufferLength;

    // Line assembly buffer for lines spanning chunk boundaries
    private byte[]? _lineAssemblyBuffer;
    private int _lineAssemblyLength;

    // Decoder state for tracking escape sequences across lines
    private RapidYencDecoderState? _decoderState;

    // CRC validation state
    private System.IO.Hashing.Crc32? _crc32;
    private uint? _expectedCrc32;
    private long _totalDecodedBytes;

    private bool _endReached;

    private const int ReadBufferSize = 65536; // 64KB read chunks for efficient network I/O
    private const int DecodeBufferSize = 512; // Typical yEnc line decodes to ~128 bytes
    private const int LineAssemblyBufferSize = 1024; // Max line length with overhead

    public YencStream(Stream innerStream)
    {
        _innerStream = innerStream ?? throw new ArgumentNullException(nameof(innerStream));
        _headersRead = false;
        _readBufferPosition = 0;
        _readBufferLength = 0;
        _decodeBufferPosition = 0;
        _decodeBufferLength = 0;
        _lineAssemblyLength = 0;
        _endReached = false;
        _totalDecodedBytes = 0;

        // Rent all buffers from ArrayPool for zero-allocation
        _readBuffer = ArrayPool<byte>.Shared.Rent(ReadBufferSize);
        _decodeBuffer = ArrayPool<byte>.Shared.Rent(DecodeBufferSize);
        _lineAssemblyBuffer = ArrayPool<byte>.Shared.Rent(LineAssemblyBufferSize);

        // Initialize CRC32 for validation
        _crc32 = new System.IO.Hashing.Crc32();
    }

    /// <summary>
    /// Gets the yEnc headers from the stream. If headers haven't been read yet, reads and parses them asynchronously.
    /// </summary>
    public virtual async ValueTask<UsenetYencHeader?> GetYencHeadersAsync(CancellationToken cancellationToken = default)
    {
        if (!_headersRead)
        {
            await ParseHeadersAsync(cancellationToken);
            _headersRead = true;
        }

        return _yencHeaders;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        // Parse headers on first read
        if (!_headersRead)
        {
            await ParseHeadersAsync(cancellationToken);
            _headersRead = true;
        }

        if (_endReached && _decodeBufferPosition >= _decodeBufferLength)
        {
            return 0; // End of stream
        }

        int totalRead = 0;

        while (totalRead < buffer.Length && !_endReached)
        {
            // Serve from decode buffer if we have leftover data
            if (_decodeBufferPosition < _decodeBufferLength)
            {
                int bytesToCopy = Math.Min(buffer.Length - totalRead, _decodeBufferLength - _decodeBufferPosition);
                _decodeBuffer.AsSpan(_decodeBufferPosition, bytesToCopy).CopyTo(buffer.Span.Slice(totalRead));
                _decodeBufferPosition += bytesToCopy;
                totalRead += bytesToCopy;
            }
            else
            {
                // Need to decode next line
                var lineMemory = await ReadNextLineAsync(cancellationToken);

                if (lineMemory.Length == 0)
                {
                    _endReached = true;
                    break;
                }

                var lineSpan = lineMemory.Span;

                // Check for =yend marker
                if (StartsWithYEnd(lineSpan))
                {
                    _endReached = true;

                    // Parse =yend line for CRC32 validation
                    var yendLine = Encoding.Latin1.GetString(lineSpan);
                    ParseYendLine(yendLine);

                    // Validate CRC32 if we have an expected value
                    ValidateCrc32();

                    break;
                }

                int remainingBufferSpace = buffer.Length - totalRead;

                // Optimization: decode directly into caller's buffer if there's enough space
                // Typical decoded yEnc line is ~128 bytes (from ~170 byte encoded line)
                if (remainingBufferSpace >= DecodeBufferSize)
                {
                    // Decode directly to caller's buffer - ZERO COPY!
                    int decodedLength = YencDecoder.DecodeEx(
                        lineSpan, buffer.Span.Slice(totalRead), ref _decoderState, isRaw: false);

                    // Update CRC32 with decoded data
                    if (decodedLength > 0 && _crc32 != null)
                    {
                        _crc32.Append(buffer.Span.Slice(totalRead, decodedLength));
                        _totalDecodedBytes += decodedLength;
                    }

                    totalRead += decodedLength;
                }
                else
                {
                    // Not enough space - decode to intermediate buffer and copy what fits
                    int decodedLength = YencDecoder.DecodeEx(lineSpan, _decodeBuffer, ref _decoderState, isRaw: false);

                    // Update CRC32 with decoded data in intermediate buffer
                    if (decodedLength > 0 && _crc32 != null && _decodeBuffer != null)
                    {
                        _crc32.Append(_decodeBuffer.AsSpan(0, decodedLength));
                        _totalDecodedBytes += decodedLength;
                    }

                    _decodeBufferPosition = 0;
                    _decodeBufferLength = decodedLength;
                    // Next iteration will copy from decode buffer
                }
            }
        }

        return totalRead;
    }

    /// <summary>
    /// Reads the next line from the stream using buffered chunked reading.
    /// Handles lines spanning multiple read chunks efficiently.
    /// </summary>
    private async ValueTask<ReadOnlyMemory<byte>> ReadNextLineAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            // Ensure we have data in read buffer
            if (_readBufferPosition >= _readBufferLength)
            {
                bool hasMoreData = await FillReadBufferAsync(cancellationToken);
                if (!hasMoreData && _lineAssemblyLength == 0)
                {
                    return ReadOnlyMemory<byte>.Empty; // EOF
                }

                if (!hasMoreData)
                {
                    // Return partial line at EOF
                    var result = new ReadOnlyMemory<byte>(_lineAssemblyBuffer, 0, _lineAssemblyLength);
                    _lineAssemblyLength = 0;
                    return result;
                }
            }

            // Scan for line ending in current buffer
            var searchSpan = _readBuffer.AsSpan(_readBufferPosition, _readBufferLength - _readBufferPosition);
            int lfIndex = searchSpan.IndexOf((byte)'\n');

            if (lfIndex >= 0)
            {
                // Found complete line
                int lineEndPos = _readBufferPosition + lfIndex;
                int lineStartPos = _readBufferPosition;

                // Check for CRLF vs LF
                int lineLength = lfIndex;
                if (lfIndex > 0 && searchSpan[lfIndex - 1] == (byte)'\r')
                {
                    lineLength--; // Exclude CR
                }

                _readBufferPosition = lineEndPos + 1; // Move past LF

                // If we have a partial line in assembly buffer, combine them
                if (_lineAssemblyLength > 0)
                {
                    searchSpan.Slice(0, lineLength).CopyTo(_lineAssemblyBuffer.AsSpan(_lineAssemblyLength));
                    int totalLength = _lineAssemblyLength + lineLength;
                    _lineAssemblyLength = 0;
                    return new ReadOnlyMemory<byte>(_lineAssemblyBuffer, 0, totalLength);
                }
                else
                {
                    // Return line directly from read buffer
                    return new ReadOnlyMemory<byte>(_readBuffer, lineStartPos, lineLength);
                }
            }
            else
            {
                // No line ending in current buffer - save to assembly buffer and read more
                int remainingLength = _readBufferLength - _readBufferPosition;
                searchSpan.CopyTo(_lineAssemblyBuffer.AsSpan(_lineAssemblyLength));
                _lineAssemblyLength += remainingLength;
                _readBufferPosition = _readBufferLength; // Consumed entire buffer

                // Continue loop to read more data
            }
        }
    }

    /// <summary>
    /// Fills the read buffer with data from the inner stream.
    /// Returns true if data was read, false if EOF.
    /// </summary>
    private async ValueTask<bool> FillReadBufferAsync(CancellationToken cancellationToken)
    {
        _readBufferPosition = 0;
        _readBufferLength = await _innerStream.ReadAsync(_readBuffer.AsMemory(0, ReadBufferSize), cancellationToken);
        return _readBufferLength > 0;
    }

    private async Task ParseHeadersAsync(CancellationToken cancellationToken)
    {
        string? ybeginLine = null;
        string? ypartLine = null;

        // Read lines until we find =ybegin (skip empty lines that may appear before it)
        while (true)
        {
            var lineMemory = await ReadNextLineAsync(cancellationToken);

            if (lineMemory.Length == 0)
            {
                // Distinguish between empty line and EOF
                // If buffer is exhausted (length is 0), we've hit EOF
                if (_readBufferLength == 0)
                {
                    throw new InvalidDataException("Reached end of stream without finding =ybegin header");
                }

                // Empty line - skip it
                continue;
            }

            var lineSpan = lineMemory.Span;
            if (StartsWithYBegin(lineSpan))
            {
                ybeginLine = Encoding.Latin1.GetString(lineSpan);
                break;
            }
        }

        // Check if next line is =ypart or encoded data
        var nextLineMemory = await ReadNextLineAsync(cancellationToken);
        if (nextLineMemory.Length > 0)
        {
            var nextLineSpan = nextLineMemory.Span;

            if (StartsWithYPart(nextLineSpan))
            {
                ypartLine = Encoding.Latin1.GetString(nextLineSpan);
                // Next line will be encoded data, ReadAsync will handle it
            }
            else if (!StartsWithYEnd(nextLineSpan))
            {
                // This is the first encoded data line - decode it now
                int decodedLength = YencDecoder.DecodeEx(nextLineSpan, _decodeBuffer!, ref _decoderState, isRaw: false);

                // Update CRC32 with decoded data
                if (decodedLength > 0 && _crc32 != null && _decodeBuffer != null)
                {
                    _crc32.Append(_decodeBuffer.AsSpan(0, decodedLength));
                    _totalDecodedBytes += decodedLength;
                }

                _decodeBufferPosition = 0;
                _decodeBufferLength = decodedLength;
            }
        }

        _yencHeaders = ParseYencHeaders(ybeginLine, ypartLine);
    }

    private static bool StartsWithYBegin(ReadOnlySpan<byte> line) =>
        line.Length >= 7 && line.Slice(0, 7).SequenceEqual("=ybegin"u8);

    private static bool StartsWithYPart(ReadOnlySpan<byte> line) =>
        line.Length >= 6 && line.Slice(0, 6).SequenceEqual("=ypart"u8);

    private static bool StartsWithYEnd(ReadOnlySpan<byte> line) =>
        line.Length >= 5 && line.Slice(0, 5).SequenceEqual("=yend"u8);

    private static UsenetYencHeader ParseYencHeaders(string ybeginLine, string? ypartLine)
    {
        // Parse =ybegin line
        // Format: =ybegin part=123 total=123 line=123 size=123 name=filename.bin
        var ybeginParts = ybeginLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        int lineLength = 128; // default
        long fileSize = 0;
        string fileName = string.Empty;
        int partNumber = 0;
        int totalParts = 0;

        foreach (var part in ybeginParts.Skip(1)) // Skip "=ybegin"
        {
            var keyValue = part.Split('=', 2);
            if (keyValue.Length == 2)
            {
                var key = keyValue[0];
                var value = keyValue[1];

                switch (key)
                {
                    case "line":
                        int.TryParse(value, out lineLength);
                        break;
                    case "size":
                        long.TryParse(value, out fileSize);
                        break;
                    case "name":
                        fileName = value;
                        break;
                    case "part":
                        int.TryParse(value, out partNumber);
                        break;
                    case "total":
                        int.TryParse(value, out totalParts);
                        break;
                }
            }
        }

        // Parse =ypart line if present
        // Format: =ypart begin=1 end=123456
        long partSize = fileSize;
        long partOffset = 0;

        if (ypartLine != null)
        {
            var ypartParts = ypartLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            long partBegin = 0;
            long partEnd = 0;

            foreach (var part in ypartParts.Skip(1)) // Skip "=ypart"
            {
                var keyValue = part.Split('=', 2);
                if (keyValue.Length == 2)
                {
                    var key = keyValue[0];
                    var value = keyValue[1];

                    switch (key)
                    {
                        case "begin":
                            long.TryParse(value, out partBegin);
                            break;
                        case "end":
                            long.TryParse(value, out partEnd);
                            break;
                    }
                }
            }

            partOffset = partBegin - 1; // yEnc uses 1-based indexing
            partSize = partEnd - partBegin + 1;
        }

        return new UsenetYencHeader
        {
            FileName = fileName,
            FileSize = fileSize,
            LineLength = lineLength,
            PartNumber = partNumber,
            TotalParts = totalParts,
            PartSize = partSize,
            PartOffset = partOffset
        };
    }

    private void ParseYendLine(string yendLine)
    {
        // Parse =yend line
        // Format: =yend size=12345 part=1 pcrc32=a1b2c3d4 crc32=e5f6a7b8
        var parts = yendLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        foreach (var part in parts.Skip(1)) // Skip "=yend"
        {
            var keyValue = part.Split('=', 2);
            if (keyValue.Length == 2)
            {
                var key = keyValue[0];
                var value = keyValue[1];

                // For multipart files, use pcrc32 (part CRC)
                // For single files, use crc32 (file CRC)
                if (key == "pcrc32" || (key == "crc32" && !_expectedCrc32.HasValue))
                {
                    if (uint.TryParse(value, System.Globalization.NumberStyles.HexNumber, null, out var crc))
                    {
                        _expectedCrc32 = crc;
                    }
                }
            }
        }
    }

    private void ValidateCrc32()
    {
        if (_expectedCrc32.HasValue && _crc32 != null)
        {
            var actualCrc32 = BitConverter.ToUInt32(_crc32.GetCurrentHash());

            if (actualCrc32 != _expectedCrc32.Value)
            {
                var fileName = _yencHeaders?.FileName ?? "unknown";
                var partNumber = _yencHeaders?.PartNumber ?? 0;
                throw new InvalidDataException(
                    $"YENC CRC32 validation failed for '{fileName}' part {partNumber}: " +
                    $"expected 0x{_expectedCrc32.Value:X8}, got 0x{actualCrc32:X8}. " +
                    $"Decoded {_totalDecodedBytes} bytes. The segment may be corrupted or incomplete."
                );
            }
        }
        // If no expected CRC32 was provided in =yend, we can't validate (some encoders omit it)
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Return all buffers to ArrayPool
            if (_readBuffer != null)
            {
                ArrayPool<byte>.Shared.Return(_readBuffer);
                _readBuffer = null;
            }

            if (_decodeBuffer != null)
            {
                ArrayPool<byte>.Shared.Return(_decodeBuffer);
                _decodeBuffer = null;
            }

            if (_lineAssemblyBuffer != null)
            {
                ArrayPool<byte>.Shared.Return(_lineAssemblyBuffer);
                _lineAssemblyBuffer = null;
            }

            // Dispose the inner stream
            _innerStream?.Dispose();
        }

        base.Dispose(disposing);
    }
}