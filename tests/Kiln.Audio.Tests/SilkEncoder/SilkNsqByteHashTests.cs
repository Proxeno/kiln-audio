using System.Security.Cryptography;
using Kiln.Audio.Simd.Kernels;

namespace Kiln.Audio.Tests;

/// <summary>
/// ENC-NSQ-1: the SILK noise shaping quantizer and the analysis kernels are performance work only. Every
/// SILK-only encode of a fixed synthetic corpus must hash to the bytes the encoder produced before the work
/// (golden hashes recorded from the pre-change encoder), on every kernel tier. The corpus is generated with
/// integer arithmetic only, so it is identical on every host. CBR-FIX regenerated 24 CBR hashes: the CBR
/// rate-loop restore used to emit a corrupt frame ("Range coder state mismatch" in libopus) whenever it ran,
/// and every case must now also decode in libopus with a matching final range. The two 64 kbps cases also
/// change because their automatic mode selection falls back to CELT, whose CBR payload is now sized to the
/// nominal packet. The VBR cases are unchanged.
/// </summary>
public class SilkNsqByteHashTests
{
    public sealed record Case(string Name, int Channels, int BitrateBps, int Complexity, OpusBandwidth Bandwidth,
        OpusFrameDuration Duration, bool Vbr, bool Fec, bool Dtx, string Hash);

    private static readonly Case[] s_cases =
    [
        new("wb12k-c0", 1, 12000, 0, OpusBandwidth.Wideband, OpusFrameDuration.Ms20, false, false, false, "39E58054C2A5DE540BC579FA"),
        new("wb12k-c2", 1, 12000, 2, OpusBandwidth.Wideband, OpusFrameDuration.Ms20, false, false, false, "E698D4ABC5E5A02DFCCA4728"),
        new("wb12k-c4", 1, 12000, 4, OpusBandwidth.Wideband, OpusFrameDuration.Ms20, false, false, false, "3DB9EC7FE51AA59432D71745"),
        new("wb12k-c6", 1, 12000, 6, OpusBandwidth.Wideband, OpusFrameDuration.Ms20, false, false, false, "5C5FBF120A115D0F4FAFD939"),
        new("wb12k-c8", 1, 12000, 8, OpusBandwidth.Wideband, OpusFrameDuration.Ms20, false, false, false, "27E193F432E3D37E935EF785"),
        new("wb12k-c10", 1, 12000, 10, OpusBandwidth.Wideband, OpusFrameDuration.Ms20, false, false, false, "27E193F432E3D37E935EF785"),
        new("wb12k-c5-vbr", 1, 12000, 5, OpusBandwidth.Wideband, OpusFrameDuration.Ms20, true, false, false, "4F49B2906A200D08F39376D0"),
        new("wb12k-c0-10ms", 1, 12000, 0, OpusBandwidth.Wideband, OpusFrameDuration.Ms10, false, false, false, "6F46742CD4A27E6AD8500F18"),
        new("wb12k-c5-10ms", 1, 12000, 5, OpusBandwidth.Wideband, OpusFrameDuration.Ms10, false, false, false, "260DD9D2B11C001CCA5EAE6E"),
        new("wb12k-c5-40ms", 1, 12000, 5, OpusBandwidth.Wideband, OpusFrameDuration.Ms40, false, false, false, "E287050AFBE8522470AAE774"),
        new("wb12k-c0-60ms", 1, 12000, 0, OpusBandwidth.Wideband, OpusFrameDuration.Ms60, false, false, false, "3C5E478732B9D853E8CF458F"),
        new("wb12k-c5-fec", 1, 12000, 5, OpusBandwidth.Wideband, OpusFrameDuration.Ms20, false, true, false, "3DB9EC7FE51AA59432D71745"),
        new("wb12k-c8-fec", 1, 12000, 8, OpusBandwidth.Wideband, OpusFrameDuration.Ms20, false, true, false, "27E193F432E3D37E935EF785"),
        new("wb16k-c3-dtx", 1, 16000, 3, OpusBandwidth.Wideband, OpusFrameDuration.Ms20, false, false, true, "E37684697842D710A7AC929A"),
        new("mb10k-c0", 1, 10000, 0, OpusBandwidth.Mediumband, OpusFrameDuration.Ms20, false, false, false, "2E653661FF35EC8A3FFB6DFE"),
        new("mb10k-c7", 1, 10000, 7, OpusBandwidth.Mediumband, OpusFrameDuration.Ms20, false, false, false, "0D45C22659EC1DA28447C4C9"),
        new("nb7k-c0", 1, 7000, 0, OpusBandwidth.Narrowband, OpusFrameDuration.Ms20, false, false, false, "6C9B1875AF1F3DCE8F3D1F1A"),
        new("nb7k-c9", 1, 7000, 9, OpusBandwidth.Narrowband, OpusFrameDuration.Ms20, false, false, false, "97E909603EC3D8482B681985"),
        new("wb20k-c0-highrate", 1, 20000, 0, OpusBandwidth.Wideband, OpusFrameDuration.Ms20, false, false, false, "28CB191B756EB7478C7DC00F"),
        new("wb20k-c5-highrate", 1, 20000, 5, OpusBandwidth.Wideband, OpusFrameDuration.Ms20, false, false, false, "3C32EDF7A0ED07F5C8578D71"),
        new("wb64k-c0-highrate", 1, 64000, 0, OpusBandwidth.Wideband, OpusFrameDuration.Ms20, false, false, false, "DDBBE95F1083F30A1CC9171D"),
        new("wb64k-c8-highrate", 1, 64000, 8, OpusBandwidth.Wideband, OpusFrameDuration.Ms20, false, false, false, "73270BE89E8AE3649C43DAE4"),
        new("wb20k-st-c0", 2, 20000, 0, OpusBandwidth.Wideband, OpusFrameDuration.Ms20, false, false, false, "B64333CF78E44709138A7646"),
        new("wb20k-st-c5", 2, 20000, 5, OpusBandwidth.Wideband, OpusFrameDuration.Ms20, false, false, false, "912D248089000838FC4AED87"),
        new("wb20k-st-c9-vbr", 2, 20000, 9, OpusBandwidth.Wideband, OpusFrameDuration.Ms20, true, false, false, "E3170CF5457A47B402430C28"),
        new("wb40k-st-c5", 2, 40000, 5, OpusBandwidth.Wideband, OpusFrameDuration.Ms20, false, false, false, "438639102C768D793895DCEE"),
    ];


