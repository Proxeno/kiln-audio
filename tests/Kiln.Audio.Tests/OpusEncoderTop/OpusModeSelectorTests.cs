using FluentAssertions;
using Kiln.Audio;
using Kiln.Audio.Encoder;

namespace Kiln.Audio.Tests.OpusEncoderTop;

/// <summary>
/// The mode and bandwidth decision table at the reference's documented thresholds
/// (RFC 6716 Appendix A, <c>opus_encoder.c</c>).
/// </summary>
public class OpusModeSelectorTests
{
    // Mono Voip, 20 ms, complexity 5, VBR: equivalent rate equals the bitrate.
    // mode: 24000 + ((115*115*(48000-24000)) >> 14) = 43372
    private const int MonoVoipModeThreshold = 43372;

    // Bandwidth pairs, voice estimate 115: NB<->MB 11578, MB<->WB 14771, WB<->SWB 21578,
    // SWB<->FB 29771, with hysteresis 1000, 1192, 2000, 2000.
    private const int MonoVoipNbMb = 11578;
    private const int MonoVoipMbWb = 14771;
    private const int MonoVoipWbSwb = 21578;
    private const int MonoVoipSwbFb = 29771;

    [Fact]
    public void Mono_voip_selects_celt_at_the_mode_threshold()
    {
        // Cap the bandwidth at wideband so a SILK decision stays SILK rather than becoming hybrid.
        new OpusModeSelector().Decide(Request(MonoVoipModeThreshold - 1) with { MaxBandwidth = OpusBandwidth.Wideband })
            .Mode.Should().Be(OpusMode.Silk);
        new OpusModeSelector().Decide(Request(MonoVoipModeThreshold) with { MaxBandwidth = OpusBandwidth.Wideband })
            .Mode.Should().Be(OpusMode.Celt);
    }

    [Fact]
    public void Mono_audio_selects_celt_at_its_own_mode_threshold()
    {
        // Audio: 24000 + ((48*48*(48000-24000)) >> 14) = 27375
        const int threshold = 27375;

        new OpusModeSelector().Decide(
                Request(threshold - 1, OpusApplication.Audio) with { MaxBandwidth = OpusBandwidth.Wideband })
            .Mode.Should().Be(OpusMode.Silk);
        new OpusModeSelector().Decide(
                Request(threshold, OpusApplication.Audio) with { MaxBandwidth = OpusBandwidth.Wideband })
            .Mode.Should().Be(OpusMode.Celt);
    }

    [Fact]
    public void A_previous_celt_frame_lowers_the_threshold_and_a_previous_silk_frame_raises_it()
    {
        var afterCelt = new OpusModeSelector();
        afterCelt.Decide(Request(60000) with { MaxBandwidth = OpusBandwidth.Wideband }).Mode.Should().Be(OpusMode.Celt);
        // 43372 - 4000 = 39372: a rate that a first SILK frame would have kept in SILK.
        afterCelt.Decide(Request(40000) with { MaxBandwidth = OpusBandwidth.Wideband }).Mode.Should().Be(OpusMode.Celt);
        new OpusModeSelector().Decide(Request(40000) with { MaxBandwidth = OpusBandwidth.Wideband })
            .Mode.Should().Be(OpusMode.Silk);

        var afterSilk = new OpusModeSelector();
        afterSilk.Decide(Request(20000)).Mode.Should().Be(OpusMode.Silk);
        // 43372 + 4000 = 47372: a rate a first frame would have made CELT.
        afterSilk.Decide(Request(45000) with { MaxBandwidth = OpusBandwidth.Wideband }).Mode.Should().Be(OpusMode.Silk);
        new OpusModeSelector().Decide(Request(45000) with { MaxBandwidth = OpusBandwidth.Wideband })
            .Mode.Should().Be(OpusMode.Celt);
    }

    [Fact]
    public void Frames_shorter_than_10_ms_force_celt()
    {
        OpusModeDecision decision = new OpusModeSelector().Decide(Request(8000) with
        {
            FrameDuration = OpusFrameDuration.Ms5,
        });

        decision.Mode.Should().Be(OpusMode.Celt);
    }

    [Fact]
    public void Restricted_low_delay_always_selects_celt()
    {
        OpusModeDecision decision = new OpusModeSelector().Decide(Request(8000) with
        {
            Application = OpusApplication.RestrictedLowDelay,
        });

        decision.Mode.Should().Be(OpusMode.Celt);
    }

    [Theory]
    [InlineData(30000, (int)OpusBandwidth.Fullband)]
    [InlineData(25000, (int)OpusBandwidth.SuperWideband)]
    [InlineData(16000, (int)OpusBandwidth.Wideband)]
    [InlineData(12000, (int)OpusBandwidth.Mediumband)]
    [InlineData(11000, (int)OpusBandwidth.Narrowband)]
    public void The_first_frame_uses_the_interpolated_bandwidth_thresholds(int bitrate, int expected)
    {
        OpusModeDecision decision = new OpusModeSelector().Decide(Request(bitrate));

        decision.Bandwidth.Should().Be((OpusBandwidth)expected);
    }

