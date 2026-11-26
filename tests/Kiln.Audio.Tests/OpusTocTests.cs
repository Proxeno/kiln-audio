using FluentAssertions;
using Kiln.Audio;

namespace Kiln.Audio.Tests;

public class OpusTocTests
{
    // RFC 6716 Table 2, transcribed independently of the parser.
    private static readonly (OpusMode Mode, OpusBandwidth Bw, OpusFrameDuration Dur)[] Table =
    [
        (OpusMode.Silk, OpusBandwidth.Narrowband, OpusFrameDuration.Ms10),
        (OpusMode.Silk, OpusBandwidth.Narrowband, OpusFrameDuration.Ms20),
        (OpusMode.Silk, OpusBandwidth.Narrowband, OpusFrameDuration.Ms40),
        (OpusMode.Silk, OpusBandwidth.Narrowband, OpusFrameDuration.Ms60),
        (OpusMode.Silk, OpusBandwidth.Mediumband, OpusFrameDuration.Ms10),
        (OpusMode.Silk, OpusBandwidth.Mediumband, OpusFrameDuration.Ms20),
        (OpusMode.Silk, OpusBandwidth.Mediumband, OpusFrameDuration.Ms40),
        (OpusMode.Silk, OpusBandwidth.Mediumband, OpusFrameDuration.Ms60),
        (OpusMode.Silk, OpusBandwidth.Wideband, OpusFrameDuration.Ms10),
        (OpusMode.Silk, OpusBandwidth.Wideband, OpusFrameDuration.Ms20),
        (OpusMode.Silk, OpusBandwidth.Wideband, OpusFrameDuration.Ms40),
        (OpusMode.Silk, OpusBandwidth.Wideband, OpusFrameDuration.Ms60),
        (OpusMode.Hybrid, OpusBandwidth.SuperWideband, OpusFrameDuration.Ms10),
        (OpusMode.Hybrid, OpusBandwidth.SuperWideband, OpusFrameDuration.Ms20),
        (OpusMode.Hybrid, OpusBandwidth.Fullband, OpusFrameDuration.Ms10),
        (OpusMode.Hybrid, OpusBandwidth.Fullband, OpusFrameDuration.Ms20),
        (OpusMode.Celt, OpusBandwidth.Narrowband, OpusFrameDuration.Ms2_5),
        (OpusMode.Celt, OpusBandwidth.Narrowband, OpusFrameDuration.Ms5),
        (OpusMode.Celt, OpusBandwidth.Narrowband, OpusFrameDuration.Ms10),
        (OpusMode.Celt, OpusBandwidth.Narrowband, OpusFrameDuration.Ms20),
        (OpusMode.Celt, OpusBandwidth.Wideband, OpusFrameDuration.Ms2_5),
        (OpusMode.Celt, OpusBandwidth.Wideband, OpusFrameDuration.Ms5),
        (OpusMode.Celt, OpusBandwidth.Wideband, OpusFrameDuration.Ms10),
        (OpusMode.Celt, OpusBandwidth.Wideband, OpusFrameDuration.Ms20),
        (OpusMode.Celt, OpusBandwidth.SuperWideband, OpusFrameDuration.Ms2_5),
        (OpusMode.Celt, OpusBandwidth.SuperWideband, OpusFrameDuration.Ms5),
        (OpusMode.Celt, OpusBandwidth.SuperWideband, OpusFrameDuration.Ms10),
        (OpusMode.Celt, OpusBandwidth.SuperWideband, OpusFrameDuration.Ms20),
        (OpusMode.Celt, OpusBandwidth.Fullband, OpusFrameDuration.Ms2_5),
        (OpusMode.Celt, OpusBandwidth.Fullband, OpusFrameDuration.Ms5),
        (OpusMode.Celt, OpusBandwidth.Fullband, OpusFrameDuration.Ms10),
        (OpusMode.Celt, OpusBandwidth.Fullband, OpusFrameDuration.Ms20),
    ];

    public static TheoryData<int> AllConfigs()
    {
        var d = new TheoryData<int>();
        for (int i = 0; i < 32; i++)
        {
            d.Add(i);
        }

        return d;
    }

    [Theory]
    [MemberData(nameof(AllConfigs))]
    public void Config_matches_RFC6716_Table2(int config)
    {
        var (mode, bw, dur) = Table[config];
        for (int s = 0; s < 2; s++)
        {
            for (int c = 0; c < 4; c++)
            {
                byte b = (byte)((config << 3) | (s << 2) | c);
                var toc = OpusToc.Parse(b);
                toc.Config.Should().Be(config);
                toc.Mode.Should().Be(mode);
                toc.Bandwidth.Should().Be(bw);
                toc.FrameDuration.Should().Be(dur);
                toc.IsStereo.Should().Be(s == 1);
                toc.FrameCountCode.Should().Be(c);
            }
        }
    }

    [Fact]
    public void Table_has_32_entries()
    {
        Table.Length.Should().Be(32);
    }

    [Fact]
    public void Known_byte_0xFC_is_celt_fullband_20ms_stereo_code0()
    {
        // 0xFC = 11111 1 00
        var toc = OpusToc.Parse(0xFC);
        toc.Config.Should().Be(31);
        toc.Mode.Should().Be(OpusMode.Celt);
        toc.Bandwidth.Should().Be(OpusBandwidth.Fullband);
        toc.FrameDuration.Should().Be(OpusFrameDuration.Ms20);
        toc.IsStereo.Should().BeTrue();
        toc.FrameCountCode.Should().Be(0);
    }
}