    public static TheoryData<string> CaseNames
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (Case c in s_cases)
            {
                data.Add(c.Name);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void A_silk_encode_hashes_to_the_pre_change_bytes_on_every_tier(string name)
    {
        Case c = Array.Find(s_cases, x => x.Name == name)!;
        short[] pcm = Synthesize(c.Channels, 48000 * 3);
        var failures = new List<string>();
        foreach (AudioKernelTier tier in new[]
                 {
                     AudioKernelTier.Scalar, AudioKernelTier.Vector128, AudioKernelTier.AdvSimdArm64, AudioKernelTier.Avx2Fma,
                 })
        {
            if (!AudioKernels.IsSupported(tier))
            {
                continue;
            }

            string actual = HashEncode(tier, c, pcm);
            if (actual != c.Hash)
            {
                failures.Add($"{tier}: {actual}");
            }
        }

        Assert.True(failures.Count == 0, $"{name} expected {c.Hash}; got {string.Join(", ", failures)}");
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void A_silk_encode_decodes_in_libopus_with_matching_final_range(string name)
    {
        if (!Kiln.Audio.Tests.Oracle.OracleTestSupport.OracleAvailable())
        {
            return;
        }

        Case c = Array.Find(s_cases, x => x.Name == name)!;
        short[] pcm = Synthesize(c.Channels, 48000 * 3);
        var packets = new List<(byte[] Packet, uint Range)>();
        int suppressed = 0;
        using (AudioKernels.Scope(AudioKernelTier.Scalar))
        {
            using var encoder = CreateEncoder(c);
            int frame = FrameSamples(c) * c.Channels;
            var buffer = new byte[1276];
            for (int offset = 0; offset + frame <= pcm.Length; offset += frame)
            {
                int length = encoder.Encode(pcm.AsSpan(offset, frame), buffer);
                byte[] packet = buffer.AsSpan(0, length).ToArray();
                if (c.Dtx && length == 1)
                {
                    // A DTX-suppressed packet is one TOC byte and no range-coded payload, so the encoder
                    // must report range 0 just as a decoder does; feed that reported value to libopus.
                    Assert.Equal(0u, encoder.FinalRange);
                    suppressed++;
                    packets.Add((packet, encoder.FinalRange));
                }
                else
                {
                    packets.Add((packet, encoder.FinalRange));
                }
            }
        }

        if (c.Dtx)
        {
            // The fixture must actually exercise DTX, and every other packet must carry a range check.
            Assert.True(suppressed > 0, $"{name}: the DTX fixture must suppress at least one packet");
            Assert.True(packets.Count > suppressed, $"{name}: the DTX fixture must transmit packets");
        }

        (_, string diagnostics) = Kiln.Audio.Tests.Encoder.EncoderOracle.Decode(
            Kiln.Audio.Tests.Encoder.EncoderCorpus.WriteBitFileToBytes(packets), c.Channels);
        Assert.False(
            Kiln.Audio.Tests.Encoder.EncoderOracle.HasRangeMismatch(diagnostics), $"{name}: {diagnostics}");
    }

    private static int FrameSamples(Case c) => c.Duration switch
    {
        OpusFrameDuration.Ms10 => 480,
        OpusFrameDuration.Ms40 => 1920,
        OpusFrameDuration.Ms60 => 2880,
        _ => 960,
    };

    private static OpusEncoder CreateEncoder(Case c) => new(new OpusEncoderOptions
    {
        Application = OpusApplication.Voip,
        SampleRate = 48000,
        Channels = c.Channels,
        BitrateBps = c.BitrateBps,
        FrameDuration = c.Duration,
        Complexity = c.Complexity,
        Bandwidth = c.Bandwidth,
        UseCbr = !c.Vbr,
        UseVbr = c.Vbr,
        UseInbandFec = c.Fec,
        PacketLossPercent = c.Fec ? 15 : 0,
        UseDtx = c.Dtx,
    });

    private static string HashEncode(AudioKernelTier tier, Case c, short[] pcm)
    {
        using (AudioKernels.Scope(tier))
        {
            using var encoder = new OpusEncoder(new OpusEncoderOptions
            {
                Application = OpusApplication.Voip,
                SampleRate = 48000,
                Channels = c.Channels,
                BitrateBps = c.BitrateBps,
                FrameDuration = c.Duration,
                Complexity = c.Complexity,
                Bandwidth = c.Bandwidth,
                UseCbr = !c.Vbr,
                UseVbr = c.Vbr,
                UseInbandFec = c.Fec,
                PacketLossPercent = c.Fec ? 15 : 0,
                UseDtx = c.Dtx,
            });
            int samplesPerMs = 48;
            int frame = c.Duration switch
            {
                OpusFrameDuration.Ms10 => 10 * samplesPerMs,
                OpusFrameDuration.Ms40 => 40 * samplesPerMs,
                OpusFrameDuration.Ms60 => 60 * samplesPerMs,
                _ => 20 * samplesPerMs,
            } * c.Channels;
            var buffer = new byte[1276];
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            for (int offset = 0; offset + frame <= pcm.Length; offset += frame)
            {
                int length = encoder.Encode(pcm.AsSpan(offset, frame), buffer);
                sha.AppendData(BitConverter.GetBytes(length));
                sha.AppendData(buffer.AsSpan(0, length));
            }

            return Convert.ToHexString(sha.GetHashAndReset())[..24];
        }
    }

    /// <summary>
    /// Integer-only speech-like signal at 48 kHz: a pitch-varying pulse train through a two-pole resonator plus noise,
    /// with unvoiced bursts and silent gaps, so every signal type, quantization offset and the LTP and interpolation
    /// paths of the quantizer are exercised. Channel 1 is a delayed, attenuated copy with its own noise.
    /// </summary>
    private static short[] Synthesize(int channels, int frames)
    {
        var pcm = new short[frames * channels];
        uint state = 0x1234ABCD;
        int y1 = 0;
        int y2 = 0;
        int phase = 0;
        for (int i = 0; i < frames; i++)
        {
            state = unchecked((state * 1664525u) + 1013904223u);
            int noise = (int)(state >> 17) - 16384; // +-16384
            int segment = i / 12000;
            int pitch = 300 + (((i / 97) % 61) - 30); // samples at 48 kHz, ~160 Hz
            int excitation = 0;
            if (++phase >= pitch)
            {
                phase = 0;
                excitation = 24000;
            }

            int gain = (segment % 5) switch { 0 => 4, 1 => 3, 2 => 1, 3 => 0, _ => 2 };
            int input = gain switch
            {
                0 => noise >> 9,
                1 => noise >> 2,
                _ => (excitation * gain / 4) + (noise >> 6),
            };
            int y = input + ((y1 * 3) >> 1) - ((y2 * 3) >> 2);
            y2 = y1;
            y1 = Math.Clamp(y, -30000, 30000);
            int sample = Math.Clamp(y1 / 2, -32768, 32767);
            for (int ch = 0; ch < channels; ch++)
            {
                state = unchecked((state * 1664525u) + 1013904223u);
                int n2 = ((int)(state >> 20) - 2048) >> (ch + 1);
                int s = ch == 0 ? sample : (sample * 3 / 4) + n2;
                pcm[(i * channels) + ch] = (short)Math.Clamp(s + (ch == 0 ? n2 : 0), -32768, 32767);
            }
        }

        return pcm;
    }
}
