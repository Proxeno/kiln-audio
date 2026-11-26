using Kiln.Audio;
using Kiln.Audio.Celt;
using Kiln.Audio.Entropy;
using Kiln.Audio.Tests.Oracle;

namespace Kiln.Audio.Tests.Celt;

/// <summary>
/// Minimal CELT-only driver used by the conformance tests: reads a <c>.bit</c> packet stream,
/// splits it with <see cref="OpusPacket"/>, decodes every CELT frame and synthesizes 48 kHz PCM.
/// </summary>
/// <remarks>
/// The driver deliberately supports only CELT-only streams. It maps the TOC configuration to
/// the decoder parameters of RFC 6716 Table 2: <c>LM</c> from the frame duration and the end
/// band from the bandwidth. Packets with two frames (codes 1 and 2) and packets with an
/// arbitrary number of frames (code 3) are handled by decoding each frame in order with the
/// same decoder state.
/// </remarks>
internal static class CeltVectorDriver
{
    /// <summary>Decodes a CELT-only <c>.bit</c> file to interleaved 48 kHz PCM.</summary>
    /// <param name="bitPath">Path to the <c>.bit</c> file.</param>
    /// <param name="outputChannels">Output channels, 1 or 2.</param>
    /// <returns>Interleaved signed 16-bit PCM at 48 kHz.</returns>
    /// <exception cref="InvalidOperationException">
    /// A packet is invalid, is not CELT-only, or uses a CELT duration or bandwidth the driver
    /// does not support.
    /// </exception>
    internal static short[] Decode(string bitPath, int outputChannels = 2)
    {
        ArgumentException.ThrowIfNullOrEmpty(bitPath);
        if (outputChannels is not (1 or 2))
        {
            throw new ArgumentOutOfRangeException(nameof(outputChannels), outputChannels, "Channels must be 1 or 2.");
        }

        var decoder = new CeltDecoder(outputChannels);
        var synthesizer = new CeltSynthesizer(outputChannels);
        var frame = new CeltFrameResult();
        decoder.Reset();
        synthesizer.Reset();

        Span<Range> frames = stackalloc Range[OpusPacket.MaxFrames];
        var pcm = new List<short>()
        {
            Capacity = 1 << 20,
        };
        var framePcm = new short[2 * CeltTables.MaxFrameSamples];
        var floatPcm = new float[2 * CeltTables.MaxFrameSamples];

        long packetIndex = -1;
        foreach ((byte[] packet, _) in OpusBitFile.Read(bitPath))
        {
            packetIndex++;
            OpusStatus status = OpusPacket.TryParse(packet, frames, out int frameCount, out OpusToc toc, out _);
            if (status != OpusStatus.Ok)
            {
                throw new InvalidOperationException(
                    FormattableString.Invariant($"packet {packetIndex}: packet parse failed with {status}."));
            }

            if (toc.Mode != OpusMode.Celt)
            {
                throw new InvalidOperationException(
                    FormattableString.Invariant($"packet {packetIndex}: expected a CELT-only stream, found {toc.Mode}."));
            }

            int lm = toc.FrameDuration switch
            {
                OpusFrameDuration.Ms2_5 => 0,
                OpusFrameDuration.Ms5 => 1,
                OpusFrameDuration.Ms10 => 2,
                OpusFrameDuration.Ms20 => 3,
                _ => throw new InvalidOperationException(
                    FormattableString.Invariant($"packet {packetIndex}: CELT duration {toc.FrameDuration} is invalid.")),
            };
            int endBand = toc.Bandwidth switch
            {
                OpusBandwidth.Narrowband => 13,
                OpusBandwidth.Mediumband => 17,
                OpusBandwidth.Wideband => 17,
                OpusBandwidth.SuperWideband => 19,
                OpusBandwidth.Fullband => 21,
                _ => throw new InvalidOperationException(
                    FormattableString.Invariant($"packet {packetIndex}: unknown bandwidth {toc.Bandwidth}.")),
            };
            int streamChannels = toc.IsStereo ? 2 : 1;

            for (int i = 0; i < frameCount; i++)
            {
                int start = frames[i].Start.Value;
                int size = frames[i].End.Value - start;
                int sampleCount = CeltTables.ShortBlockSamples << lm;
                if (size <= 1)
                {
                    // DTX or a lost frame: the reference decodes these with packet loss
                    // concealment, which is a later lane. Emit silence for the duration.
                    synthesizer.DecodeLost(lm, floatPcm.AsSpan(0, sampleCount * outputChannels));
                }
                else
                {
                    var rangeDecoder = new RangeDecoder(packet.AsSpan(start, size));
                    decoder.DecodeFrame(ref rangeDecoder, size, lm, 0, endBand, streamChannels, frame);
                    sampleCount = frame.SampleCount;
                    synthesizer.Synthesize(frame, floatPcm.AsSpan(0, sampleCount * outputChannels));
                }

                int count = sampleCount * outputChannels;
                for (int j = 0; j < count; j++)
                {
                    framePcm[j] = CeltPcm.ToInt16(floatPcm[j]);
                }

                pcm.AddRange(framePcm.AsSpan(0, count));
            }
        }

        return [.. pcm];
    }
}
