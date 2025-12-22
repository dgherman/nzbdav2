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

        public async Task ReadAsync(Stream stream)
        {
            // Determine the length of the body as the given packet length, minus the length of the header.
            var bodyLength = Header.PacketLength - (ulong)Marshal.SizeOf<Par2PacketHeader>();

            // Optimization: If this is the base Par2Packet class, we don't need the body data.
            // We can just skip over it using Seek to avoid downloading unnecessary data (often GBs for recovery slices).
            if (GetType() == typeof(Par2Packet) && stream.CanSeek)
            {
                stream.Seek((long)bodyLength, SeekOrigin.Current);
                return;
            }

            // Read the calculated number of bytes from the stream.
            var body = new byte[bodyLength];
            await stream.ReadExactlyAsync(body.AsMemory(0, (int)bodyLength)).ConfigureAwait(false);

            // Pass the body to the further implementation for parsing.
            ParseBody(body);
        }

        protected virtual void ParseBody(byte[] body)
        {
            // intentionally left blank
        }
    }
}