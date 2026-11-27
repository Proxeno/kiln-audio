using FluentAssertions;
using Kiln.Audio;
using Kiln.Audio.Tests.Encoder;
using Kiln.Audio.Tests.OpusEncoderTop;
using Kiln.Audio.Tests.Oracle;

namespace Kiln.Audio.Tests.Robustness;

/// <summary>
/// Decoding a stream whose packets come from different encoders exercises every mode transition.
/// The decoder must never throw outside its documented contract (docs/robustness.md): valid
/// packets always decode, whatever the previous mode, bandwidth or encoder instance was.
/// </summary>
public class ModeTransitionDecodeTests
{
    private const int FrameSamples = 960;

    [Fact]
    public void Concatenated_silk_hybrid_and_celt_streams_decode_without_throwing()
    {
        if (!OracleTestSupport.OracleAvailable())
        {
            return;
        }

        // SILK narrowband, hybrid fullband, CELT-only, then SILK wideband: every cross-mode
        // boundary the transition logic handles, in one stream.
        var segments = new[]
        {
            new OpusEncoderOptions { Application = OpusApplication.Voip, BitrateBps = 12000, Bandwidth = OpusBandwidth.Narrowband, Complexity = 5 },
            new OpusEncoderOptions { Application = OpusApplication.Voip, BitrateBps = 24000, Bandwidth = OpusBandwidth.Fullband, Complexity = 5 },
            new OpusEncoderOptions { Application = OpusApplication.Audio, BitrateBps = 64000, Complexity = 5 },
            new OpusEncoderOptions { Application = OpusApplication.Voip, BitrateBps = 24000, Bandwidth = OpusBandwidth.Wideband, Complexity = 5 },
        };

        short[] signal = EncoderTestSignals.Tone(segments.Length * 8 * FrameSamples, 300, 6000);
        var packets = new List<(byte[] Packet, uint Range)>();
        for (int s = 0; s < segments.Length; s++)
        {
            using var encoder = new OpusEncoder(segments[s]);
            var packet = new byte[1276];
            for (int f = 0; f < 8; f++)
            {
                int offset = ((s * 8) + f) * FrameSamples;
                int written = encoder.Encode(signal.AsSpan(offset, FrameSamples), packet);
                packets.Add((packet.AsSpan(0, written).ToArray(), encoder.FinalRange));
            }
        }

        using var decoder = new OpusDecoder(new OpusDecoderOptions { SampleRate = 48000, Channels = 1 });
        var pcm = new short[FrameSamples];
        for (int i = 0; i < packets.Count; i++)
        {
            (byte[] packet, uint range) = packets[i];
            decoder.Decode(packet, pcm).Should().Be(FrameSamples, $"frame {i} must decode to one frame");
            decoder.FinalRange.Should().Be(range, $"frame {i}: the decoder range must match the encoder's");
        }

        string path = Path.Combine(Path.GetTempPath(), $"kiln-transition-{Guid.NewGuid():N}.bit");
        try
        {
            EncoderCorpus.WriteBitFile(path, packets);
            (_, string diagnostics) = EncoderOracle.Decode(path);
            EncoderOracle.HasRangeMismatch(diagnostics).Should().BeFalse(
                $"opus_demo reported a range mismatch on the concatenated stream:\n{diagnostics}");
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void Concealment_after_a_rate_change_does_not_throw()
    {
        // A SILK narrowband packet followed by concealment, then a hybrid frame followed by
        // concealment: the PLC geometry must follow the synthesizer's rate, not a stale one.
        var silk = new OpusEncoderOptions
        {
            Application = OpusApplication.Voip,
            BitrateBps = 12000,
            Bandwidth = OpusBandwidth.Narrowband,
            Complexity = 5,
        };
        var hybrid = new OpusEncoderOptions
        {
            Application = OpusApplication.Voip,
            BitrateBps = 24000,
            Bandwidth = OpusBandwidth.Fullband,
            Complexity = 5,
        };

        using var decoder = new OpusDecoder(new OpusDecoderOptions { SampleRate = 48000, Channels = 1 });
        var pcm = new short[FrameSamples];
        short[] signal = EncoderTestSignals.Tone(8 * FrameSamples, 300, 6000);

        using (var encoder = new OpusEncoder(silk))
        {
            var packet = new byte[1276];
            int written = encoder.Encode(signal.AsSpan(0, FrameSamples), packet);
            decoder.Decode(packet.AsSpan(0, written), pcm);
        }

        // 20 ms of concealment at the narrowband geometry.
        decoder.DecodeLoss(pcm).Should().Be(FrameSamples);

        using (var encoder = new OpusEncoder(hybrid))
        {
            var packet = new byte[1276];
            int written = encoder.Encode(signal.AsSpan(FrameSamples, FrameSamples), packet);
            decoder.Decode(packet.AsSpan(0, written), pcm);
        }

        // 20 ms of concealment at the hybrid geometry.
        decoder.DecodeLoss(pcm).Should().Be(FrameSamples);
    }
}
