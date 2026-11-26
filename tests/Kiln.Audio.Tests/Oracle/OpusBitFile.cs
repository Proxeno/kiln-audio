using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace Kiln.Audio.Tests.Oracle;

/// <summary>
/// Reads the packet stream emitted by <c>opus_demo</c> and shipped as the RFC 8251
/// <c>testvectorNN.bit</c> files.
/// </summary>
/// <remarks>
/// Each record is a 4-byte big-endian packet length, a 4-byte big-endian encoder final range,
/// then that many packet bytes. There is no file header and no trailing record.
/// </remarks>
internal static class OpusBitFile
{
    /// <summary>Size of the length-plus-range record header in bytes.</summary>
    internal const int HeaderSize = 8;

    /// <summary>Reads every packet record in <paramref name="path"/>.</summary>
    /// <param name="path">Path to a <c>.bit</c> file.</param>
    /// <returns>The packets in file order, each with its encoder final range.</returns>
    /// <exception cref="InvalidDataException">The file ends inside a record header or packet.</exception>
    internal static IEnumerable<(byte[] Packet, uint FinalRange)> Read(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        int offset = 0;
        while (offset < bytes.Length)
        {
            if (bytes.Length - offset < HeaderSize)
            {
                throw new InvalidDataException(
                    FormattableString.Invariant($"Truncated record header at byte {offset} of '{path}'."));
            }

            int length = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(offset, sizeof(int)));
            uint finalRange = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset + sizeof(int), sizeof(uint)));
            offset += HeaderSize;

            if (length < 0 || length > bytes.Length - offset)
            {
                throw new InvalidDataException(
                    FormattableString.Invariant(
                        $"Record at byte {offset - HeaderSize} of '{path}' announces {length} bytes but only {bytes.Length - offset} remain."));
            }

            byte[] packet = bytes.AsSpan(offset, length).ToArray();
            offset += length;
            yield return (packet, finalRange);
        }
    }
}
