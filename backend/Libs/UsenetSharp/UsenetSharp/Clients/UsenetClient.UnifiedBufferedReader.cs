using System;
using System.Buffers;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace UsenetSharp.Clients;

public partial class UsenetClient
{
    /// <summary>
    /// Unified buffered reader that handles both NNTP line reading (responses/headers)
    /// and chunk-based body reading, with full control over buffering.
    /// This replaces StreamReader to avoid buffering conflicts.
    /// </summary>
    private class UnifiedBufferedReader : IDisposable
    {
        private readonly Stream _stream;
        private readonly byte[] _buffer;
        private readonly int _bufferSize;
        private int _bufferPos;
        private int _bufferLength;
        private bool _disposed;

        public UnifiedBufferedReader(Stream stream, int bufferSize = 65536) // 64KB default
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
            _bufferSize = bufferSize;
            _buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
            _bufferPos = 0;
            _bufferLength = 0;
        }

        /// <summary>
        /// Reads a line from the stream (for NNTP commands/responses).
        /// Returns string decoded as Latin1 (preserves byte values 0-255).
        /// Returns null on EOF.
        /// </summary>
        public async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            var lineBuilder = new StringBuilder(256);

            while (true)
            {
                // Ensure we have data in buffer
                if (_bufferPos >= _bufferLength)
                {
                    await FillBufferAsync(cancellationToken);
                    if (_bufferLength == 0)
                        return lineBuilder.Length > 0 ? lineBuilder.ToString() : null; // EOF
                }

                // Scan for \r\n in current buffer
                int startPos = _bufferPos;
                while (_bufferPos < _bufferLength)
                {
                    if (_buffer[_bufferPos] == '\r' && _bufferPos + 1 < _bufferLength && _buffer[_bufferPos + 1] == '\n')
                    {
                        // Found \r\n - convert accumulated bytes to string
                        for (int i = startPos; i < _bufferPos; i++)
                        {
                            lineBuilder.Append((char)_buffer[i]); // Latin1: direct byte-to-char
                        }
                        _bufferPos += 2; // Skip \r\n
                        return lineBuilder.ToString();
                    }
                    else if (_buffer[_bufferPos] == '\r' && _bufferPos + 1 >= _bufferLength)
                    {
                        // \r at end of buffer - need to read more to check for \n
                        break;
                    }
                    _bufferPos++;
                }

                // Accumulated bytes before needing more data
                for (int i = startPos; i < _bufferPos; i++)
                {
                    lineBuilder.Append((char)_buffer[i]);
                }

                // If we stopped at \r at end of buffer, back up one
                if (_bufferPos > 0 && _bufferPos >= _bufferLength && _buffer[_bufferPos - 1] == '\r')
                {
                    lineBuilder.Length--; // Remove the \r we just added
                    _bufferPos--;
                }
            }
        }

        /// <summary>
        /// Reads a chunk of raw bytes for body content.
        /// Returns up to bufferSize bytes from the stream.
        /// Returns empty on EOF.
        /// </summary>
        public async ValueTask<ReadOnlyMemory<byte>> ReadChunkAsync(CancellationToken cancellationToken)
        {
            // If there's buffered data from line reading, return it first
            if (_bufferPos < _bufferLength)
            {
                int available = _bufferLength - _bufferPos;
                var chunk = new ReadOnlyMemory<byte>(_buffer, _bufferPos, available);
                _bufferPos = _bufferLength; // Mark as consumed
                return chunk;
            }

            // Fill buffer with fresh data
            await FillBufferAsync(cancellationToken);

            if (_bufferLength == 0)
                return ReadOnlyMemory<byte>.Empty; // EOF

            var result = new ReadOnlyMemory<byte>(_buffer, 0, _bufferLength);
            _bufferPos = _bufferLength; // Mark as consumed
            return result;
        }

        /// <summary>
        /// Fills the internal buffer from the stream.
        /// </summary>
        private async ValueTask FillBufferAsync(CancellationToken cancellationToken)
        {
            _bufferLength = await _stream.ReadAsync(new Memory<byte>(_buffer, 0, _bufferSize), cancellationToken);
            _bufferPos = 0;
        }

        /// <summary>
        /// Gets any remaining buffered data without reading from stream.
        /// </summary>
        public ReadOnlySpan<byte> GetBufferedData()
        {
            if (_bufferPos >= _bufferLength)
                return ReadOnlySpan<byte>.Empty;
            return new ReadOnlySpan<byte>(_buffer, _bufferPos, _bufferLength - _bufferPos);
        }

        /// <summary>
        /// Advances the buffer position by the specified number of bytes.
        /// </summary>
        public void Advance(int bytes)
        {
            _bufferPos += bytes;
            if (_bufferPos > _bufferLength)
                _bufferPos = _bufferLength;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                ArrayPool<byte>.Shared.Return(_buffer);
                _disposed = true;
            }
        }
    }
}
