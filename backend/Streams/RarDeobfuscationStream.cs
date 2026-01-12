using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace NzbWebDAV.Streams;

public class RarDeobfuscationStream : Stream
{
    private readonly Stream _innerStream;
    private readonly bool _leaveOpen;
    private readonly byte[] _defaultKey = { 0xB0, 0x41, 0xC2, 0xCE };
    private byte[] _key;
    private bool? _isObfuscated;
    private long _position;

    // Known file signatures to detect obfuscation
    private static readonly byte[] MkvSignature = { 0x1A, 0x45, 0xDF, 0xA3 };  // EBML/MKV
    private static readonly byte[] Mp4Signature = { 0x00, 0x00, 0x00 };         // MP4/MOV (partial, 4th byte varies)
    private static readonly byte[] AviSignature = { 0x52, 0x49, 0x46, 0x46 };   // RIFF/AVI

    public RarDeobfuscationStream(Stream innerStream, byte[]? key = null, bool leaveOpen = false)
    {
        _innerStream = innerStream;
        _key = key ?? _defaultKey;
        _leaveOpen = leaveOpen;
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

        // Detect obfuscation on first read
        if (_isObfuscated == null && _position == 0 && read >= 4)
        {
            DetectObfuscation(buffer, offset, read);
        }

        if (_isObfuscated == true)
        {
            for (int i = 0; i < read; i++)
            {
                buffer[offset + i] ^= _key[(_position + i) % _key.Length];
            }
        }

        _position += read;
        return read;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var read = await _innerStream.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);

        // Detect obfuscation on first read
        if (_isObfuscated == null && _position == 0 && read >= 4)
        {
            DetectObfuscation(buffer, offset, read);
        }

        if (_isObfuscated == true)
        {
            for (int i = 0; i < read; i++)
            {
                buffer[offset + i] ^= _key[(_position + i) % _key.Length];
            }
        }

