using System;
using System.IO;
using SharpCompress.Common.Rar.Headers;
#if !Rar2017_64bit
using size_t = System.UInt32;
#else
using nint = System.Int64;
using nuint = System.UInt64;
using size_t = System.UInt64;
#endif

namespace SharpCompress.Compressors.Rar.UnpackV2017;

internal partial class Unpack : IRarUnpack
{
    private FileHeader fileHeader;
    private Stream readStream;
    private Stream writeStream;
    private bool _isObfuscated;
    private static readonly byte[] ObfuscationKey = { 0xB0, 0x41, 0xC2, 0xCE };

    private void _UnpackCtor()
    {
        for (var i = 0; i < AudV.Length; i++)
        {
            AudV[i] = new AudioVariables();
        }
    }

    private int UnpIO_UnpRead(byte[] buf, int offset, int count) =>
        // NOTE: caller has logic to check for -1 for error we throw instead.
        readStream.Read(buf, offset, count);

    private async System.Threading.Tasks.Task<int> UnpIO_UnpReadAsync(
        byte[] buf,
        int offset,
        int count,
        System.Threading.CancellationToken cancellationToken = default
    ) =>
        // NOTE: caller has logic to check for -1 for error we throw instead.
        await readStream.ReadAsync(buf, offset, count, cancellationToken).ConfigureAwait(false);

    private void UnpIO_UnpWrite(byte[] buf, size_t offset, uint count) =>
        writeStream.Write(buf, checked((int)offset), checked((int)count));

    private async System.Threading.Tasks.Task UnpIO_UnpWriteAsync(
        byte[] buf,
        size_t offset,
        uint count,
        System.Threading.CancellationToken cancellationToken = default
    ) =>
        await writeStream
            .WriteAsync(buf, checked((int)offset), checked((int)count), cancellationToken)
            .ConfigureAwait(false);

    public void DoUnpack(FileHeader fileHeader, Stream readStream, Stream writeStream)
    {
        // as of 12/2017 .NET limits array indexing to using a signed integer
        // MaxWinSize causes unpack to use a fragmented window when the file
        // window size exceeds MaxWinSize
        // uggh, that's not how this variable is used, it's the size of the currently allocated window buffer
        //x MaxWinSize = ((uint)int.MaxValue) + 1;

        // may be long.MaxValue which could indicate unknown size (not present in header)
        DestUnpSize = fileHeader.UncompressedSize;
        this.fileHeader = fileHeader;
        this.readStream = readStream;
        this.writeStream = writeStream;
        _isObfuscated = false;
        if (!fileHeader.IsStored)
        {
            Init(fileHeader.WindowSize, fileHeader.IsSolid);
        }
        Suspended = false;
        DoUnpack();
    }

    public async System.Threading.Tasks.Task DoUnpackAsync(
        FileHeader fileHeader,
        Stream readStream,
        Stream writeStream,
        System.Threading.CancellationToken cancellationToken = default
    )
    {
        DestUnpSize = fileHeader.UncompressedSize;
        this.fileHeader = fileHeader;
        this.readStream = readStream;
        this.writeStream = writeStream;
        _isObfuscated = false;
        if (!fileHeader.IsStored)
        {
            Init(fileHeader.WindowSize, fileHeader.IsSolid);
        }
        Suspended = false;
        await DoUnpackAsync(cancellationToken).ConfigureAwait(false);
    }

    public void DoUnpack()
    {
        if (fileHeader.IsStored)
        {
            UnstoreFile();
        }
        else
        {
            DoUnpack(fileHeader.CompressionAlgorithm, fileHeader.IsSolid);
        }
    }

