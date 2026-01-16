using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using NzbWebDAV.Par2Recovery.Packets;
using Serilog;

namespace NzbWebDAV.Par2Recovery
{
    public class Par2
    {
        internal static readonly Regex ParVolume = new(
            @"(.+)\.vol[0-9]{1,10}\+[0-9]{1,10}\.par2$",
            RegexOptions.IgnoreCase
        );

        private const string Par2PacketHeaderMagic = "PAR2\0PKT";

        /// <summary>
        /// Number of consecutive non-FileDesc packets to see before stopping.
        /// FileDesc packets are typically grouped at the beginning of Par2 files.
        /// Once we see several non-FileDesc packets in a row, we've likely found all descriptors.
        /// </summary>
        private const int MaxConsecutiveNonFileDesc = 5;

        /// <summary>
        /// Reads file descriptions from a Par2 stream.
        /// </summary>
        /// <param name="stream">The Par2 file stream</param>
        /// <param name="ct">Cancellation token</param>
        /// <param name="maxDescriptors">Optional: Stop after finding this many descriptors (early termination optimization)</param>
        public static async IAsyncEnumerable<FileDesc> ReadFileDescriptions
        (
            Stream stream,
            CancellationToken ct = default,
            int? maxDescriptors = null
        )
        {
            Par2Packet? packet = null;
            var iterationCount = 0;
            var lastPosition = stream.Position;
            var descriptorsFound = 0;
            var consecutiveNonFileDesc = 0;

            Log.Debug("[Par2] Starting ReadFileDescriptions. Stream length: {Length}, Initial position: {Position}, MaxDescriptors: {Max}",
                stream.Length, stream.Position, maxDescriptors?.ToString() ?? "unlimited");

            while (stream.Position < stream.Length && !ct.IsCancellationRequested)
            {
                // Early termination: stop if we've found enough descriptors
                if (maxDescriptors.HasValue && descriptorsFound >= maxDescriptors.Value)
                {
                    Log.Information("[Par2] Early termination: Found {Count} descriptors (max: {Max}). Skipping remaining {Remaining} bytes",
                        descriptorsFound, maxDescriptors.Value, stream.Length - stream.Position);
                    yield break;
                }

                // Smart early termination: if we've found some descriptors and then see several
                // non-FileDesc packets in a row, we've likely passed the FileDesc section
                if (descriptorsFound > 0 && consecutiveNonFileDesc >= MaxConsecutiveNonFileDesc)
                {
                    Log.Information("[Par2] Smart early termination: Found {Count} descriptors, then {NonFileDesc} consecutive non-FileDesc packets. Skipping remaining {Remaining} bytes",
                        descriptorsFound, consecutiveNonFileDesc, stream.Length - stream.Position);
                    yield break;
                }

                iterationCount++;
                var currentPosition = stream.Position;

                // Detect infinite loop - if position hasn't changed in 100 iterations
                if (iterationCount > 100 && currentPosition == lastPosition)
                {
                    Log.Error("[Par2] INFINITE LOOP DETECTED! Position stuck at {Position} after {Iterations} iterations. Stream length: {Length}",
                        currentPosition, iterationCount, stream.Length);
                    throw new InvalidOperationException($"PAR2 reading stuck in infinite loop at position {currentPosition}");
                }

                if (iterationCount % 10 == 0)
                {
                    Log.Debug("[Par2] ReadFileDescriptions iteration {Iteration}: Position {Position}/{Length}, Found {Count} descriptors",
                        iterationCount, currentPosition, stream.Length, descriptorsFound);
                }

                try
                {
                    Log.Debug("[Par2] Reading packet at position {Position}", currentPosition);
                    packet = await ReadPacketAsync(stream, ct).ConfigureAwait(false);
                    Log.Debug("[Par2] Read packet of type {PacketType}, stream now at position {Position}",
                        packet.GetType().Name, stream.Position);
                }
                catch (Exception e)
                {
                    Log.Warning("[Par2] Failed to read par2 packet at position {Position}: {Message}", currentPosition, e.Message);
                    yield break;
                }

                if (packet is FileDesc newFile)
                {
                    descriptorsFound++;
                    consecutiveNonFileDesc = 0; // Reset counter when we find a FileDesc
                    Log.Debug("[Par2] Found FileDesc packet #{Count}: {FileName}", descriptorsFound, newFile.FileName);
                    yield return newFile;
                }
                else
                {
                    consecutiveNonFileDesc++;
                }

                lastPosition = currentPosition;
            }

            Log.Debug("[Par2] ReadFileDescriptions completed. Total iterations: {Iterations}, Descriptors found: {Count}, Final position: {Position}/{Length}",
                iterationCount, descriptorsFound, stream.Position, stream.Length);
        }

