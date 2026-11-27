using System.Buffers.Binary;
using Kiln.Audio.Tests.SilkEncoder;

namespace Kiln.Audio.Tests.OpusEncoderTop;

/// <summary>
/// Measurement code for the full-band hybrid gate: the reference signal, our encoder and decoder
/// round trip, and the long-term 13 to 19 kHz band retention.
/// </summary>
internal static class HybridFullbandHarness
{
    private static readonly Lazy<short[]> ReferenceLazy =
        new(() => IdealResampler.Resample(FullbandCorpus.Mono, FullbandCorpus.Rate, FullbandCorpus.Rate));

    private static readonly object ReportLock = new();

    /// <summary>
    /// The independent reference, built exactly as the existing hybrid gate builds its 48 kHz
    /// reference: <see cref="IdealResampler"/> from 48 kHz to 48 kHz.
    /// </summary>
    internal static short[] Reference => ReferenceLazy.Value;

    /// <summary>
    /// Encodes the item with our encoder and returns the <c>.bit</c> stream (4-byte big-endian length,
    /// 4-byte big-endian final range, packet) with a histogram of the packets' coding modes
    /// (index 0 SILK-only, 1 hybrid, 2 CELT-only).
    /// </summary>
    internal static (byte[] Bitstream, int[] ModeCounts) EncodeOurs(OpusBandwidth bandwidth, int bitrate, int complexity)
    {
        short[] mono = FullbandCorpus.Mono;
        var options = new OpusEncoderOptions
        {
            Application = OpusApplication.Voip,
            SampleRate = FullbandCorpus.Rate,
            Channels = 1,
            Bandwidth = bandwidth,
            BitrateBps = bitrate,
            FrameDuration = OpusFrameDuration.Ms20,
            Complexity = complexity,
        };

        var modeCounts = new int[3];
        using var encoder = new OpusEncoder(options);
        var packet = new byte[1276];
        var bit = new MemoryStream();
        Span<byte> header = stackalloc byte[8];
        for (int offset = 0; offset + 960 <= mono.Length; offset += 960)
        {
            int written = encoder.Encode(mono.AsSpan(offset, 960), packet);
            BinaryPrimitives.WriteInt32BigEndian(header[..4], written);
            BinaryPrimitives.WriteUInt32BigEndian(header[4..], encoder.FinalRange);
            bit.Write(header);
            bit.Write(packet.AsSpan(0, written));
            if (written > 0)
            {
                OpusMode mode = OpusToc.Parse(packet[0]).Mode;
                int index = mode == OpusMode.Silk ? 0 : (mode == OpusMode.Hybrid ? 1 : 2);
                modeCounts[index]++;
            }
        }

        return (bit.ToArray(), modeCounts);
    }

    /// <summary>Decodes a <c>.bit</c> stream with our own decoder (48 kHz mono) and returns the PCM.</summary>
    internal static short[] DecodeOurs(byte[] bitstream)
    {
        using var decoder = new OpusDecoder(new OpusDecoderOptions { SampleRate = FullbandCorpus.Rate, Channels = 1 });
        var all = new List<short>();
        var frame = new short[5760];
        int position = 0;
        while (position + 8 <= bitstream.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(bitstream.AsSpan(position, 4));
            position += 8;
            if (length < 0 || position + length > bitstream.Length)
            {
                throw new InvalidDataException("Truncated .bit stream.");
            }

            int samples = decoder.Decode(bitstream.AsSpan(position, length), frame);
            for (int i = 0; i < samples; i++)
            {
                all.Add(frame[i]);
            }

            position += length;
        }

        return all.ToArray();
    }

    /// <summary>
    /// The decoded 13 to 19 kHz band energy minus the source's, in dB. A few milliseconds of codec
    /// delay do not matter for a long-term band energy.
    /// </summary>
    internal static double HighBandRetentionDb(short[] decoded) =>
        FullbandCorpus.BandEnergyDb(decoded, FullbandCorpus.HighBandLowHz, FullbandCorpus.HighBandHighHz, FullbandCorpus.WarmupSamples)
        - FullbandCorpus.BandEnergyDb(FullbandCorpus.Mono, FullbandCorpus.HighBandLowHz, FullbandCorpus.HighBandHighHz, FullbandCorpus.WarmupSamples);

    /// <summary>Appends a line to the full-band report files (temp directory and test output directory).</summary>
    internal static void Report(string line)
    {
        lock (ReportLock)
        {
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "kiln-hybrid-fullband-report.txt"), line + Environment.NewLine);
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "kiln-hybrid-fullband-report.txt"), line + Environment.NewLine);
        }
    }
}