        _position += read;
        return read;
    }

    private void DetectObfuscation(byte[] buffer, int offset, int read)
    {
        var first4 = new byte[] { buffer[offset], buffer[offset + 1], buffer[offset + 2], buffer[offset + 3] };

        // Check 1: Standard obfuscation signature (MKV XOR'd with default key B0 41 C2 CE)
        if (first4[0] == 0xAA && first4[1] == 0x04 && first4[2] == 0x1D && first4[3] == 0x6D)
        {
            _isObfuscated = true;
            _key = _defaultKey;
            Log.Information("[RarDeobfuscationStream] Standard obfuscation DETECTED. First 4 bytes: AA-04-1D-6D, using key B0-41-C2-CE");
            return;
        }

        // Check 2: Already a valid file signature (no obfuscation needed)
        if (IsKnownSignature(first4))
        {
            _isObfuscated = false;
            Log.Debug("[RarDeobfuscationStream] Valid file signature found. No obfuscation. First 4 bytes: {Sig}", BitConverter.ToString(first4));
            return;
        }

        // Check 3: Try to discover XOR key by testing against known file signatures
        if (read >= 8)
        {
            var discoveredKey = TryDiscoverXorKey(buffer, offset, read);
            if (discoveredKey != null)
            {
                _isObfuscated = true;
                _key = discoveredKey;
                Log.Information("[RarDeobfuscationStream] Non-standard obfuscation DETECTED. First 4 bytes: {Sig}, discovered key: {Key}",
                    BitConverter.ToString(first4), BitConverter.ToString(discoveredKey));
                return;
            }
        }

        // No obfuscation detected
        _isObfuscated = false;
        Log.Debug("[RarDeobfuscationStream] No obfuscation detected. First 4 bytes: {Sig}", BitConverter.ToString(first4));
    }

    private static bool IsKnownSignature(byte[] first4)
    {
        // MKV/EBML
        if (first4[0] == 0x1A && first4[1] == 0x45 && first4[2] == 0xDF && first4[3] == 0xA3)
            return true;

        // AVI/RIFF
        if (first4[0] == 0x52 && first4[1] == 0x49 && first4[2] == 0x46 && first4[3] == 0x46)
            return true;

        // MP4/MOV (ftyp atom)
        if (first4[0] == 0x00 && first4[1] == 0x00 && first4[2] == 0x00 && (first4[3] >= 0x14 && first4[3] <= 0x28))
            return true;

        return false;
    }

    private static byte[]? TryDiscoverXorKey(byte[] buffer, int offset, int read)
    {
        // Try MKV signature first (most common for modern releases)
        var mkvKey = TryKeyForSignature(buffer, offset, read, MkvSignature, ValidateMkv);
        if (mkvKey != null) return mkvKey;

        // Try AVI/RIFF signature
        var aviKey = TryKeyForSignature(buffer, offset, read, AviSignature, ValidateAvi);
        if (aviKey != null) return aviKey;

        // Try MP4 - this is trickier because first 4 bytes are atom size, then "ftyp"
        var mp4Key = TryDiscoverMp4Key(buffer, offset, read);
        if (mp4Key != null) return mp4Key;

        return null;
    }

    private static byte[]? TryKeyForSignature(byte[] buffer, int offset, int read, byte[] signature, Func<byte[], bool> validator)
    {
        // Calculate what 4-byte XOR key would produce this signature
        var discoveredKey = new byte[4];
        for (int i = 0; i < 4; i++)
        {
            discoveredKey[i] = (byte)(buffer[offset + i] ^ signature[i]);
        }

        // Apply key to decode more bytes for validation
        var decoded = new byte[Math.Min(16, read)];
        for (int i = 0; i < decoded.Length; i++)
        {
            decoded[i] = (byte)(buffer[offset + i] ^ discoveredKey[i % 4]);
        }

        // Validate the decoded content looks correct for this format
        if (validator(decoded))
        {
            return discoveredKey;
        }

        return null;
    }

    private static bool ValidateMkv(byte[] decoded)
    {
        // MKV: starts with 1A 45 DF A3, followed by EBML structure
        // Byte 4 is typically 0xA3 (DocType element) or small value, byte 5 is 0x42
        if (decoded.Length < 6) return false;
        if (decoded[0] != 0x1A || decoded[1] != 0x45 || decoded[2] != 0xDF || decoded[3] != 0xA3)
            return false;

        // Additional validation: byte 5 should be 0x42 (common in MKV EBML headers)
        return decoded[5] == 0x42 || (decoded[4] < 0x10);
    }

    private static bool ValidateAvi(byte[] decoded)
    {
        // AVI: starts with "RIFF" (52 49 46 46), followed by file size (4 bytes), then "AVI " (41 56 49 20)
        if (decoded.Length < 12) return false;
        if (decoded[0] != 0x52 || decoded[1] != 0x49 || decoded[2] != 0x46 || decoded[3] != 0x46)
            return false;

        // Bytes 8-11 should be "AVI " or "WAVE"
        return (decoded[8] == 0x41 && decoded[9] == 0x56 && decoded[10] == 0x49 && decoded[11] == 0x20) ||
               (decoded[8] == 0x57 && decoded[9] == 0x41 && decoded[10] == 0x56 && decoded[11] == 0x45);
    }

    private static byte[]? TryDiscoverMp4Key(byte[] buffer, int offset, int read)
    {
        // MP4/MOV: First 4 bytes are atom size, bytes 4-7 are "ftyp" (66 74 79 70)
        // We need at least 8 bytes to validate
        if (read < 8) return null;

        // We know bytes 4-7 should decode to "ftyp" (0x66, 0x74, 0x79, 0x70)
        byte[] ftypSignature = { 0x66, 0x74, 0x79, 0x70 };

        // Calculate key from bytes 4-7
        var discoveredKey = new byte[4];
        for (int i = 0; i < 4; i++)
        {
            discoveredKey[i] = (byte)(buffer[offset + 4 + i] ^ ftypSignature[i]);
        }

        // Apply key to decode all 8 bytes
        var decoded = new byte[Math.Min(16, read)];
        for (int i = 0; i < decoded.Length; i++)
        {
            decoded[i] = (byte)(buffer[offset + i] ^ discoveredKey[i % 4]);
        }

        // Validate: bytes 4-7 should be "ftyp", bytes 0-3 should be a reasonable atom size
        if (decoded[4] == 0x66 && decoded[5] == 0x74 && decoded[6] == 0x79 && decoded[7] == 0x70)
        {
            // Check atom size is reasonable (typically 0x14 to 0x28 for ftyp atom)
            int atomSize = (decoded[0] << 24) | (decoded[1] << 16) | (decoded[2] << 8) | decoded[3];
            if (atomSize >= 0x10 && atomSize <= 0x100)
            {
                return discoveredKey;
            }
        }

        return null;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var newPos = _innerStream.Seek(offset, origin);
        _position = newPos;
        return newPos;
    }

    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_leaveOpen)
        {
            _innerStream.Dispose();
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!_leaveOpen)
        {
            await _innerStream.DisposeAsync().ConfigureAwait(false);
        }
        GC.SuppressFinalize(this);
    }
}