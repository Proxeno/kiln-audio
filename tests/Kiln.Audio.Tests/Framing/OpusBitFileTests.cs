using System.Globalization;
using FluentAssertions;
using Kiln.Audio;
using Kiln.Audio.Tests.Oracle;

namespace Kiln.Audio.Tests.Framing;

/// <summary>
/// Parses the committed RFC 8251 <c>.bit</c> files and pins the TOC census that later decoder
/// lanes use to choose targets. The census is documented in <c>docs/test-vectors.md</c>.
/// </summary>
public class OpusBitFileTests
{
    private static readonly string[] ModeOrder = ["SILK", "Hybrid", "CELT"];
    private static readonly string[] BandwidthOrder = ["NB", "MB", "WB", "SWB", "FB"];
    private static readonly double[] DurationOrder = [2.5, 5, 10, 20, 40, 60];

    private static readonly VectorCensus[] Census =
    [
        new(2147, "CELT", "FB", "2.5, 5, 10, 20", "stereo"),
        new(1185, "SILK", "NB", "10, 20, 40, 60", "mono+stereo"),
        new(998, "SILK", "MB", "10, 20, 40, 60", "mono+stereo"),
        new(1265, "SILK", "WB", "10, 20, 40, 60", "mono+stereo"),
        new(2037, "Hybrid", "SWB", "10, 20", "mono+stereo"),
        new(1876, "Hybrid", "FB", "10, 20", "mono+stereo"),
        new(4186, "CELT", "NB, WB, SWB, FB", "2.5, 5, 10, 20", "mono+stereo"),
        new(1247, "SILK, CELT", "NB, WB, SWB, FB", "2.5, 5, 10, 20", "mono+stereo"),
        new(1337, "SILK, CELT", "NB, WB, SWB, FB", "2.5, 5, 10, 20", "mono+stereo"),
        new(1912, "Hybrid, CELT", "FB", "2.5, 5, 10, 20", "mono+stereo"),
        new(553, "CELT", "FB", "20", "stereo"),
        new(1332, "SILK, Hybrid", "NB, MB, WB, SWB", "20", "mono"),
    ];

