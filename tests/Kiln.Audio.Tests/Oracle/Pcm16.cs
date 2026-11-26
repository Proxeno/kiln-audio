using System;
using System.Buffers.Binary;
using System.IO;

namespace Kiln.Audio.Tests.Oracle;

/// <summary>Reads and writes the raw little-endian 16-bit PCM used by the libopus tools.</summary>
internal static class Pcm16
{
    /// <summary>Reads a raw PCM file into interleaved samples.</summary>
    /// <param name="path">Path to the PCM file.</param>
    internal static short[] Read(string path) => FromBytes(File.ReadAllBytes(path));

    /// <summary>Converts raw little-endian PCM bytes into interleaved samples.</summary>
    /// <param name="bytes">Raw PCM bytes.</param>
    internal static short[] FromBytes(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var samples = new short[bytes.Length / 2];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(2 * i, 2));
        }

        return samples;
    }

    /// <summary>Writes interleaved samples as raw little-endian PCM.</summary>
    /// <param name="path">Destination path.</param>
    /// <param name="samples">Interleaved samples.</param>
    internal static void Write(string path, short[] samples) => File.WriteAllBytes(path, ToBytes(samples));

    /// <summary>Converts interleaved samples into raw little-endian PCM bytes.</summary>
    /// <param name="samples">Interleaved samples.</param>
    internal static byte[] ToBytes(short[] samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var bytes = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(2 * i, 2), samples[i]);
        }

        return bytes;
    }
}
