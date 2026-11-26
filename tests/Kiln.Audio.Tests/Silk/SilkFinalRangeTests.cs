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
    public void Every_SILK_only_packet_ends_with_the_encoder_final_range(int vector)
    {
        var frames = new Range[OpusPacket.MaxFrames];
        var decoder = new SilkDecoder();
        var result = new SilkFrameResult();
        int silkPackets = 0;
        int matched = 0;
        long packetIndex = -1;
        long firstMismatch = -1;
        uint firstMismatchActual = 0;
        uint firstMismatchExpected = 0;
        bool previousPacketWasSilk = false;
        var mismatchReports = new List<string>();

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

            silkPackets++;
            for (int frame = 0; frame < frameCount; frame++)
            {
                var dec = new RangeDecoder(packet.AsSpan(frames[frame]));
                decoder.DecodeFrame(ref dec, rate, channels, durationMs, result);

                if (frame == frameCount - 1)
                {
                    if (dec.Rng == finalRange)
                    {
                        matched++;
                    }
                    else
                    {
                        if (firstMismatch < 0)
                        {
                            firstMismatch = packetIndex;
                            firstMismatchActual = dec.Rng;
                            firstMismatchExpected = finalRange;
                        }

                        if (mismatchReports.Count < 20)
                        {
                            // RFC 6716 §4.5.1.1: at least 17 bits left after the SILK layer
                            // means a byte-aligned redundant CELT frame follows, at a bandwidth
                            // transition. Its separate range coder is folded into the encoder's
                            // final range, so a SILK-only decoder cannot reproduce that number.
                            int bitsLeft = (dec.StorageBytes * 8) - dec.Tell();
                            mismatchReports.Add(FormattableString.Invariant(
                                $"packet {packetIndex}, config {parsed.Config}, {parsed.Bandwidth}, {durationMs} ms, {channels} ch, {frameCount} frame(s), {dec.StorageBytes} bytes, {bitsLeft} bits left"));
                        }
                    }
                }
            }
        }

        _output.WriteLine(
            "vector {0:D2}: {1}/{2} SILK-only packets final-range exact",
            vector, matched, silkPackets);

        if (firstMismatch >= 0)
        {
            _output.WriteLine(
                "vector {0:D2}: first mismatch at packet {1}: got 0x{2:X8}, expected 0x{3:X8}",
                vector, firstMismatch, firstMismatchActual, firstMismatchExpected);
            foreach (string report in mismatchReports)
            {
                _output.WriteLine("vector {0:D2}: {1}", vector, report);
            }
        }

        silkPackets.Should().BeGreaterThan(0, "vector {0:D2} must contain SILK-only packets", vector);
        matched.Should().Be(
            silkPackets,
            "vector {0:D2}: every SILK-only packet must reproduce the encoder's final range", vector);
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
