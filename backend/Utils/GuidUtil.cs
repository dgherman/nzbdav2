using System.Security.Cryptography;
using System.Text;

namespace NzbWebDAV.Utils;

public static class GuidUtil
{
    // Generates a guid using a cryptographically secure RNG
    public static Guid GenerateSecureGuid()
    {
        byte[] bytes = new byte[16];
        RandomNumberGenerator.Fill(bytes);

        // Set version to 4 (random)
        bytes[7] = (byte)((bytes[7] & 0x0F) | 0x40);
        // Set variant to RFC 4122
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);

        return new Guid(bytes);
    }

    /// <summary>
    /// Creates a deterministic GUID based on a namespace and a name.
    /// Uses SHA-1 as per RFC 4122 (Version 5).
    /// </summary>
    public static Guid CreateDeterministic(Guid namespaceId, string name)
    {
        var namespaceBytes = namespaceId.ToByteArray();
        SwapByteOrder(namespaceBytes);

        var nameBytes = Encoding.UTF8.GetBytes(name);
        var combinedBytes = new byte[namespaceBytes.Length + nameBytes.Length];
        Buffer.BlockCopy(namespaceBytes, 0, combinedBytes, 0, namespaceBytes.Length);
        Buffer.BlockCopy(nameBytes, 0, combinedBytes, namespaceBytes.Length, nameBytes.Length);

        var hash = SHA1.HashData(combinedBytes);
        var newGuid = new byte[16];
        Buffer.BlockCopy(hash, 0, newGuid, 0, 16);

        // set version
        newGuid[7] = (byte)((newGuid[7] & 0x0F) | 0x50);
        // set variant
        newGuid[8] = (byte)((newGuid[8] & 0x3F) | 0x80);

        SwapByteOrder(newGuid);
        return new Guid(newGuid);
    }

    private static void SwapByteOrder(byte[] guid)
    {
        Swap(guid, 0, 3);
        Swap(guid, 1, 2);
        Swap(guid, 4, 5);
        Swap(guid, 6, 7);
    }

    private static void Swap(byte[] b, int i, int j)
    {
        (b[i], b[j]) = (b[j], b[i]);
    }
}