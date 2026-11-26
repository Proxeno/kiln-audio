using System.Buffers.Binary;
using System.IO;

namespace Kiln.Audio.Benchmarks;

/// <summary>
/// Loads the RFC 8251 <c>.bit</c> packet streams into memory for the decode benchmarks.
/// </summary>
/// <remarks>
/// The benchmark project does not reference the test project, so this reproduces the small
/// amount of locating and record parsing that the conformance tests use. The vectors are
/// fetched, never committed, by <c>scripts/fetch-test-vectors.sh</c>.
/// </remarks>
internal static class TestVectorData
{
    /// <summary>Size of a <c>.bit</c> record header: a packet length and an encoder final range.</summary>
    private const int HeaderSize = 8;

    /// <summary>Returns the path to <c>testvectorNN.bit</c>, searching upward from the build output.</summary>
    /// <param name="vector">Vector number, 1 to 12.</param>
    /// <exception cref="FileNotFoundException">The vectors have not been fetched.</exception>
    internal static string Locate(int vector)
    {
        if (vector is < 1 or > 12)
        {
            throw new ArgumentOutOfRangeException(nameof(vector), vector, "Vector number must be 1 to 12.");
        }

        string fileName = FormattableString.Invariant($"testvector{vector:D2}.bit");
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(
                directory.FullName, "tests", "Kiln.Audio.Tests", "Fixtures", "opus_testvectors", fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            FormattableString.Invariant(
                $"{fileName} was not found; run scripts/fetch-test-vectors.sh first."));
    }

    /// <summary>Reads every packet of a vector into memory.</summary>
    /// <param name="vector">Vector number, 1 to 12.</param>
    /// <returns>The packets in file order.</returns>
    /// <exception cref="InvalidDataException">The file ends inside a record header or packet.</exception>
    internal static byte[][] ReadPackets(int vector)
    {
        byte[] bytes = File.ReadAllBytes(Locate(vector));
        var packets = new List<byte[]>();
        int offset = 0;
        while (offset < bytes.Length)
        {
            if (bytes.Length - offset < HeaderSize)
            {
                throw new InvalidDataException(
                    FormattableString.Invariant($"Truncated record header at byte {offset}."));
            }

            int length = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(offset, sizeof(int)));
            offset += HeaderSize;
            if (length < 0 || length > bytes.Length - offset)
            {
                throw new InvalidDataException(
                    FormattableString.Invariant(
                        $"A record announces {length} bytes but only {bytes.Length - offset} remain."));
            }

            packets.Add(bytes.AsSpan(offset, length).ToArray());
            offset += length;
        }

        return [.. packets];
    }
}
