using System.Buffers.Binary;
using Kiln.Audio.Tests.Oracle;

namespace Kiln.Audio.Tests.Encoder;

/// <summary>The mono corpus and the objective metrics used by the encoder gates.</summary>
internal static class EncoderCorpus
{
    /// <summary>The audio delay of a CELT-only stream, in samples at 48 kHz (2.5 ms overlap).</summary>
    internal const int StreamDelay = 120;

    /// <summary>Reads the mono downmix of every RFC 8251 <c>.dec</c> file.</summary>
    /// <returns>The seventeen-second corpus, one entry per vector, in vector order.</returns>
    internal static IReadOnlyList<(int Vector, short[] Mono)> Load()
    {
        var corpus = new List<(int, short[])>(TestVectors.Count);
        for (int vector = 1; vector <= TestVectors.Count; vector++)
        {
            short[] stereo = Pcm16.Read(TestVectors.DecPath(vector));
            var mono = new short[stereo.Length / 2];
            for (int i = 0; i < mono.Length; i++)
            {
                mono[i] = (short)(((int)stereo[2 * i] + stereo[2 * i + 1]) / 2);
            }

            corpus.Add((vector, mono));
        }

        return corpus;
    }

    /// <summary>
    /// Computes the mean segmental SNR of <paramref name="test"/> against
    /// <paramref name="reference"/>, in 20 ms segments clamped to [-10, 60] dB.
    /// </summary>
    /// <param name="reference">The original mono signal.</param>
    /// <param name="test">The decoded mono signal, delayed by <paramref name="delay"/> samples.</param>
    /// <param name="delay">The codec's stream delay in samples.</param>
    /// <returns>A (mean dB, segment count) pair.</returns>
    internal static (double Mean, long Segments) SegmentalSnr(short[] reference, short[] test, int delay)
    {
        const int segment = 960;
        double total = 0;
        long segments = 0;
        int comparable = Math.Min(reference.Length, test.Length);
        for (int start = 0; start + segment <= comparable - delay; start += segment)
        {
            double signal = 0;
            double noise = 0;
            for (int i = 0; i < segment; i++)
            {
                double s = reference[start + i];
                double d = (double)test[start + i + delay] - s;
                signal += s * s;
                noise += d * d;
            }

            double snr;
            if (signal <= 0)
            {
                snr = noise <= 0 ? 60 : -10;
            }
            else if (noise <= 0)
            {
                snr = 60;
            }
            else
            {
                snr = 10 * Math.Log10(signal / noise);
            }

            total += Math.Clamp(snr, -10, 60);
            segments++;
        }

        return (total / segments, segments);
    }

    /// <summary>Encodes a mono signal with this library and returns the packet stream and final ranges.</summary>
    internal static List<(byte[] Packet, uint Range)> EncodeWithLibrary(
        short[] mono, int bitrate, OpusFrameDuration duration, int complexity)
    {
        var options = new OpusEncoderOptions
        {
            Application = OpusApplication.RestrictedLowDelay,
            BitrateBps = bitrate,
            FrameDuration = duration,
            Complexity = complexity,
        };

        int frameSamples = duration switch
        {
            OpusFrameDuration.Ms2_5 => 120,
            OpusFrameDuration.Ms5 => 240,
            OpusFrameDuration.Ms10 => 480,
            OpusFrameDuration.Ms20 => 960,
            _ => throw new ArgumentOutOfRangeException(nameof(duration)),
        };

        using var encoder = new OpusEncoder(options);
        var packets = new List<(byte[], uint)>();
        var packet = new byte[1276];
        for (int offset = 0; offset + frameSamples <= mono.Length; offset += frameSamples)
        {
            int written = encoder.Encode(mono.AsSpan(offset, frameSamples), packet);
            packets.Add((packet.AsSpan(0, written).ToArray(), encoder.FinalRange));
        }

        return packets;
    }

    /// <summary>Writes a <c>.bit</c> stream in the format <c>opus_demo</c> reads and writes.</summary>
    internal static void WriteBitFile(string path, IReadOnlyList<(byte[] Packet, uint Range)> packets)
    {
        using var stream = File.Create(path);
        WriteBitFileTo(stream, packets);
    }

    /// <summary>Serializes a packet stream in the <c>.bit</c> format and returns the bytes.</summary>
    internal static byte[] WriteBitFileToBytes(IReadOnlyList<(byte[] Packet, uint Range)> packets)
    {
        using var stream = new MemoryStream();
        WriteBitFileTo(stream, packets);
        return stream.ToArray();
    }

    /// <summary>Writes a <c>.bit</c> stream to an open stream.</summary>
    internal static void WriteBitFileTo(Stream stream, IReadOnlyList<(byte[] Packet, uint Range)> packets)
    {
        Span<byte> header = stackalloc byte[8];
        foreach ((byte[] packet, uint range) in packets)
        {
            BinaryPrimitives.WriteInt32BigEndian(header[..4], packet.Length);
            BinaryPrimitives.WriteUInt32BigEndian(header[4..], range);
            stream.Write(header);
            stream.Write(packet);
        }
    }

    /// <summary>Reinterprets a mono stream as interleaved stereo with identical channels.</summary>
    internal static short[] DuplicateToStereo(short[] mono)
    {
        var stereo = new short[mono.Length * 2];
        for (int i = 0; i < mono.Length; i++)
        {
            stereo[2 * i] = mono[i];
            stereo[2 * i + 1] = mono[i];
        }

        return stereo;
    }
}
