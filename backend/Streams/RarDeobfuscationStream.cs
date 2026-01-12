using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace NzbWebDAV.Streams;

public class RarDeobfuscationStream : Stream
{
    private readonly Stream _innerStream;
    private readonly byte[] _key = { 0xB0, 0x41, 0xC2, 0xCE };
    private bool? _isObfuscated;
    private long _position;
    private readonly byte[] _sigBuffer = new byte[4];
    private int _sigBufferOffset = 0;

    public RarDeobfuscationStream(Stream innerStream)
    {
        _innerStream = innerStream;
    }

    public override bool CanRead => _innerStream.CanRead;
    public override bool CanSeek => _innerStream.CanSeek;
    public override bool CanWrite => false;
    public override long Length => _innerStream.Length;

    public override long Position
    {
        get => _position;
        set
        {
            _position = value;
            _innerStream.Position = value;
        }
    }

    public override void Flush() => _innerStream.Flush();

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = _innerStream.Read(buffer, offset, count);
        ProcessBuffer(buffer, offset, read, _position);
        _position += read;
        return read;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var read = await _innerStream.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
        ProcessBuffer(buffer, offset, read, _position);
        _position += read;
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var newPos = _innerStream.Seek(offset, origin);
        _position = newPos;
        return newPos;
    }

    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private void ProcessBuffer(byte[] buffer, int offset, int count, long streamPosition)
    {
        if (count == 0) return;

        // Detection logic (only checks start of file)
        if (_isObfuscated == null)
        {
            // If we are reading from position 0, or we are currently filling our signature buffer
            if (streamPosition < 4)
            {
                // Fill signature buffer
                int bytesToCopy = Math.Min(count, 4 - (int)streamPosition);
                // Ensure we only copy from the part of the buffer that corresponds to the start of the stream
                // This assumes the read actually started at streamPosition
                Buffer.BlockCopy(buffer, offset, _sigBuffer, (int)streamPosition, bytesToCopy);
                
                // If we have 4 bytes now, we can decide
                if (streamPosition + bytesToCopy >= 4)
                {
                    if (_sigBuffer[0] == 0xAA && _sigBuffer[1] == 0x04 && _sigBuffer[2] == 0x1D && _sigBuffer[3] == 0x6D)
                    {
                        _isObfuscated = true;
                        Log.Information("[RarDeobfuscationStream] Obfuscation DETECTED at start of file. Applying XOR.");
                    }
                    else
                    {
                        _isObfuscated = false;
                        Log.Information("[RarDeobfuscationStream] No obfuscation detected. Signature: {Sig}", 
                            BitConverter.ToString(_sigBuffer));
                    }
                }
            }
            else
            {
                // We started reading past the header? 
                // In a stateless stream wrapper, we can't reliably detect here.
                // But usually the first read is the header.
                _isObfuscated = false;
            }
        }

        if (_isObfuscated == true)
        {
            for (int i = 0; i < count; i++)
            {
                buffer[offset + i] ^= _key[(streamPosition + i) % 4];
            }
        }
    }
}