    public async System.Threading.Tasks.Task DoUnpackAsync(
        System.Threading.CancellationToken cancellationToken = default
    )
    {
        if (fileHeader.IsStored)
        {
            await UnstoreFileAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // TODO: When compression methods are converted to async, call them here
            // For now, fall back to synchronous version
            await DoUnpackAsync(
                    fileHeader.CompressionAlgorithm,
                    fileHeader.IsSolid,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
    }

    private void UnstoreFile()
    {
        Span<byte> b = stackalloc byte[(int)Math.Min(0x10000, DestUnpSize)];
        do
        {
            var n = readStream.Read(b);
            if (n == 0)
            {
                break;
            }

            if (DestUnpSize == fileHeader.UncompressedSize)
            {
                if (n >= 4)
                {
                    if (b[0] == 0xAA && b[1] == 0x04 && b[2] == 0x1D && b[3] == 0x6D)
                    {
                        _isObfuscated = true;
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss} INF] [SharpCompress] Obfuscation DETECTED for Stored file. Sig: {b[0]:X2} {b[1]:X2} {b[2]:X2} {b[3]:X2}");
                    }
                    else
                    {
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss} INF] [SharpCompress] Standard Stored file. Sig: {b[0]:X2} {b[1]:X2} {b[2]:X2} {b[3]:X2}");
                    }
                }
                else if (n > 0)
                {
                     Console.WriteLine($"[{DateTime.Now:HH:mm:ss} INF] [SharpCompress] Short read at start of Stored file. Bytes: {n}. Data: {BitConverter.ToString(b.Slice(0, n).ToArray())}");
                }
            }

            if (_isObfuscated)
            {
                var offset = fileHeader.UncompressedSize - DestUnpSize;
                for (int i = 0; i < n; i++)
                {
                    b[i] ^= ObfuscationKey[(offset + i) % 4];
                }
            }

            writeStream.Write(b.Slice(0, n));
            DestUnpSize -= n;
        } while (!Suspended);
    }

    private async System.Threading.Tasks.Task UnstoreFileAsync(
        System.Threading.CancellationToken cancellationToken = default
    )
    {
        var buffer = new byte[(int)Math.Min(0x10000, DestUnpSize)];
        do
        {
            var n = await readStream
                .ReadAsync(buffer, 0, buffer.Length, cancellationToken)
                .ConfigureAwait(false);
            if (n == 0)
            {
                break;
            }

            if (DestUnpSize == fileHeader.UncompressedSize)
            {
                if (n >= 4)
                {
                    if (buffer[0] == 0xAA && buffer[1] == 0x04 && buffer[2] == 0x1D && buffer[3] == 0x6D)
                    {
                        _isObfuscated = true;
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss} INF] [SharpCompress] Obfuscation DETECTED for Stored file. Sig: {buffer[0]:X2} {buffer[1]:X2} {buffer[2]:X2} {buffer[3]:X2}");
                    }
                    else
                    {
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss} INF] [SharpCompress] Standard Stored file. Sig: {buffer[0]:X2} {buffer[1]:X2} {buffer[2]:X2} {buffer[3]:X2}");
                    }
                }
                else if (n > 0)
                {
                     Console.WriteLine($"[{DateTime.Now:HH:mm:ss} INF] [SharpCompress] Short read at start of Stored file. Bytes: {n}. Data: {BitConverter.ToString(buffer, 0, n)}");
                }
            }

            if (_isObfuscated)
            {
                var offset = fileHeader.UncompressedSize - DestUnpSize;
                for (int i = 0; i < n; i++)
                {
                    buffer[i] ^= ObfuscationKey[(offset + i) % 4];
                }
            }

            await writeStream.WriteAsync(buffer, 0, n, cancellationToken).ConfigureAwait(false);
            DestUnpSize -= n;
        } while (!Suspended);
    }

    public bool Suspended { get; set; }

    public long DestSize => DestUnpSize;

    public int Char
    {
        get
        {
            // TODO: coderb: not sure where the "MAXSIZE-30" comes from, ported from V1 code
            if (InAddr > MAX_SIZE - 30)
            {
                UnpReadBuf();
            }
            return InBuf[InAddr++];
        }
    }

    public int PpmEscChar
    {
        get => PPMEscChar;
        set => PPMEscChar = value;
    }

    public static byte[] EnsureCapacity(byte[] array, int length) =>
        array.Length < length ? new byte[length] : array;
}
