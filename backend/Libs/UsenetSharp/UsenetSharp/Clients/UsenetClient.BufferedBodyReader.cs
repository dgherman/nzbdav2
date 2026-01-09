using System;
using System.Buffers;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace UsenetSharp.Clients;

public partial class UsenetClient
{
    /// <summary>
    /// Custom buffered reader for NNTP body content that reads raw chunks instead of lines.
    /// This avoids the overhead of StreamReader.ReadLineAsync() and string allocations.
    /// Phase 3 optimization: Chunk-based reading like NZBGet.
    /// </summary>
    private class BufferedBodyReader : IDisposable
    {
        private readonly Stream _stream;
        private readonly byte[] _buffer;
        private readonly int _bufferSize;
        private int _bufferPos;
        private int _bufferLength;
        private bool _disposed;

        public BufferedBodyReader(Stream stream, int bufferSize = 131072) // 128KB default
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
            _bufferSize = bufferSize;
            _buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
            _bufferPos = 0;
            _bufferLength = 0;
        }

        /// <summary>
        /// Reads a chunk of data from the stream into the internal buffer.
        /// Returns the buffered data as ReadOnlyMemory.
        /// Returns empty memory on EOF.
        /// </summary>
        public async ValueTask<ReadOnlyMemory<byte>> ReadChunkAsync(CancellationToken cancellationToken)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(BufferedBodyReader));

            // Read from stream into buffer
            _bufferLength = await _stream.ReadAsync(new Memory<byte>(_buffer, 0, _bufferSize), cancellationToken);
            _bufferPos = 0;

            if (_bufferLength == 0)
                return ReadOnlyMemory<byte>.Empty; // EOF

            return new ReadOnlyMemory<byte>(_buffer, 0, _bufferLength);
        }

        /// <summary>
        /// Gets the remaining data in the current buffer without reading from stream.
        /// Useful for handling data that spans multiple chunks.
        /// </summary>
        public ReadOnlyMemory<byte> GetRemainingData()
        {
            if (_bufferPos >= _bufferLength)
                return ReadOnlyMemory<byte>.Empty;

            return new ReadOnlyMemory<byte>(_buffer, _bufferPos, _bufferLength - _bufferPos);
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
