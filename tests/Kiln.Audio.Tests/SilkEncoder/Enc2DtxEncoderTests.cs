using FluentAssertions;
using Kiln.Audio.Silk;

namespace Kiln.Audio.Tests.SilkEncoder;

/// <summary>
/// Checks the VAD-driven DTX state machine (RFC 6716 §2.1.9; Appendix A
/// <c>silk_encode_do_VAD_FIX</c>).
/// </summary>
public class Enc2DtxEncoderTests
{
    private const int SilenceQ8 = 0;
    private const int SpeechQ8 = 200;

    /// <summary>The first 200 ms of silence are coded before DTX starts.</summary>
    [Fact]
    public void Dtx_starts_after_the_speech_lead_in()
    {
        var dtx = new SilkDtxEncoder { UseDtx = true };

        // The counter is checked after incrementing, so counters 1..9 keep DTX off and the
        // tenth silent frame is the first one whose payload can be suppressed.
        for (int frame = 1; frame < SilkDtxEncoder.SpeechFramesBeforeDtx; frame++)
        {
            SilkDtxDecision decision = Step(dtx, SilenceQ8);
            decision.InDtx.Should().BeFalse("frame {0} is still inside the 200 ms lead-in", frame);
            decision.SignalType.Should().Be(SilkSignalType.Inactive);
            decision.VadFlag.Should().BeFalse();
        }

        Step(dtx, SilenceQ8).InDtx.Should().BeTrue();
    }

    /// <summary>A refresh frame is sent before the run exceeds 400 ms.</summary>
    [Fact]
    public void Refresh_frame_restarts_the_dtx_run()
    {
        var dtx = new SilkDtxEncoder { UseDtx = true };
        for (int frame = 1; frame < SilkDtxEncoder.SpeechFramesBeforeDtx; frame++)
        {
            Step(dtx, SilenceQ8);
        }

        int run = 0;
        int longestRun = 0;
        int refreshes = 0;
        for (int frame = 0; frame < 200; frame++)
        {
            if (Step(dtx, SilenceQ8).InDtx)
            {
                run++;
                longestRun = Math.Max(longestRun, run);
            }
            else
            {
                refreshes++;
                run = 0;
            }
        }

        // The first run spans counters 10..30 (21 frames); after each refresh it is exactly
        // MaxConsecutiveDtx frames long (counters 11..30).
        longestRun.Should().Be(SilkDtxEncoder.MaxConsecutiveDtx + 1);
        refreshes.Should().BePositive();
        run.Should().BeLessThanOrEqualTo(SilkDtxEncoder.MaxConsecutiveDtx);
    }

    /// <summary>Speech clears the counters and leaves DTX immediately.</summary>
    [Fact]
    public void Speech_resets_the_dtx_state()
    {
        var dtx = new SilkDtxEncoder { UseDtx = true };
        for (int frame = 0; frame < 20; frame++)
        {
            Step(dtx, SilenceQ8);
        }

        dtx.InDtx.Should().BeTrue();
        SilkDtxDecision speech = Step(dtx, SpeechQ8);
        speech.InDtx.Should().BeFalse();
        speech.VadFlag.Should().BeTrue();
        speech.SignalType.Should().Be(SilkSignalType.Unvoiced);
        dtx.NoSpeechCounter.Should().Be(0);
    }

    /// <summary>DTX only suppresses a packet when every channel suppresses it.</summary>
    [Fact]
    public void Stereo_dtx_requires_both_channels()
    {
        var mid = new SilkDtxEncoder { UseDtx = true };
        var side = new SilkDtxEncoder { UseDtx = true };
        for (int frame = 0; frame < 20; frame++)
        {
            Step(mid, SilenceQ8);
            Step(side, SilenceQ8);
        }

        SilkDtxEncoder.SuppressPacket(mid, side).Should().BeTrue();
        SilkDtxEncoder.SuppressPacket(mid, null).Should().BeTrue();
        Step(side, SpeechQ8);
        SilkDtxEncoder.SuppressPacket(mid, side).Should().BeFalse();
    }

    /// <summary>Without the DTX request, silence is coded normally.</summary>
    [Fact]
    public void Dtx_disabled_never_suppresses()
    {
        var dtx = new SilkDtxEncoder { UseDtx = false };
        for (int frame = 0; frame < 50; frame++)
        {
            Step(dtx, SilenceQ8).InDtx.Should().BeFalse();
        }

        SilkDtxEncoder.SuppressPacket(dtx, null).Should().BeFalse();
    }

    /// <summary>A DTX packet is exactly one byte: the TOC and an empty payload.</summary>
    [Fact]
    public void Dtx_packet_is_one_byte()
    {
        byte[] packet = new byte[4];
        int length = SilkDtxEncoder.WriteDtxPacket(packet, toc: 0x08);

        length.Should().Be(1);
        SilkDtxEncoder.DtxPacketLength.Should().Be(1);
        packet[0].Should().Be(0x08);
        packet[1..].Should().OnlyContain(b => b == 0);
    }

    /// <summary>The state machine allocates nothing.</summary>
    [Fact]
    public void Process_frame_allocates_nothing()
    {
        var dtx = new SilkDtxEncoder { UseDtx = true };
        for (int i = 0; i < 10; i++)
        {
            dtx.BeginPacket();
            dtx.ProcessFrame(SilenceQ8);
            dtx.ProcessFrame(SpeechQ8);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            dtx.BeginPacket();
            dtx.ProcessFrame(SilenceQ8);
            dtx.ProcessFrame(SpeechQ8);
        }

        (GC.GetAllocatedBytesForCurrentThread() - before).Should().Be(0);
    }

    private static SilkDtxDecision Step(SilkDtxEncoder dtx, int speechActivityQ8)
    {
        dtx.BeginPacket();
        return dtx.ProcessFrame(speechActivityQ8);
    }
}
