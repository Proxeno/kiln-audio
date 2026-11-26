using System;
using System.Collections.Generic;
using System.IO;
using Kiln.Audio;
using Kiln.Audio.Entropy;
using Kiln.Audio.Silk;
using Kiln.Audio.Tests.Oracle;

namespace Kiln.Audio.Tests.Silk;

/// <summary>
/// Decodes a SILK-only <c>.bit</c> file end to end: packets, SILK synthesis at the internal rate,
/// then the <see cref="SilkResampler"/> to 48 kHz stereo PCM.
/// </summary>
/// <remarks>
/// <para>
/// This mirrors what <c>opus_demo -d 48000 2</c> does for the SILK layer. Mono streams are mapped
/// to stereo by duplicating the channel, as libopus does. The resampler is re-created when the
/// internal rate or channel count changes, which also matches libopus: a bandwidth or channel
/// transition resets the SILK state and discards the resampling buffer (RFC 6716 §4.2.9, §4.5.2).
/// </para>
/// <para>
/// The driver refuses packets that are not SILK-only, because the CELT and hybrid modes are out of
/// scope here. Every packet is checked before any sample is produced.
/// </para>
/// </remarks>
internal static class SilkVectorDriver
{
    /// <summary>Output sample rate of the driver.</summary>
    internal const int OutputRate = 48000;

    /// <summary>One decoded packet's position and format in the output stream.</summary>
    /// <param name="PacketIndex">Zero-based packet index in the file.</param>
    /// <param name="FirstFrame">First output frame (per channel) of the packet.</param>
    /// <param name="FrameCount">Number of output frames the packet produced.</param>
    /// <param name="Channels">Channel count of the packet, 1 or 2.</param>
    /// <param name="InternalRateHz">SILK internal rate of the packet.</param>
    internal sealed record Packet(
        int PacketIndex, int FirstFrame, int FrameCount, int Channels, int InternalRateHz);

    /// <summary>The decoded stream and its packet layout.</summary>
    /// <param name="StereoPcm">Interleaved stereo PCM at 48 kHz.</param>
    /// <param name="Channels">Channel count of the first packet; see <see cref="Packets"/> for changes.</param>
    /// <param name="Packets">Per-packet layout, in file order.</param>
    internal sealed record Result(short[] StereoPcm, int Channels, IReadOnlyList<Packet> Packets)
    {
        /// <summary>Number of packets decoded.</summary>
        internal int PacketCount => Packets.Count;
    }

    /// <summary>Decodes a SILK-only <c>.bit</c> file to 48 kHz stereo PCM.</summary>
    /// <param name="path">Path to the packet file.</param>
    /// <returns>The decoded stream.</returns>
    /// <exception cref="InvalidDataException">A packet is not SILK-only or is malformed.</exception>
    internal static Result DecodeFile(string path) => DecodePackets(OpusBitFile.Read(path));

