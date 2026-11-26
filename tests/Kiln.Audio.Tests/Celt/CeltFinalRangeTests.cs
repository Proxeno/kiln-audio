using FluentAssertions;
using Kiln.Audio;
using Kiln.Audio.Celt;
using Kiln.Audio.Entropy;
using Kiln.Audio.Tests.Oracle;
using Xunit.Abstractions;

namespace Kiln.Audio.Tests.Celt;

/// <summary>
/// Checks the CELT bitstream decoder against the encoder's final range, which is the
/// conformance criterion of RFC 8251: after the last symbol of a frame, the range decoder
/// must hold the same <c>rng</c> value the encoder recorded.
/// </summary>
public class CeltFinalRangeTests
{
    private readonly ITestOutputHelper _output;

    public CeltFinalRangeTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public static TheoryData<int> Vectors() => new() { 1, 7, 11, 8, 9, 10 };

    [Theory]
    [MemberData(nameof(Vectors))]
    public void Every_celt_packet_matches_the_encoder_final_range(int vector)
    {
        Span<Range> frames = new Range[OpusPacket.MaxFrames];
        var result = new CeltFrameResult();
        var decoder = new CeltDecoder(2);
        decoder.Reset();

        long packets = 0;
        long celtPackets = 0;
        long matched = 0;
        string? firstFailure = null;

        foreach ((byte[] packet, uint finalRange) in OpusBitFile.Read(TestVectors.BitPath(vector)))
        {
            long packetIndex = packets++;
            OpusStatus status = OpusPacket.TryParse(packet, frames, out int frameCount, out OpusToc toc, out _);
            status.Should().Be(OpusStatus.Ok, "vector {0:D2}, packet {1}", vector, packetIndex);
            if (toc.Mode != OpusMode.Celt)
            {
                continue;
            }

            celtPackets++;
            int lm = toc.FrameDuration switch
            {
                OpusFrameDuration.Ms2_5 => 0,
                OpusFrameDuration.Ms5 => 1,
                OpusFrameDuration.Ms10 => 2,
                OpusFrameDuration.Ms20 => 3,
                _ => throw new InvalidOperationException("CELT frame duration is 2.5, 5, 10 or 20 ms."),
            };
            int endBand = toc.Bandwidth switch
            {
                OpusBandwidth.Narrowband => 13,
                OpusBandwidth.Mediumband => 17,
                OpusBandwidth.Wideband => 17,
                OpusBandwidth.SuperWideband => 19,
                _ => 21,
            };
            int channels = toc.IsStereo ? 2 : 1;

            uint lastRange = 0;
            for (int frame = 0; frame < frameCount; frame++)
            {
                int start = frames[frame].Start.Value;
                int size = frames[frame].End.Value - start;
                if (size <= 1)
                {
                    // A zero- or one-byte frame is DTX/PLC; the reference decoder reports a
                    // final range of zero for it (RFC 6716 §3.2.1, §4.3.1).
                    lastRange = 0;
                    continue;
                }

                var rangeDecoder = new RangeDecoder(packet.AsSpan(start, size));
                try
                {
                    decoder.DecodeFrame(ref rangeDecoder, size, lm, 0, endBand, channels, result);
                }
                catch (Exception exception) when (exception is not Xunit.Sdk.XunitException)
                {
                    throw new InvalidOperationException(
                        FormattableString.Invariant(
                            $"decode failed: vector {vector:D2}, packet {packetIndex}, frame {frame}/{frameCount}, config {toc.Config}, bandwidth {toc.Bandwidth}, duration {toc.FrameDuration}, stereo {toc.IsStereo}, frame bytes {size}, packet bytes {packet.Length}"),
                        exception);
                }

                rangeDecoder.Error.Should().BeFalse(
                    "vector {0:D2}, packet {1}, frame {2}", vector, packetIndex, frame);
                lastRange = rangeDecoder.Rng;
            }

            if (lastRange == finalRange)
            {
                matched++;
            }
            else if (firstFailure is null)
            {
                firstFailure = FormattableString.Invariant(
                    $"vector {vector:D2} packet {packetIndex}: decoded 0x{lastRange:X8}, expected 0x{finalRange:X8}, config {toc.Config}, {frameCount} frame(s)");
            }
        }

        _output.WriteLine(
            FormattableString.Invariant($"vector {vector:D2}: {matched}/{celtPackets} CELT-only packets matched"));
        if (firstFailure is not null)
        {
            _output.WriteLine("first failure: " + firstFailure);
        }

        celtPackets.Should().BeGreaterThan(0, "vector {0:D2} should contain CELT-only packets", vector);
        matched.Should().Be(celtPackets, "vector {0:D2} must match 100% of its CELT-only packets", vector);
    }
}
