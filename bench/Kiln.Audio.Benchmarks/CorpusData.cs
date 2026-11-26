using System.Buffers.Binary;
using System.IO;

namespace Kiln.Audio.Benchmarks;

/// <summary>
/// Loads the encoder benchmark corpus: the mono downmix of every RFC 8251 <c>.dec</c> file,
/// concatenated, as used by the quality gate.
/// </summary>
internal static class CorpusData
{
    /// <summary>Reads and downmixes every <c>.dec</c> file, then concatenates them.</summary>
    /// <exception cref="FileNotFoundException">The vectors have not been fetched.</exception>
    internal static short[] LoadMono()
    {
        var mono = new List<short>(16 * 1024 * 1024);
        for (int vector = 1; vector <= 12; vector++)
        {
            short[] stereo = ReadPcm(Locate(vector));
            for (int i = 0; i + 1 < stereo.Length; i += 2)
            {
                mono.Add((short)(((int)stereo[i] + stereo[i + 1]) / 2));
            }
        }

        return [.. mono];
    }

    private static string Locate(int vector)
    {
        string fileName = FormattableString.Invariant($"testvector{vector:D2}.dec");
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
            FormattableString.Invariant($"{fileName} was not found; run scripts/fetch-test-vectors.sh first."));
    }

    private static short[] ReadPcm(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        var samples = new short[bytes.Length / 2];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(2 * i, 2));
        }

        return samples;
    }
}
