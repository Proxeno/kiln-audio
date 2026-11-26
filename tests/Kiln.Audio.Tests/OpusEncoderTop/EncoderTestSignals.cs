using Kiln.Audio;
using Kiln.Audio.Celt.Encoder;

namespace Kiln.Audio.Tests.OpusEncoderTop;

/// <summary>Valid CELT frames and one-packet decode helpers shared by the top-level encoder tests.</summary>
internal static class EncoderTestSignals
{
    /// <summary>Samples in one 20 ms frame at 48 kHz.</summary>
    internal const int FrameSamples = 960;

    /// <summary>Builds a continuous sine wave of <paramref name="samples"/> samples.</summary>
    internal static short[] Tone(int samples, double frequency = 440, double amplitude = 8000)
    {
        var pcm = new short[samples];
        for (int i = 0; i < samples; i++)
        {
            pcm[i] = (short)(amplitude * Math.Sin(2 * Math.PI * frequency * i / 48000));
        }

        return pcm;
    }

    /// <summary>Encodes <paramref name="count"/> consecutive 20 ms CELT frames with one encoder.</summary>
    /// <returns>The frame payloads and each frame's final range.</returns>
    internal static (byte[][] Frames, uint[] Ranges) EncodeCeltFrames(int count, int bitrate = 32000, int complexity = 5)
    {
        var encoder = new CeltEncoder(OpusFrameDuration.Ms20, bitrate, complexity);
        short[] pcm = Tone(count * FrameSamples);
        var frames = new byte[count][];
        var ranges = new uint[count];
        for (int i = 0; i < count; i++)
        {
            var frame = new byte[encoder.FrameBytes];
            encoder.EncodeFrame(pcm.AsSpan(i * FrameSamples, FrameSamples), frame);
            frames[i] = frame;
            ranges[i] = encoder.FinalRange;
        }

        return (frames, ranges);
    }

    /// <summary>
    /// Encodes a valid two-byte CELT silence frame with a separate encoder. A silent frame reads
    /// no band data, so it is valid whatever the decoder's prior state.
    /// </summary>
    internal static byte[] EncodeSilenceCeltFrame()
    {
        var encoder = new CeltEncoder(OpusFrameDuration.Ms20, bitrateBps: 1, complexity: 0);
        var frame = new byte[encoder.FrameBytes];
        encoder.EncodeFrame(new short[FrameSamples], frame);
        return frame;
    }
}
