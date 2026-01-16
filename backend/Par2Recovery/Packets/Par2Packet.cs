using System.Runtime.InteropServices;

namespace NzbWebDAV.Par2Recovery.Packets
{
    /// <summary>
    /// Implements the basic Read mechanism, passing the body bytes to any child class.
    /// </summary>
    public class Par2Packet
    {
        public Par2PacketHeader Header { get; protected set; }

        public Par2Packet(Par2PacketHeader header)
        {
            Header = header;
        }

        /// <summary>
        /// Shared buffer for discarding bytes when seeking isn't supported.
        /// Using a larger buffer (64KB) reduces the number of read calls needed.
        /// </summary>
        private static readonly byte[] DiscardBuffer = new byte[64 * 1024];

        public async Task ReadAsync(Stream stream, CancellationToken cancellationToken = default)
        {
            // Determine the length of the body as the given packet length, minus the length of the header.
            var bodyLength = Header.PacketLength - (ulong)Marshal.SizeOf<Par2PacketHeader>();

            // Optimization: If this is the base Par2Packet class, we don't need the body data.
            // We can skip over it to avoid downloading unnecessary data (often GBs for recovery slices).
            if (GetType() == typeof(Par2Packet))
            {
                if (stream.CanSeek)
                {
                    // Fast path: seek past the body
                    stream.Seek((long)bodyLength, SeekOrigin.Current);
                }
                else
                {
                    // Fallback: read and discard in chunks (for non-seekable streams like BufferedSegmentStream)
                    var remaining = (long)bodyLength;
                    while (remaining > 0 && !cancellationToken.IsCancellationRequested)
                    {
                        var toRead = (int)Math.Min(remaining, DiscardBuffer.Length);
                        var read = await stream.ReadAsync(DiscardBuffer.AsMemory(0, toRead), cancellationToken).ConfigureAwait(false);
                        if (read == 0) break; // EOF
                        remaining -= read;
                    }
                }
                return;
            }

            // Read the calculated number of bytes from the stream.
            var body = new byte[bodyLength];
            await stream.ReadExactlyAsync(body.AsMemory(0, (int)bodyLength), cancellationToken).ConfigureAwait(false);

            // Pass the body to the further implementation for parsing.
            ParseBody(body);
        }

        protected virtual void ParseBody(byte[] body)
        {
            // intentionally left blank
        }
    }
}