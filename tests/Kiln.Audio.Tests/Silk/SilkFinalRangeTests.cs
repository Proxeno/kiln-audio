using FluentAssertions;
using Kiln.Audio;
using Kiln.Audio.Entropy;
using Kiln.Audio.Silk;
using Kiln.Audio.Tests.Oracle;
using Xunit.Abstractions;

namespace Kiln.Audio.Tests.Silk;

/// <summary>
/// Checks the SILK symbol decoder against the encoder's final range, which is bit-exact evidence
/// that every symbol was decoded with the right table, in the right order, with the right state.
/// </summary>
/// <remarks>
/// A SILK-only frame may carry a redundant CELT transition frame (RFC 6716 §4.5.1.1), whose
/// separate range coder is folded into the reference final range. Those packets are identified
/// here and asserted to be exactly the ones that cannot match a SILK-only range until the CELT
/// integration lane decodes them.
/// </remarks>
public class SilkFinalRangeTests
{
    private readonly ITestOutputHelper _output;

    public SilkFinalRangeTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>The vectors that contain SILK-only packets.</summary>
    public static TheoryData<int> SilkVectors => new() { 2, 3, 4, 12 };

    [Theory]
    [MemberData(nameof(SilkVectors))]
    public void Every_non_redundant_SILK_only_packet_ends_with_the_encoder_final_range(int vector)
    {
        var frames = new Range[OpusPacket.MaxFrames];
        var decoder = new SilkDecoder();
        var result = new SilkFrameResult();
        int plainPackets = 0;
        int plainMatched = 0;
        int plainMismatched = 0;
        int redundantPackets = 0;
        int redundantMatched = 0;
        int redundantMismatched = 0;
        long packetIndex = -1;
        bool previousPacketWasSilk = false;
        var redundantPacketIndices = new List<long>();
        var plainMismatchReports = new List<string>();

        foreach ((byte[] packet, uint finalRange) in OpusBitFile.Read(TestVectors.BitPath(vector)))
        {
            packetIndex++;
            OpusToc toc = OpusToc.Parse(packet[0]);
            if (toc.Mode != OpusMode.Silk)
            {
                // Hybrid and CELT packets are skipped: their final range also covers CELT, and
                // the top-level decoder that carries SILK state across modes does not exist yet.
                // Resetting at the next SILK packet is an approximation, valid here because no
                // SILK symbol depends on a previous Opus frame's state: the first frame of each
                // type in a frame is coded absolutely and independently (RFC 6716 §4.2.7.4 and
                // §4.2.7.6.1).
                previousPacketWasSilk = false;
                continue;
            }

            if (!previousPacketWasSilk)
            {
                decoder.Reset();
                previousPacketWasSilk = true;
            }

            OpusStatus status = OpusPacket.TryParse(packet, frames, out int frameCount, out OpusToc parsed, out _);
            status.Should().Be(OpusStatus.Ok, "vector {0:D2}, packet {1}", vector, packetIndex);

            int rate = parsed.Bandwidth switch
            {
                OpusBandwidth.Narrowband => 8000,
                OpusBandwidth.Mediumband => 12000,
                OpusBandwidth.Wideband => 16000,
                _ => throw new InvalidOperationException("A SILK-only packet has an LP bandwidth."),
            };
            int channels = parsed.IsStereo ? 2 : 1;
            int durationMs = parsed.FrameDuration switch
            {
                OpusFrameDuration.Ms10 => 10,
                OpusFrameDuration.Ms20 => 20,
                OpusFrameDuration.Ms40 => 40,
                OpusFrameDuration.Ms60 => 60,
                _ => throw new InvalidOperationException("A SILK-only packet has a 10, 20, 40 or 60 ms frame."),
            };

            for (int frame = 0; frame < frameCount; frame++)
            {
                Range frameRange = frames[frame];
                int frameBytes = frameRange.End.Value - frameRange.Start.Value;
                var dec = new RangeDecoder(packet.AsSpan(frameRange));
                decoder.DecodeFrame(ref dec, rate, channels, durationMs, result);

                // RFC 6716 §4.5.1.1: a SILK-only frame signals redundancy implicitly, by having
                // at least 17 bits left after the SILK layer; §4.5.1.2 then codes the position
                // flag and §4.5.1.3 sizes the redundant CELT frame. The caller reads all three
                // right here, keeping the SILK range for the comparison below.
                uint silkRange = dec.Rng;
                OpusRedundancyInfo redundancy = SilkDecoder.DecodeRedundancy(ref dec, frameBytes);
                if (frame != frameCount - 1)
                {
                    continue;
                }

                bool matches = silkRange == finalRange;
                if (redundancy.Present)
                {
                    // The reference final range is the main coder's range XOR the redundant CELT
                    // coder's range, and that CELT frame is lane CELT-DEC-1's to decode. These
                    // packets are recorded and asserted to be exactly the non-matching ones.
                    redundantPackets++;
                    redundantPacketIndices.Add(packetIndex);
                    if (matches)
                    {
                        redundantMatched++;
                    }
                    else
                    {
                        redundantMismatched++;
                    }
                }
                else
                {
                    plainPackets++;
                    if (matches)
                    {
                        plainMatched++;
                    }
                    else
                    {
                        plainMismatched++;
                        if (plainMismatchReports.Count < 20)
                        {
                            int bitsLeft = (dec.StorageBytes * 8) - dec.Tell();
                            plainMismatchReports.Add(FormattableString.Invariant(
                                $"packet {packetIndex}, config {parsed.Config}, {parsed.Bandwidth}, {durationMs} ms, {channels} ch, {frameCount} frame(s), {dec.StorageBytes} bytes, {bitsLeft} bits left"));
                        }
                    }
                }
            }
        }

        int silkPackets = plainPackets + redundantPackets;
        if (redundantPackets > 0)
        {
            _output.WriteLine(
                "vector {0:D2}: {1}/{2} SILK-only packets without redundancy are final-range exact; " +
                "{3} packets carry redundancy (packets {4}) and wait for the CELT integration lane",
                vector, plainMatched, plainPackets, redundantPackets,
                string.Join(", ", redundantPacketIndices));
        }
        else
        {
            _output.WriteLine(
                "vector {0:D2}: {1}/{2} SILK-only packets without redundancy are final-range exact; none carry redundancy",
                vector, plainMatched, plainPackets);
        }

        silkPackets.Should().BeGreaterThan(0, "vector {0:D2} must contain SILK-only packets", vector);

        // Every SILK-only packet without redundancy must reproduce the encoder's final range.
        plainMatched.Should().Be(plainPackets, "vector {0:D2}: every non-redundant SILK-only packet must match", vector);
        plainMismatched.Should().Be(
            0,
            "vector {0:D2}: first non-redundant mismatch: {1}",
            vector,
            string.Join("; ", plainMismatchReports));

        // The redundancy set and the non-matching set must be exactly the same set: no
        // non-redundant packet may mismatch (above), and no redundant packet may match.
        redundantMatched.Should().Be(
            0,
            "vector {0:D2}: a frame whose final range folds in the redundant CELT coder cannot match the SILK-only range",
            vector);
        redundantMismatched.Should().Be(
            redundantPackets,
            "vector {0:D2}: every redundant packet must be one of the non-matching packets",
            vector);
    }