    [Fact]
    public void The_thresholds_are_the_documented_values()
    {
        // Walk the boundaries one bit at a time.
        new OpusModeSelector().Decide(Request(MonoVoipNbMb)).Bandwidth.Should().Be(OpusBandwidth.Mediumband);
        new OpusModeSelector().Decide(Request(MonoVoipNbMb - 1)).Bandwidth.Should().Be(OpusBandwidth.Narrowband);
        new OpusModeSelector().Decide(Request(MonoVoipMbWb)).Bandwidth.Should().Be(OpusBandwidth.Wideband);
        new OpusModeSelector().Decide(Request(MonoVoipMbWb - 1)).Bandwidth.Should().Be(OpusBandwidth.Mediumband);
        new OpusModeSelector().Decide(Request(MonoVoipWbSwb)).Bandwidth.Should().Be(OpusBandwidth.SuperWideband);
        new OpusModeSelector().Decide(Request(MonoVoipWbSwb - 1)).Bandwidth.Should().Be(OpusBandwidth.Wideband);
        new OpusModeSelector().Decide(Request(MonoVoipSwbFb)).Bandwidth.Should().Be(OpusBandwidth.Fullband);
        new OpusModeSelector().Decide(Request(MonoVoipSwbFb - 1)).Bandwidth.Should().Be(OpusBandwidth.SuperWideband);
    }

    [Fact]
    public void Bandwidth_hysteresis_keeps_a_frame_near_the_threshold()
    {
        var selector = new OpusModeSelector();
        selector.Decide(Request(16000)).Bandwidth.Should().Be(OpusBandwidth.Wideband);

        // 14771 - 1192 = 13579 with the WB hysteresis; a fresh frame at 14000 would drop to MB.
        selector.Decide(Request(14000) with { SilkAllowsBandwidthSwitch = true })
            .Bandwidth.Should().Be(OpusBandwidth.Wideband);
        new OpusModeSelector().Decide(Request(14000)).Bandwidth.Should().Be(OpusBandwidth.Mediumband);

        // 11578 - 1000 = 10578 keeps MB at 11000; a fresh frame would use NB.
        var lower = new OpusModeSelector();
        lower.Decide(Request(12000)).Bandwidth.Should().Be(OpusBandwidth.Mediumband);
        lower.Decide(Request(11000) with { SilkAllowsBandwidthSwitch = true })
            .Bandwidth.Should().Be(OpusBandwidth.Mediumband);

        // 14771 + 1192 = 15963 still admits WB from MB at 16000.
        var rising = new OpusModeSelector();
        rising.Decide(Request(12000)).Bandwidth.Should().Be(OpusBandwidth.Mediumband);
        rising.Decide(Request(16000) with { SilkAllowsBandwidthSwitch = true })
            .Bandwidth.Should().Be(OpusBandwidth.Wideband);
    }

    [Fact]
    public void Cbr_lowers_the_silk_bandwidth_rate_by_one_kbps()
    {
        new OpusModeSelector().Decide(Request(12000)).Bandwidth.Should().Be(OpusBandwidth.Mediumband);
        new OpusModeSelector().Decide(Request(12000) with { UseVbr = false })
            .Bandwidth.Should().Be(OpusBandwidth.Narrowband);
    }

    [Fact]
    public void Complexity_scales_the_silk_bandwidth_rate()
    {
        new OpusModeSelector().Decide(Request(12000) with { Complexity = 0 })
            .Bandwidth.Should().Be(OpusBandwidth.Narrowband);
        new OpusModeSelector().Decide(Request(12000) with { Complexity = 10 })
            .Bandwidth.Should().Be(OpusBandwidth.Mediumband);
    }

    [Fact]
    public void Silk_above_wideband_becomes_hybrid()
    {
        OpusModeDecision decision = new OpusModeSelector().Decide(Request(30000));

        decision.Mode.Should().Be(OpusMode.Hybrid);
        decision.Bandwidth.Should().Be(OpusBandwidth.Fullband);
    }

    [Fact]
    public void The_maximum_bandwidth_caps_the_decision()
    {
        OpusModeDecision decision = new OpusModeSelector().Decide(Request(60000) with
        {
            MaxBandwidth = OpusBandwidth.Wideband,
        });

        decision.Mode.Should().Be(OpusMode.Celt);
        decision.Bandwidth.Should().Be(OpusBandwidth.Wideband);
    }

