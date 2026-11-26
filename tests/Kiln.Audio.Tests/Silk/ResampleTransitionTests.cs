using System;
using System.Collections.Generic;
using FluentAssertions;
using Kiln.Audio;
using Kiln.Audio.Entropy;
using Kiln.Audio.Silk;
using Kiln.Audio.Tests.Oracle;
using Xunit.Abstractions;

namespace Kiln.Audio.Tests.Silk;

/// <summary>
/// Checks that a mono-to-stereo channel change carries the mid channel's decoder and synthesis
/// state, as the reference decoder does (RFC 6716 §4.5.2 resets only on a rate or mode change;
/// RFC 8251 §3 resets only the stereo state).
/// </summary>
/// <remarks>
/// RFC 8251 vectors 02, 03 and 04 are mono for their first half and stereo for the second, so
/// they exercise exactly this transition. Decoding the whole vector at the internal rate and
/// comparing with <c>opus_demo</c> shows whether the mid channel continued: a decoder that
/// resets both channels restarts the stereo half from silence and differs for about 100 ms.
/// </remarks>
public class ResampleTransitionTests
{
    private readonly ITestOutputHelper _output;

    public ResampleTransitionTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>Copy-path delay of libopus's internal-rate decode, in samples per channel.</summary>
    private static (int Mono, int Stereo) CopyDelay(int rate) => rate switch
    {
        8000 => (5, 4),
        12000 => (10, 9),
        _ => (13, 12),
    };

    [Theory]
    [InlineData(2, 8000)]
    [InlineData(3, 12000)]
    [InlineData(4, 16000)]
    public void Mono_to_stereo_transition_keeps_the_mid_channel(int vector, int rate)
    {
        if (!OracleTestSupport.OracleAvailable())
        {
            return;
        }

        var packets = new List<byte[]>();
        foreach ((byte[] packet, _) in OpusBitFile.Read(TestVectors.BitPath(vector)))
        {
            packets.Add(packet);
        }

        int switchIndex = packets.FindIndex(packet => ((packet[0] >> 2) & 1) == 1);
        switchIndex.Should().BeGreaterThan(0, "vector {0} must contain a mono half", vector);

        int monoFrames = 0;
        for (int i = 0; i < switchIndex; i++)
        {
            int config = packets[i][0] >> 3;
            int durationMs = (config % 4) switch { 0 => 10, 1 => 20, 2 => 40, _ => 60 };
            monoFrames += durationMs * rate / 1000;
        }

        short[] ours = DecodeInternal(packets, rate);
        short[] reference = LibOpusOracle.Decode(TestVectors.BitPath(vector), rate, 2);
        ours.Length.Should().Be(reference.Length, "vector {0}: sample count", vector);

        (int monoDelay, int stereoDelay) = CopyDelay(rate);
        int monoShift = monoDelay * 2;
        int stereoShift = stereoDelay * 2;

        // The mono half must be bit-exact after libopus's copy delay.
        int monoMismatch = FirstMismatch(ours, reference, 0, monoFrames * 2, monoShift);
        monoMismatch.Should().Be(
            -1, "vector {0}: the mono half must be bit-exact, first mismatch {1}", vector, monoMismatch);

        // The stereo half must also be bit-exact after its (one sample smaller) copy delay. The
        // first few samples differ because libopus's side-channel start-up is not fully modelled;
        // skip 50 ms and require an exact match from there.
        int skip = (rate / 20) * 2;
        int stereoStart = (monoFrames * 2) + skip;
        int stereoEnd = ours.Length - stereoShift;
        int stereoMismatch = FirstMismatch(ours, reference, stereoStart, stereoEnd, stereoShift);
        stereoMismatch.Should().Be(
            -1,
            "vector {0}: after the mono-to-stereo transition the stereo half must be bit-exact, first mismatch {1}",
            vector,
            stereoMismatch);

        _output.WriteLine(FormattableString.Invariant(
            $"vector {vector} ({rate} Hz): mono half exact at {monoDelay}/ch, stereo half exact at {stereoDelay}/ch after {skip / 2} frames"));
    }

    private static int FirstMismatch(short[] ours, short[] reference, int start, int end, int shift)
    {
        for (int i = start; i < end; i++)
        {
            if (ours[i] != reference[i + shift])
            {
                return i;
            }
        }

        return -1;
    }

    private static short[] DecodeInternal(List<byte[]> packets, int rate)
    {
        var pcm = new List<short>();
        var frames = new Range[OpusPacket.MaxFrames];
        var decoder = new SilkDecoder();
        var synthesis = new SilkSynthesis();
        var result = new SilkFrameResult();
        foreach (byte[] packet in packets)
        {
            OpusToc toc = OpusToc.Parse(packet[0]);
            int channels = toc.IsStereo ? 2 : 1;
            OpusPacket.TryParse(packet, frames, out int frameCount, out _, out _);
            int durationMs = toc.FrameDuration switch
            {
                OpusFrameDuration.Ms10 => 10,
                OpusFrameDuration.Ms20 => 20,
                OpusFrameDuration.Ms40 => 40,
                _ => 60,
            };

            for (int f = 0; f < frameCount; f++)
            {
                var range = new RangeDecoder(packet.AsSpan(frames[f]));
                decoder.DecodeFrame(ref range, rate, channels, durationMs, result);
                var framePcm = new short[durationMs * rate / 1000 * channels];
                synthesis.Synthesize(result, framePcm);
                if (channels == 1)
                {
                    foreach (short sample in framePcm)
                    {
                        pcm.Add(sample);
                        pcm.Add(sample);
                    }
                }
                else
                {
                    pcm.AddRange(framePcm);
                }
            }
        }

        return pcm.ToArray();
    }
}