        private static async Task<Par2Packet> ReadPacketAsync(Stream stream, CancellationToken cancellationToken = default)
        {
            var startPosition = stream.Position;
            Log.Debug("[Par2] ReadPacketAsync starting at position {Position}", startPosition);

            // Read a Packet Header.
            var header = await ReadStructAsync<Par2PacketHeader>(stream, cancellationToken).ConfigureAwait(false);
            Log.Debug("[Par2] Read packet header, stream now at {Position}. Packet length: {PacketLength}", stream.Position, header.PacketLength);

            // Test if the magic constant matches.
            var magic = Encoding.ASCII.GetString(header.Magic);
            if (!Par2PacketHeaderMagic.Equals(magic))
            {
                Log.Error("[Par2] Invalid Magic Constant at position {Position}. Expected: {Expected}, Got: {Got}",
                    startPosition, Par2PacketHeaderMagic, magic);
                throw new ApplicationException("Invalid Magic Constant");
            }

            // Determine which type of packet we have.
            var packetType = Encoding.ASCII.GetString(header.PacketType);
            Log.Debug("[Par2] Packet type: {PacketType}", packetType);

            Par2Packet result;
            switch (packetType)
            {
                case FileDesc.PacketType:
                    result = new FileDesc(header);
                    Log.Debug("[Par2] Creating FileDesc packet");
                    break;
                default:
                    result = new Par2Packet(header);
                    Log.Debug("[Par2] Creating generic Par2Packet");
                    break;
            }

            // Let the packet type parse more of the stream as needed.
            var beforeReadAsync = stream.Position;
            await result.ReadAsync(stream, cancellationToken).ConfigureAwait(false);
            Log.Debug("[Par2] Packet ReadAsync completed. Position before: {Before}, after: {After}, bytes read: {BytesRead}",
                beforeReadAsync, stream.Position, stream.Position - beforeReadAsync);

            return result;
        }

        /// <summary>
        /// Read a struct as binary from a stream.
        /// </summary>
        /// <typeparam name="T">The struct to read.</typeparam>
        /// <param name="stream">The stream to read from.</param>
        /// <param name="cancellationToken">Cancellation token to cancel the read operation.</param>
        /// <returns>The struct with values read from the stream.</returns>
        private static async Task<T> ReadStructAsync<T>(Stream stream, CancellationToken cancellationToken = default) where T : struct
        {
            var size = Marshal.SizeOf<T>();
            var buffer = new byte[size];
            await stream.ReadExactlyAsync(buffer.AsMemory(0, size), cancellationToken).ConfigureAwait(false);
            var pinnedBuffer = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                var structure = Marshal.PtrToStructure<T>(pinnedBuffer.AddrOfPinnedObject());
                return structure;
            }
            finally
            {
                pinnedBuffer.Free();
            }
        }

        private static T ReadStruct<T>(byte[] bytes) where T : struct
        {
            var size = Marshal.SizeOf<T>();
            if (bytes.Length < size)
            {
                throw new ArgumentException("Byte array is too short to represent the struct.", nameof(bytes));
            }

            var pinnedBuffer = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try
            {
                var structure = Marshal.PtrToStructure<T>(pinnedBuffer.AddrOfPinnedObject());
                return structure;
            }
            finally
            {
                pinnedBuffer.Free();
            }
        }

        public static bool HasPar2MagicBytes(byte[] bytes)
        {
            try
            {
                var header = ReadStruct<Par2PacketHeader>(bytes);
                var magic = Encoding.ASCII.GetString(header.Magic);
                return Par2PacketHeaderMagic.Equals(magic);
            }
            catch (Exception e)
            {
                return false;
            }
        }
    }
}