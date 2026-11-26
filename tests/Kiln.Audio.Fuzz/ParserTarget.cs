using System.Diagnostics;

namespace Kiln.Audio.Fuzz;

/// <summary>
/// Checks <see cref="OpusPacket.TryParse"/> against the framing contract of RFC 6716 §3.2–§3.4.
/// </summary>
/// <remarks>
/// Every byte string is legal input. The parser may report <see cref="OpusStatus.Ok"/> or
/// <see cref="OpusStatus.InvalidPacket"/>, but it must never throw and must never report a frame
/// range outside the packet, a frame over 1275 bytes, or more than 120 ms of audio.
/// </remarks>
internal static class ParserTarget
{
    /// <summary>Runs the parser contract checks over <paramref name="packet"/>.</summary>
    /// <param name="packet">The packet to parse; any bytes are legal.</param>
    /// <param name="rng">Generator state, used to choose how many extra checks to run.</param>
    /// <exception cref="FuzzFailureException">A framing or robustness rule was violated.</exception>
    internal static void Run(byte[] packet, FuzzRng rng)
    {
        long start = Stopwatch.GetTimestamp();
        Span<Range> frames = stackalloc Range[OpusPacket.MaxFrames];
        OpusStatus status;
        int frameCount;
        OpusToc toc;
        int padding;
        try
        {
            status = OpusPacket.TryParse(packet, frames, out frameCount, out toc, out padding);
        }
        catch (Exception exception)
        {
            throw new FuzzFailureException(FuzzTargetKind.Parser, packet, $"TryParse threw {exception.GetType().Name}.", exception);
        }

        Elapsed(start, packet, "TryParse");

        if (status is not (OpusStatus.Ok or OpusStatus.InvalidPacket))
        {
            // A full 48-frame destination always suffices for a valid packet.
            throw new FuzzFailureException(FuzzTargetKind.Parser, packet, FormattableString.Invariant($"TryParse returned {status} with a 48-frame span."));
        }

        if (status == OpusStatus.InvalidPacket)
        {
            if (frameCount != 0)
            {
                throw new FuzzFailureException(FuzzTargetKind.Parser, packet, FormattableString.Invariant($"An invalid packet reported {frameCount} frames."));
            }

            int reported = OpusPacket.GetFrameCount(packet);
            if (reported != 0)
            {
                throw new FuzzFailureException(FuzzTargetKind.Parser, packet, FormattableString.Invariant($"GetFrameCount returned {reported} for an invalid packet."));
            }

            return;
        }

        ValidateOk(packet, frames, frameCount, toc, padding);

        int getCount = OpusPacket.GetFrameCount(packet);
        if (getCount != frameCount)
        {
            throw new FuzzFailureException(FuzzTargetKind.Parser, packet, FormattableString.Invariant($"GetFrameCount returned {getCount} but TryParse returned {frameCount}."));
        }

        // A destination smaller than the frame count must be refused without writing.
        if (frameCount > 1 && rng.Chance(1, 4))
        {
            Span<Range> small = stackalloc Range[frameCount - 1];
            OpusStatus smallStatus;
            try
            {
                smallStatus = OpusPacket.TryParse(packet, small, out _, out _, out _);
            }
            catch (Exception exception)
            {
                throw new FuzzFailureException(FuzzTargetKind.Parser, packet, $"TryParse threw {exception.GetType().Name} for a short span.", exception);
            }

            if (smallStatus != OpusStatus.BufferTooSmall)
            {
                throw new FuzzFailureException(FuzzTargetKind.Parser, packet, FormattableString.Invariant($"A {frameCount - 1}-frame span was not rejected for a {frameCount}-frame packet ({smallStatus})."));
            }
        }
    }

    private static void ValidateOk(byte[] packet, Span<Range> frames, int frameCount, OpusToc toc, int padding)
    {
        if (frameCount is < 1 or > OpusPacket.MaxFrames)
        {
            throw new FuzzFailureException(FuzzTargetKind.Parser, packet, FormattableString.Invariant($"Frame count {frameCount} is outside 1 to 48."));
        }

        if (toc.Config is < 0 or > 31)
        {
            throw new FuzzFailureException(FuzzTargetKind.Parser, packet, FormattableString.Invariant($"TOC config {toc.Config} is outside 0 to 31."));
        }

        if (padding < 0 || padding > packet.Length)
        {
            throw new FuzzFailureException(FuzzTargetKind.Parser, packet, FormattableString.Invariant($"Padding {padding} does not fit a {packet.Length}-byte packet."));
        }

        int expectedCount = toc.FrameCountCode switch
        {
            0 => 1,
            1 or 2 => 2,
            _ => packet.Length > 1 ? packet[1] & 0x3F : 0,
        };
        if (frameCount != expectedCount)
        {
            throw new FuzzFailureException(FuzzTargetKind.Parser, packet, FormattableString.Invariant($"Frame-count code {toc.FrameCountCode} reported {frameCount} frames, expected {expectedCount}."));
        }

        int dataEnd = packet.Length - padding;
        int position = frames[0].Start.Value;
        if (position < 1 || position > dataEnd)
        {
            throw new FuzzFailureException(FuzzTargetKind.Parser, packet, FormattableString.Invariant($"The first frame starts at {position}, outside 1 to {dataEnd}."));
        }

        int samplesPerFrame = OpusPacket.GetSamplesPerFrame(toc, 48000);
        for (int i = 0; i < frameCount; i++)
        {
            int size = frames[i].End.Value - frames[i].Start.Value;
            if (frames[i].Start.Value != position)
            {
                throw new FuzzFailureException(FuzzTargetKind.Parser, packet, FormattableString.Invariant($"Frame {i} starts at {frames[i].Start.Value}, expected {position}."));
            }

            if (size is < 0 or > OpusPacket.MaxFrameBytes)
            {
                throw new FuzzFailureException(FuzzTargetKind.Parser, packet, FormattableString.Invariant($"Frame {i} is {size} bytes; the range is 0 to 1275."));
            }

            if (frames[i].End.Value > dataEnd)
            {
                throw new FuzzFailureException(FuzzTargetKind.Parser, packet, FormattableString.Invariant($"Frame {i} ends at {frames[i].End.Value}, past the frame data at {dataEnd}."));
            }

            position = frames[i].End.Value;
        }

        if (position != dataEnd)
        {
            throw new FuzzFailureException(FuzzTargetKind.Parser, packet, FormattableString.Invariant($"Frames cover {position} bytes but {dataEnd} are frame data."));
        }

        int totalSamples48 = frameCount * samplesPerFrame;
        if (totalSamples48 > 5760)
        {
            throw new FuzzFailureException(FuzzTargetKind.Parser, packet, FormattableString.Invariant($"The packet lasts {totalSamples48 * 1000 / 48000} ms; the maximum is 120 ms."));
        }
    }

    private static void Elapsed(long start, byte[] packet, string operation)
    {
        if (Stopwatch.GetElapsedTime(start) > FuzzContract.PacketTimeLimit)
        {
            throw new FuzzFailureException(FuzzTargetKind.Parser, packet, FormattableString.Invariant($"{operation} took longer than {FuzzContract.PacketTimeLimit.TotalSeconds:0} s."));
        }
    }
}