    [Fact]
    public void Pulse_split_tree_places_pulses_at_hand_computed_positions()
    {
        // RFC 6716 §4.2.7.8.3. A 16-sample block with 3 pulses, walked by hand:
        //   (16, 3) split 1 -> left 8 gets 1, right 8 gets 2
        //   ( 8, 1) split 0 -> left-left 4 gets 0, left-right 4 gets 1
        //   ( 4, 1) split 1 -> samples 4..5 get 1, samples 6..7 get 0
        //   ( 2, 1) split 1 -> sample 4 gets 1, sample 5 gets 0
        //   ( 8, 2) split 2 -> right-left 4 gets 2, right-right 4 gets 0
        //   ( 4, 2) split 1 -> samples 8..9 get 1, samples 10..11 get 1
        //   ( 2, 1) split 0 -> sample 8 gets 0, sample 9 gets 1
        //   ( 2, 1) split 1 -> sample 10 gets 1, sample 11 gets 0
        // Empty partitions code no symbol, so those splits are simply skipped.
        short[] expected = new short[16];
        expected[4] = 1;
        expected[9] = 1;
        expected[10] = 1;

        byte[] buffer = new byte[64];
        var encoder = new RangeEncoder(buffer);
        EncodePdf(ref encoder, SilkTables.PulseSplit16Pdf[3 - 1], 1);
        EncodePdf(ref encoder, SilkTables.PulseSplit8Pdf[1 - 1], 0);
        EncodePdf(ref encoder, SilkTables.PulseSplit4Pdf[1 - 1], 1);
        EncodePdf(ref encoder, SilkTables.PulseSplit2Pdf[1 - 1], 1);
        EncodePdf(ref encoder, SilkTables.PulseSplit8Pdf[2 - 1], 2);
        EncodePdf(ref encoder, SilkTables.PulseSplit4Pdf[2 - 1], 1);
        EncodePdf(ref encoder, SilkTables.PulseSplit2Pdf[1 - 1], 0);
        EncodePdf(ref encoder, SilkTables.PulseSplit2Pdf[1 - 1], 1);
        encoder.Done();

        encoder.Error.Should().BeFalse();
        var dec = new RangeDecoder(buffer.AsSpan(0, encoder.RangeBytes));
        short[] pulses = new short[16];
        SilkDecoder.DecodeShellBlock(ref dec, 3, pulses);

        pulses.Should().Equal(expected);
        dec.Rng.Should().Be(encoder.Rng, "the split tree must consume exactly the symbols that were encoded");
    }

    [Theory]
    [InlineData(0, 81920)]        // RFC 6716 §4.2.7.4: the minimum Q16 gain.
    [InlineData(1, 96256)]        // (0x1D1C71*1>>16) + 2090 = 2119; log2lin(2119) by hand.
    [InlineData(10, 397312)]      // (0x1D1C71*10>>16) + 2090 = 2381; log2lin(2381) by hand.
    [InlineData(32, 12713984)]    // (0x1D1C71*32>>16) + 2090 = 3021; log2lin(3021) by hand.
    [InlineData(63, 1686110208)]  // RFC 6716 §4.2.7.4: the maximum Q16 gain.
    public void Gain_dequantization_matches_hand_computed_values(int logGain, int expected)
    {
        SilkDecoder.DequantizeGainQ16(logGain).Should().Be(expected);
    }

    [Fact]
    public void Selection_tables_are_validated_when_first_used()
    {
        // Touching the tables runs the static constructor that checks every selection row.
        SilkTables.NlsfStage2SelectNbMb.Should().HaveCount(32);
        SilkTables.NlsfStage2SelectWb.Should().HaveCount(32);
        SilkTables.NlsfStage2SelectNbMb.Should().OnlyContain(row => row.Length == 10);
        SilkTables.NlsfStage2SelectWb.Should().OnlyContain(row => row.Length == 16);
    }

    private static void EncodePdf(ref RangeEncoder encoder, ReadOnlySpan<byte> pdf, int symbol)
    {
        int fl = 0;
        for (int k = 0; k < symbol; k++)
        {
            fl += pdf[k];
        }

        int fh = fl + pdf[symbol];
        encoder.Encode((uint)fl, (uint)fh, 256);
    }
}