    public static TheoryData<int> Vectors()
    {
        var data = new TheoryData<int>();
        for (int vector = 1; vector <= TestVectors.Count; vector++)
        {
            data.Add(vector);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public void Every_packet_parses_and_ranges_tile_the_payload(int vector)
    {
        var frames = new Range[OpusPacket.MaxFrames];
        long packets = 0;

        foreach ((byte[] packet, _) in OpusBitFile.Read(TestVectors.BitPath(vector)))
        {
            OpusStatus status = OpusPacket.TryParse(packet, frames, out int count, out OpusToc toc, out int padding);
            status.Should().Be(OpusStatus.Ok, "vector {0:D2}, packet {1}", vector, packets);
            count.Should().BeInRange(1, OpusPacket.MaxFrames, "vector {0:D2}, packet {1}", vector, packets);

            int position = frames[0].Start.Value;
            position.Should().BeGreaterThanOrEqualTo(1, "vector {0:D2}, packet {1}", vector, packets);
            for (int i = 0; i < count; i++)
            {
                frames[i].Start.Value.Should().Be(position, "vector {0:D2}, packet {1}, frame {2}", vector, packets, i);
                frames[i].End.Value.Should().BeGreaterThanOrEqualTo(frames[i].Start.Value);
                frames[i].End.Value.Should().BeLessThanOrEqualTo(packet.Length);
                position = frames[i].End.Value;
            }

            position.Should().Be(packet.Length - padding, "vector {0:D2}, packet {1}", vector, packets);
            ((long)count * OpusPacket.GetSamplesPerFrame(toc, 48000))
                .Should().BeLessThanOrEqualTo(OpusPacket.MaxPacketMilliseconds * 48);
            packets++;
        }

        packets.Should().BeGreaterThan(0, "vector {0:D2} must contain packets", vector);
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public void Toc_census_matches_the_committed_vectors(int vector)
    {
        var modes = new HashSet<string>();
        var bandwidths = new HashSet<string>();
        var durations = new HashSet<double>();
        bool anyMono = false;
        bool anyStereo = false;
        int packets = 0;

        foreach ((byte[] packet, _) in OpusBitFile.Read(TestVectors.BitPath(vector)))
        {
            OpusToc toc = OpusToc.Parse(packet[0]);
            modes.Add(ModeName(toc.Mode));
            bandwidths.Add(BandwidthName(toc.Bandwidth));
            durations.Add(DurationMilliseconds(toc.FrameDuration));
            anyMono |= !toc.IsStereo;
            anyStereo |= toc.IsStereo;
            packets++;
        }

        VectorCensus expected = Census[vector - 1];
        packets.Should().Be(expected.Packets, "vector {0:D2} packet count", vector);
        string.Join(", ", ModeOrder.Where(modes.Contains)).Should().Be(expected.Modes, "vector {0:D2} modes", vector);
        string.Join(", ", BandwidthOrder.Where(bandwidths.Contains)).Should().Be(
            expected.Bandwidths, "vector {0:D2} bandwidths", vector);
        string.Join(", ", DurationOrder.Where(durations.Contains).Select(FormatDuration)).Should().Be(
            expected.FrameDurations, "vector {0:D2} frame durations", vector);
        Channels(anyMono, anyStereo).Should().Be(expected.Channels, "vector {0:D2} channels", vector);
    }

    [Fact]
    public void Census_covers_every_vector()
    {
        Census.Should().HaveCount(TestVectors.Count);
    }

    [Fact]
    public void Bit_reader_reports_the_final_range_of_the_first_record()
    {
        // testvector01.bit starts with length 0x000002DB and range 0x16230400.
        (byte[] packet, uint finalRange) = OpusBitFile.Read(TestVectors.BitPath(1)).First();

        packet.Should().HaveCount(0x000002DB);
        finalRange.Should().Be(0x16230400u);
        OpusToc.Parse(packet[0]).Config.Should().Be(31);
    }

    private static string ModeName(OpusMode mode) => mode switch
    {
        OpusMode.Silk => "SILK",
        OpusMode.Hybrid => "Hybrid",
        OpusMode.Celt => "CELT",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown mode."),
    };

    private static string BandwidthName(OpusBandwidth bandwidth) => bandwidth switch
    {
        OpusBandwidth.Narrowband => "NB",
        OpusBandwidth.Mediumband => "MB",
        OpusBandwidth.Wideband => "WB",
        OpusBandwidth.SuperWideband => "SWB",
        OpusBandwidth.Fullband => "FB",
        _ => throw new ArgumentOutOfRangeException(nameof(bandwidth), bandwidth, "Unknown bandwidth."),
    };

    private static double DurationMilliseconds(OpusFrameDuration duration) => duration switch
    {
        OpusFrameDuration.Ms2_5 => 2.5,
        OpusFrameDuration.Ms5 => 5,
        OpusFrameDuration.Ms10 => 10,
        OpusFrameDuration.Ms20 => 20,
        OpusFrameDuration.Ms40 => 40,
        OpusFrameDuration.Ms60 => 60,
        _ => throw new ArgumentOutOfRangeException(nameof(duration), duration, "Unknown frame duration."),
    };

    private static string FormatDuration(double milliseconds) => milliseconds.ToString("0.#", CultureInfo.InvariantCulture);

    private static string Channels(bool anyMono, bool anyStereo) => (anyMono, anyStereo) switch
    {
        (true, true) => "mono+stereo",
        (false, true) => "stereo",
        _ => "mono",
    };

    private sealed record VectorCensus(
        int Packets,
        string Modes,
        string Bandwidths,
        string FrameDurations,
        string Channels);
}
