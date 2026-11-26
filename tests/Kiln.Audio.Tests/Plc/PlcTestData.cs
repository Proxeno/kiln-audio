using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using Kiln.Audio;
using Kiln.Audio.Celt;
using Kiln.Audio.Decoder;
using Kiln.Audio.Tests.Oracle;

namespace Kiln.Audio.Tests.Plc;

/// <summary>One packet record of a <c>.bit</c> file.</summary>
internal sealed record PlcPacket(byte[] Data, uint FinalRange);

/// <summary>One output region of a lossy decode.</summary>
internal sealed record PlcSegment(int StartSample, int Length, bool Concealed, int RunIndex);

/// <summary>Deterministic packet-loss patterns shared by the PLC tests.</summary>
internal enum PlcLossPattern
{
    /// <summary>Every tenth packet.</summary>
    EveryTenth,

    /// <summary>A run of five consecutive packets.</summary>
    BurstOfFive,

    /// <summary>Two percent of packets, chosen with a fixed seed.</summary>
    RandomTwoPercent,
}

/// <summary>Helpers that apply a packet-loss plan to a vector and drive both decoders.</summary>
internal static class PlcTestData
{
    /// <summary>Reads a vector's packet records.</summary>
    internal static List<PlcPacket> ReadVector(int vector)
    {
        var packets = new List<PlcPacket>();
        foreach ((byte[] packet, uint finalRange) in OpusBitFile.Read(TestVectors.BitPath(vector)))
        {
            packets.Add(new PlcPacket(packet, finalRange));
        }

        return packets;
    }

    /// <summary>Returns the number of frames a packet codes (RFC 6716 §3.2).</summary>
    internal static int FrameCount(byte[] packet) => OpusPacket.GetFrameCount(packet);

    /// <summary>Returns the samples per channel of one frame of the packet.</summary>
    internal static int SamplesPerFrame(byte[] packet, int apiRate) =>
        OpusPacket.GetSamplesPerFrame(OpusToc.Parse(packet[0]), apiRate);

    /// <summary>
    /// Chooses the packet indices to drop for a pattern. A packet is only eligible when it uses
    /// the same frame size as the packet before it, because a lost packet is concealed with the
    /// previous frame size by both the reference and this decoder.
    /// </summary>
    internal static HashSet<int> ChooseLosses(IReadOnlyList<PlcPacket> packets, int apiRate, PlcLossPattern pattern)
    {
        // Both decoders conceal a lost packet at the duration of the packet before it, and
        // opus_demo emits one preceding-packet duration per lost record. Restrict the patterns to
        // packets that are single-frame, whose predecessor is single-frame too, and whose frame
        // size matches, so the lossy streams stay the same length as the clean decode.
        var candidates = new List<int>();
        int previousSamples = 0;
        int previousFrames = 0;
        for (int i = 0; i < packets.Count; i++)
        {
            int samples = SamplesPerFrame(packets[i].Data, apiRate);
            int frames = FrameCount(packets[i].Data);
            if (i > 0 && frames == 1 && previousFrames == 1 && samples == previousSamples)
            {
                candidates.Add(i);
            }

            previousSamples = samples;
            previousFrames = frames;
        }

        var losses = new HashSet<int>();
        switch (pattern)
        {
            case PlcLossPattern.EveryTenth:
                for (int c = 9; c < candidates.Count; c += 10)
                {
                    losses.Add(candidates[c]);
                }

                break;
            case PlcLossPattern.BurstOfFive:
                int start = Math.Clamp(candidates.Count / 2, 0, Math.Max(0, candidates.Count - 5));
                for (int c = start; c < Math.Min(start + 5, candidates.Count); c++)
                {
                    losses.Add(candidates[c]);
                }

                break;
            default:
                var random = new Random(0x5EED);
                foreach (int candidate in candidates)
                {
                    if (random.NextDouble() < 0.02)
                    {
                        losses.Add(candidate);
                    }
                }

                break;
        }

        return losses;
    }

    /// <summary>
    /// Writes the packet file the oracle decodes: a dropped packet becomes as many zero-length
    /// records as it has frames, so <c>opus_demo</c> conceals one frame per record.
    /// </summary>
    internal static byte[] BuildOracleLossFile(IReadOnlyList<PlcPacket> packets, HashSet<int> losses)
    {
        using var stream = new MemoryStream();
        for (int i = 0; i < packets.Count; i++)
        {
            PlcPacket packet = packets[i];
            int records = losses.Contains(i) ? Math.Max(1, FrameCount(packet.Data)) : 1;
            for (int r = 0; r < records; r++)
            {
                if (losses.Contains(i))
                {
                    WriteRecord(stream, 0, 0, ReadOnlySpan<byte>.Empty);
                }
                else
                {
                    WriteRecord(stream, packet.Data.Length, packet.FinalRange, packet.Data);
                }
            }
        }

        return stream.ToArray();
    }

    /// <summary>Writes the original packet file bytes.</summary>
    internal static byte[] BuildBitFile(IReadOnlyList<PlcPacket> packets)
    {
        using var stream = new MemoryStream();
        foreach (PlcPacket packet in packets)
        {
            WriteRecord(stream, packet.Data.Length, packet.FinalRange, packet.Data);
        }

        return stream.ToArray();
    }

