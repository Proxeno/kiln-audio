using FluentAssertions;
using Kiln.Audio;

namespace Kiln.Audio.Tests.OpusEncoderTop;

/// <summary>
/// The automatic mode decision follows a per-frame voice/music estimate, so a stream that changes
/// character changes mode mid-stream and still decodes cleanly.
/// </summary>
public class DynamicModeSelectionTests
{
    private const int FrameSamples = 960;

    [Fact]
    public void A_speech_then_music_stream_switches_mode_and_decodes_cleanly()
    {
        const int framesPerHalf = 40;

        // 300 Hz tone: low zero-crossing rate, classified as voice. White noise: high
        // zero-crossing rate, classified as music.
        var pcm = new short[2 * framesPerHalf * FrameSamples];
        for (int i = 0; i < framesPerHalf * FrameSamples; i++)
        {
            pcm[i] = (short)(6000 * Math.Sin(2 * Math.PI * 300 * i / 48000.0));
        }

        var random = new Random(0x5EED);
        for (int i = framesPerHalf * FrameSamples; i < pcm.Length; i++)
        {
            pcm[i] = (short)random.Next(-6000, 6000);
        }

        var options = new OpusEncoderOptions
        {
            Application = OpusApplication.Voip,
            // 34 kbps sits above the music mode threshold plus the SILK-side hysteresis
            // (27375 + 4000) and below the voice threshold minus the CELT-side hysteresis
            // (43372 - 4000), so the estimate alone decides the mode.
            BitrateBps = 34000,
            FrameDuration = OpusFrameDuration.Ms20,
            Complexity = 5,
        };

        var packets = new List<(byte[] Packet, uint Range)>();
        var modes = new List<OpusMode>();
        using (var encoder = new OpusEncoder(options))
        {
            var packet = new byte[1276];
            for (int offset = 0; offset + FrameSamples <= pcm.Length; offset += FrameSamples)
            {
                int written = encoder.Encode(pcm.AsSpan(offset, FrameSamples), packet);
                packets.Add((packet.AsSpan(0, written).ToArray(), encoder.FinalRange));
                modes.Add(OpusToc.Parse(packet[0]).Mode);
            }
        }

        OpusMode speechMode = modes[framesPerHalf - 1];
        OpusMode musicMode = modes[^1];
        speechMode.Should().NotBe(OpusMode.Celt, "voiced input selects SILK or hybrid");
        musicMode.Should().Be(OpusMode.Celt, "noisy content selects CELT");

        using var decoder = new OpusDecoder(new OpusDecoderOptions { SampleRate = 48000, Channels = 1 });
        var decoded = new short[FrameSamples];
        for (int i = 0; i < packets.Count; i++)
        {
            (byte[] packet, uint range) = packets[i];
            decoder.Decode(packet, decoded).Should().Be(FrameSamples, $"frame {i}");
            decoder.FinalRange.Should().Be(range, $"frame {i}: the decoder range must match the encoder's");
        }

        modes.Distinct().Should().HaveCountGreaterThan(1, "the mode must change mid-stream");
    }
}