    [Fact]
    public void A_forced_bandwidth_wins_and_celt_mediumband_becomes_wideband()
    {
        new OpusModeSelector().Decide(Request(60000) with { ForcedBandwidth = OpusBandwidth.Narrowband })
            .Bandwidth.Should().Be(OpusBandwidth.Narrowband);

        OpusModeDecision mediumband = new OpusModeSelector().Decide(Request(60000) with
        {
            ForcedBandwidth = OpusBandwidth.Mediumband,
        });
        mediumband.Mode.Should().Be(OpusMode.Celt);
        mediumband.Bandwidth.Should().Be(OpusBandwidth.Wideband);
    }

    [Fact]
    public void A_celt_to_silk_transition_requests_redundancy()
    {
        var selector = new OpusModeSelector();
        selector.Decide(Request(60000)).Mode.Should().Be(OpusMode.Celt);

        OpusModeDecision decision = selector.Decide(Request(20000));

        decision.Mode.Should().Be(OpusMode.Hybrid);
        decision.Redundancy.Should().BeTrue();
        decision.CeltToSilk.Should().BeTrue();
        decision.ToCelt.Should().BeFalse();
    }

    [Fact]
    public void A_silk_to_celt_transition_stays_one_more_frame_in_silk()
    {
        var selector = new OpusModeSelector();
        selector.Decide(Request(20000)).Mode.Should().Be(OpusMode.Silk);

        OpusModeDecision decision = selector.Decide(Request(60000));

        decision.Mode.Should().Be(OpusMode.Silk);
        decision.Redundancy.Should().BeTrue();
        decision.CeltToSilk.Should().BeFalse();
        decision.ToCelt.Should().BeTrue();
        selector.PreviousMode.Should().Be(OpusMode.Celt);

        // The following frame is a plain CELT frame.
        OpusModeDecision next = selector.Decide(Request(60000));
        next.Mode.Should().Be(OpusMode.Celt);
        next.Redundancy.Should().BeFalse();
    }

    [Fact]
    public void A_silk_to_celt_transition_shorter_than_10_ms_carries_no_redundancy()
    {
        var selector = new OpusModeSelector();
        selector.Decide(Request(20000) with { FrameDuration = OpusFrameDuration.Ms20 });

        OpusModeDecision decision = selector.Decide(Request(60000) with
        {
            FrameDuration = OpusFrameDuration.Ms5,
        });

        decision.Mode.Should().Be(OpusMode.Celt);
        decision.Redundancy.Should().BeFalse();
        decision.ToCelt.Should().BeFalse();
    }

    [Fact]
    public void A_silk_bandwidth_switch_forces_a_celt_to_silk_redundancy()
    {
        OpusModeDecision decision = new OpusModeSelector().Decide(Request(20000) with
        {
            SilkBandwidthSwitch = true,
        });

        decision.Redundancy.Should().BeTrue();
        decision.CeltToSilk.Should().BeTrue();
    }

    [Fact]
    public void Stereo_is_chosen_above_the_stereo_threshold()
    {
        OpusModeDecision loud = new OpusModeSelector().Decide(Request(60000) with { Channels = 2 });
        loud.StreamChannels.Should().Be(2);

        OpusModeDecision quiet = new OpusModeSelector().Decide(Request(10000) with { Channels = 2 });
        quiet.StreamChannels.Should().Be(1);
    }

    [Fact]
    public void Invalid_requests_are_rejected()
    {
        ((Action)(() => new OpusModeSelector().Decide(Request(0)))).Should()
            .Throw<ArgumentOutOfRangeException>();
        ((Action)(() => new OpusModeSelector().Decide(Request(20000) with { Channels = 3 }))).Should()
            .Throw<ArgumentOutOfRangeException>();
        ((Action)(() => new OpusModeSelector().Decide(Request(20000) with { Complexity = 11 }))).Should()
            .Throw<ArgumentOutOfRangeException>();
        ((Action)(() => new OpusModeSelector().Decide(Request(20000) with { VoiceEstimate = 128 }))).Should()
            .Throw<ArgumentOutOfRangeException>();
        ((Action)(() => new OpusModeSelector().Decide(Request(20000) with { PacketLossPercent = 101 }))).Should()
            .Throw<ArgumentOutOfRangeException>();
        ((Action)(() => new OpusModeSelector().Decide(Request(20000) with { MaxDataBytes = -1 }))).Should()
            .Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Reset_restores_first_frame_behaviour()
    {
        var selector = new OpusModeSelector();
        selector.Decide(Request(60000)).Mode.Should().Be(OpusMode.Celt);
        selector.Reset();
        selector.PreviousMode.Should().BeNull();
        selector.Decide(Request(20000)).Mode.Should().Be(OpusMode.Silk);
    }

    private static OpusModeRequest Request(
        int bitrate, OpusApplication application = OpusApplication.Voip) =>
        new(bitrate, application, channels: 1, OpusFrameDuration.Ms20);
}