    /// <summary>Decodes an in-memory SILK-only <c>.bit</c> file to 48 kHz stereo PCM.</summary>
    /// <param name="bitFile">Contents of the packet file.</param>
    /// <returns>The decoded stream.</returns>
    /// <exception cref="InvalidDataException">A packet is not SILK-only or is malformed.</exception>
    internal static Result Decode(byte[] bitFile)
    {
        ArgumentNullException.ThrowIfNull(bitFile);
        string path = Path.Combine(
            Path.GetTempPath(), FormattableString.Invariant($"kiln-vector-{Guid.NewGuid():N}.bit"));
        try
        {
            File.WriteAllBytes(path, bitFile);
            return DecodeFile(path);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static Result DecodePackets(IEnumerable<(byte[] Packet, uint FinalRange)> packets)
    {
        var pcm = new List<short>();
        var layout = new List<Packet>();
        var frames = new Range[OpusPacket.MaxFrames];
        var decoder = new SilkDecoder();
        var synthesis = new SilkSynthesis();
        var result = new SilkFrameResult();

        int rate = 0;
        int channels = 0;
        SilkResampler? left = null;
        SilkResampler? right = null;
        int packetIndex = 0;
        int pendingTransition = 0;
        int inputDebt = 0;

        foreach ((byte[] packet, _) in packets)
        {
            if (packet.Length == 0)
            {
                throw new InvalidDataException(FormattableString.Invariant(
                    $"Packet {packetIndex} is empty."));
            }

            OpusToc toc = OpusToc.Parse(packet[0]);
            if (toc.Mode != OpusMode.Silk)
            {
                throw new InvalidDataException(FormattableString.Invariant(
                    $"Packet {packetIndex} is {toc.Mode}, not SILK-only; this driver handles SILK only."));
            }

            int packetRate = toc.Bandwidth switch
            {
                OpusBandwidth.Narrowband => 8000,
                OpusBandwidth.Mediumband => 12000,
                OpusBandwidth.Wideband => 16000,
                _ => throw new InvalidDataException(FormattableString.Invariant(
                    $"Packet {packetIndex} has bandwidth {toc.Bandwidth}, which is not a SILK bandwidth.")),
            };
            int packetChannels = toc.IsStereo ? 2 : 1;

            if (packetRate != rate)
            {
                rate = packetRate;
                channels = packetChannels;
                left = new SilkResampler(rate, OutputRate, channels);
                right = new SilkResampler(rate, OutputRate, channels);
                pendingTransition = 0;
            }
            else if (packetChannels != channels)
            {
                // libopus's mono path holds one extra sample of mid history, so the total
                // delay steps by one input sample when the stream changes between mono and
                // stereo. Apply the step before the resampler (skip or duplicate one input
                // sample) so the filter smooths it, as libopus's resampler does. The filter
                // state itself carries across the change.
                pendingTransition = packetChannels == 2 ? 1 : -1;
                inputDebt += pendingTransition;
                channels = packetChannels;
            }

            OpusStatus status = OpusPacket.TryParse(
                packet, frames, out int frameCount, out OpusToc parsed, out _);
            if (status != OpusStatus.Ok)
            {
                throw new InvalidDataException(FormattableString.Invariant(
                    $"Packet {packetIndex} failed to parse: {status}."));
            }

            int durationMs = parsed.FrameDuration switch
            {
                OpusFrameDuration.Ms10 => 10,
                OpusFrameDuration.Ms20 => 20,
                OpusFrameDuration.Ms40 => 40,
                OpusFrameDuration.Ms60 => 60,
                _ => throw new InvalidDataException(FormattableString.Invariant(
                    $"Packet {packetIndex} has an unsupported frame duration {parsed.FrameDuration}.")),
            };

            int firstFrame = pcm.Count / 2;
            for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
            {
                var range = new RangeDecoder(packet.AsSpan(frames[frameIndex]));
                decoder.DecodeFrame(ref range, rate, channels, durationMs, result);

                int samplesPerChannel = durationMs * rate / 1000;
                var framePcm = new short[samplesPerChannel * channels];
                synthesis.Synthesize(result, framePcm);

                int skip = 0;
                int insert = 0;
                if (pendingTransition > 0)
                {
                    skip = 1;
                    pendingTransition = 0;
                }
                else if (pendingTransition < 0)
                {
                    insert = 1;
                    pendingTransition = 0;
                }

                if (channels == 2)
                {
                    var channel = new short[samplesPerChannel];
                    for (int i = 0; i < samplesPerChannel; i++)
                    {
                        channel[i] = framePcm[2 * i];
                    }

                    short[] delayedLeft = Resample(left!, ApplyTransition(channel, skip, insert));
                    for (int i = 0; i < samplesPerChannel; i++)
                    {
                        channel[i] = framePcm[(2 * i) + 1];
                    }

                    short[] delayedRight = Resample(right!, ApplyTransition(channel, skip, insert));
                    for (int i = 0; i < delayedLeft.Length; i++)
                    {
                        pcm.Add(delayedLeft[i]);
                        pcm.Add(delayedRight[i]);
                    }
                }
                else
                {
                    // Feed both output channels so the side resampler carries the mid history,
                    // as libopus does when it copies channel 0's resampler state to channel 1.
                    short[] source = ApplyTransition(framePcm, skip, insert);
                    short[] delayedLeft = Resample(left!, source);
                    short[] delayedRight = Resample(right!, source);
                    for (int i = 0; i < delayedLeft.Length; i++)
                    {
                        pcm.Add(delayedLeft[i]);
                        pcm.Add(delayedRight[i]);
                    }
                }
            }

            layout.Add(new Packet(
                packetIndex, firstFrame, (pcm.Count / 2) - firstFrame, channels, rate));
            packetIndex++;
        }

        // Restore the skipped or duplicated input samples so the output has one sample per
        // decoded sample. A skipped sample is repaid with silence at the end; a duplicated one
        // is removed from the end.
        if (inputDebt > 0)
        {
            var zeros = new short[inputDebt];
            short[] delayedLeft = Resample(left!, zeros);
            short[] delayedRight = Resample(right!, zeros);
            for (int i = 0; i < delayedLeft.Length; i++)
            {
                pcm.Add(delayedLeft[i]);
                pcm.Add(delayedRight[i]);
            }
        }
        else if (inputDebt < 0)
        {
            int remove = (-inputDebt * (OutputRate / rate)) * 2;
            if (remove > 0 && remove <= pcm.Count)
            {
                pcm.RemoveRange(pcm.Count - remove, remove);
            }
        }

        return new Result(pcm.ToArray(), layout.Count == 0 ? 0 : layout[0].Channels, layout);
    }

    private static short[] ApplyTransition(ReadOnlySpan<short> source, int skip, int insert)
    {
        if (skip == 0 && insert == 0)
        {
            return source.ToArray();
        }

        if (skip > 0)
        {
            source = source[skip..];
        }

        if (insert > 0)
        {
            var output = new short[source.Length + 1];
            if (source.Length > 0)
            {
                output[0] = source[0];
            }

            source.CopyTo(output.AsSpan(1));
            return output;
        }

        return source.ToArray();
    }

    private static short[] Resample(SilkResampler resampler, ReadOnlySpan<short> input)
    {
        var output = new short[resampler.OutputCount(input.Length)];
        resampler.Process(input, output);
        return output;
    }
}
