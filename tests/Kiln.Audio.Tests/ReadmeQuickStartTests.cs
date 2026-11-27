using FluentAssertions;
using Kiln.Audio;

namespace Kiln.Audio.Tests;

/// <summary>
/// The code in these tests is the README quick start, verbatim. If you change one, change the other.
/// </summary>
public class ReadmeQuickStartTests
{
    [Fact]
    public void Encode_and_decode_snippet_runs()
    {
        // Scaffolding: one 20 ms frame of a 440 Hz sine at 48 kHz.
        short[] pcm = Sine(0);

        // ---- README snippet 1 (verbatim) ----
        using var encoder = new OpusEncoder(new OpusEncoderOptions
        {
            SampleRate = 48000,
            Channels = 1,
            Application = OpusApplication.Voip,
            BitrateBps = 24000,
            FrameDuration = OpusFrameDuration.Ms20,
        });
        using var decoder = new OpusDecoder(new OpusDecoderOptions { SampleRate = 48000, Channels = 1 });

        const int FrameSamples = 960;        // 20 ms at 48 kHz, per channel
        var packet = new byte[1276];         // large enough for any single Opus frame
        var decoded = new short[FrameSamples];

        // pcm: exactly one frame of interleaved 16-bit samples.
        int bytes = encoder.Encode(pcm, packet);
        int samples = decoder.Decode(packet.AsSpan(0, bytes), decoded);

        // ---- end of README snippet 1 ----
        bytes.Should().BeInRange(1, 1276);
        samples.Should().Be(960);

        for (int frame = 1; frame <= 10; frame++)
        {
            bytes = encoder.Encode(Sine(frame), packet);
            samples = decoder.Decode(packet.AsSpan(0, bytes), decoded);
            bytes.Should().BeInRange(1, 1276);
            samples.Should().Be(960);
        }

        decoded.Should().Contain(sample => sample != 0, "the last decoded frame of a 440 Hz tone is not silence");
    }

    [Fact]
    public void Loss_and_fec_snippet_runs()
    {
        // ---- README snippet 2, declarations (verbatim) ----
        using var encoder = new OpusEncoder(new OpusEncoderOptions
        {
            Application = OpusApplication.Voip,
            BitrateBps = 32000,
            UseInbandFec = true,       // carry a low-bitrate copy of the previous frame
            PacketLossPercent = 10,    // tell the encoder how lossy the path is
        });
        using var decoder = new OpusDecoder(new OpusDecoderOptions());
        var decoded = new short[960];

        // Scaffolding: encode 12 speech-like frames, decode the first 10, drop the 11th and keep
        // the 12th as `next`.
        var scratch = new byte[1276];
        var encoded = new List<byte[]>();
        for (int frame = 0; frame < 12; frame++)
        {
            int written = encoder.Encode(Speech(frame), scratch);
            encoded.Add(scratch.AsSpan(0, written).ToArray());
        }

        for (int frame = 0; frame < 10; frame++)
        {
            decoder.Decode(encoded[frame], decoded).Should().Be(960);
        }

        byte[] next = encoded[11];

        // The packet before `next` was lost.
        if (OpusPacket.HasLbrr(next))
        {
            decoder.Decode(next, decoded, decodeFec: true);   // recover the lost frame from `next`
        }
        else
        {
            decoder.DecodeLoss(decoded);                      // no FEC data: conceal instead
        }

        decoder.Decode(next, decoded);                        // then decode `next` itself

        // ---- end of README snippet 2 ----
        decoder.LastFecOutcome.Should().Be(OpusFecOutcome.NotRequested, "the last call was a plain decode");
        decoded.Should().Contain(sample => sample != 0);
    }

    /// <summary>One 20 ms frame of a 440 Hz sine at 48 kHz, amplitude 8000.</summary>
    private static short[] Sine(int frameIndex)
    {
        var pcm = new short[960];
        for (int i = 0; i < pcm.Length; i++)
        {
            long n = ((long)frameIndex * pcm.Length) + i;
            pcm[i] = (short)Math.Round(8000.0 * Math.Sin(2.0 * Math.PI * 440.0 * n / 48000.0));
        }

        return pcm;
    }

    /// <summary>One 20 ms frame of a speech-like harmonic mix: 150 Hz with 10 harmonics, amplitude 6000.</summary>
    private static short[] Speech(int frameIndex)
    {
        const int Harmonics = 10;
        double norm = 0;
        for (int h = 1; h <= Harmonics; h++)
        {
            norm += 1.0 / h;
        }

        var pcm = new short[960];
        for (int i = 0; i < pcm.Length; i++)
        {
            long n = ((long)frameIndex * pcm.Length) + i;
            double sum = 0;
            for (int h = 1; h <= Harmonics; h++)
            {
                sum += Math.Sin(2.0 * Math.PI * 150.0 * h * n / 48000.0) / h;
            }

            pcm[i] = (short)Math.Round(6000.0 * sum / norm);
        }

        return pcm;
    }
}