    /// <summary>Decodes with this library, calling <see cref="OpusDecoder.DecodeLoss"/> for drops.</summary>
    internal static (float[] Pcm, List<PlcSegment> Segments) DecodeOurs(
        IReadOnlyList<PlcPacket> packets, int apiRate, HashSet<int> losses)
    {
        const int channels = 1;
        var decoder = new OpusDecoderCore(apiRate, channels);
        int capacity = apiRate * 120 / 1000;
        var pcm = new List<float>(packets.Count * capacity);
        var scratch = new float[capacity * channels];
        var segments = new List<PlcSegment>();
        int runIndex = -1;
        bool previousLost = false;

        for (int i = 0; i < packets.Count; i++)
        {
            int start = pcm.Count;
            bool lost = losses.Contains(i);
            if (lost != previousLost)
            {
                runIndex++;
            }

            if (lost)
            {
                int frames = Math.Max(1, FrameCount(packets[i].Data));
                for (int f = 0; f < frames; f++)
                {
                    int produced = decoder.DecodeLoss(scratch);
                    Append(pcm, scratch, produced * channels);
                }
            }
            else
            {
                int produced = decoder.DecodePacket(packets[i].Data, scratch, decodeFec: false);
                Append(pcm, scratch, produced * channels);
            }

            segments.Add(new PlcSegment(start, pcm.Count - start, lost, runIndex));
            previousLost = lost;
        }

        return (pcm.ToArray(), segments);
    }

    /// <summary>Decodes with this library without any losses.</summary>
    internal static float[] DecodeOursClean(IReadOnlyList<PlcPacket> packets, int apiRate)
    {
        const int channels = 1;
        var decoder = new OpusDecoderCore(apiRate, channels);
        int capacity = apiRate * 120 / 1000;
        var pcm = new List<float>(packets.Count * capacity);
        var scratch = new float[capacity * channels];
        foreach (PlcPacket packet in packets)
        {
            int produced = decoder.DecodePacket(packet.Data, scratch, decodeFec: false);
            Append(pcm, scratch, produced * channels);
        }

        return pcm.ToArray();
    }

    /// <summary>Converts normalized float PCM to the library's int16 rounding.</summary>
    internal static short[] ToInt16(float[] pcm)
    {
        var result = new short[pcm.Length];
        for (int i = 0; i < pcm.Length; i++)
        {
            result[i] = CeltPcm.ToInt16(pcm[i]);
        }

        return result;
    }

    /// <summary>Replaces every concealed sample with silence (the pre-PLC behaviour).</summary>
    internal static short[] SilenceConcealed(short[] pcm, IReadOnlyList<PlcSegment> segments)
    {
        var result = (short[])pcm.Clone();
        foreach (PlcSegment segment in segments)
        {
            if (segment.Concealed)
            {
                result.AsSpan(segment.StartSample, segment.Length).Clear();
            }
        }

        return result;
    }

    /// <summary>SNR in dB of <paramref name="test"/> against <paramref name="reference"/>.</summary>
    internal static double Snr(ReadOnlySpan<short> reference, ReadOnlySpan<short> test)
    {
        double signal = 0;
        double noise = 0;
        for (int i = 0; i < reference.Length; i++)
        {
            double r = reference[i];
            double t = test[i];
            signal += r * r;
            noise += (r - t) * (r - t);
        }

        return noise <= 0 ? 200 : 10 * Math.Log10(signal / noise);
    }

    /// <summary>SNR in dB over a sample window, clamped to the arrays.</summary>
    internal static double Snr(short[] reference, short[] test, int start, int length) =>
        Snr(
            reference.AsSpan(start, Math.Max(0, Math.Min(length, reference.Length - start))),
            test.AsSpan(start, Math.Max(0, Math.Min(length, test.Length - start))));

    /// <summary>Collects the concealed sample ranges as a list of (start, end) pairs.</summary>
    internal static List<(int Start, int End)> ConcealedRanges(IReadOnlyList<PlcSegment> segments)
    {
        var ranges = new List<(int Start, int End)>();
        foreach (PlcSegment segment in segments)
        {
            if (segment.Concealed)
            {
                if (ranges.Count > 0 && ranges[^1].End == segment.StartSample)
                {
                    ranges[^1] = (ranges[^1].Start, segment.StartSample + segment.Length);
                }
                else
                {
                    ranges.Add((segment.StartSample, segment.StartSample + segment.Length));
                }
            }
        }

        return ranges;
    }

    /// <summary>SNR over the union of the given sample ranges.</summary>
    internal static double SnrOverRanges(
        short[] reference, short[] test, IReadOnlyList<(int Start, int End)> ranges)
    {
        double signal = 0;
        double noise = 0;
        foreach ((int start, int end) in ranges)
        {
            for (int i = start; i < end && i < reference.Length; i++)
            {
                double r = reference[i];
                double t = test[i];
                signal += r * r;
                noise += (r - t) * (r - t);
            }
        }

        return noise <= 0 ? 200 : 10 * Math.Log10(signal / noise);
    }

    private static void Append(List<float> destination, float[] source, int count)
    {
        for (int i = 0; i < count; i++)
        {
            destination.Add(source[i]);
        }
    }

    private static void WriteRecord(Stream stream, int length, uint finalRange, ReadOnlySpan<byte> data)
    {
        Span<byte> header = stackalloc byte[OpusBitFile.HeaderSize];
        BinaryPrimitives.WriteInt32BigEndian(header, length);
        BinaryPrimitives.WriteUInt32BigEndian(header[sizeof(int)..], finalRange);
        stream.Write(header);
        stream.Write(data);
    }
}
