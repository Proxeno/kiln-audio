// Portions derived from the RFC 6716 reference implementation (Appendix A), (c) IETF Trust and the persons identified as authors, Simplified BSD License; see THIRD-PARTY-NOTICES.md.

namespace Kiln.Audio.Encoder;

/// <summary>The inputs to <see cref="OpusModeSelector.Decide"/> for one frame (RFC 6716 §2).</summary>
internal readonly record struct OpusModeRequest
{
    /// <summary>Creates a request with the reference encoder's ordinary defaults.</summary>
    /// <param name="bitrateBps">Target bitrate in bits per second, at least 1.</param>
    /// <param name="application">The encoder application hint.</param>
    /// <param name="channels">Input channels, 1 or 2.</param>
    /// <param name="frameDuration">The frame duration to encode.</param>
    public OpusModeRequest(int bitrateBps, OpusApplication application, int channels, OpusFrameDuration frameDuration)
    {
        BitrateBps = bitrateBps;
        Application = application;
        Channels = channels;
        FrameDuration = frameDuration;
        Complexity = 5;
        UseVbr = true;
        VoiceEstimate = -1;
        MaxBandwidth = OpusBandwidth.Fullband;
    }

    /// <summary>Target bitrate in bits per second.</summary>
    public int BitrateBps { get; init; }

    /// <summary>The encoder application hint.</summary>
    public OpusApplication Application { get; init; }

    /// <summary>Input channels: 1 or 2.</summary>
    public int Channels { get; init; }

    /// <summary>A forced coded channel count: 0 for automatic, 1 for mono or 2 for stereo.</summary>
    public int ForceChannels { get; init; }

    /// <summary>Frame duration.</summary>
    public OpusFrameDuration FrameDuration { get; init; }

    /// <summary>Encoder complexity, 0 to 10. Default 5.</summary>
    public int Complexity { get; init; }

    /// <summary>Whether the encoder is variable-bitrate. Default true, matching the reference.</summary>
    public bool UseVbr { get; init; }

    /// <summary>Whether in-band FEC (LBRR) is enabled.</summary>
    public bool UseInBandFec { get; init; }

    /// <summary>Expected packet loss percentage, 0 to 100.</summary>
    public int PacketLossPercent { get; init; }

    /// <summary>Whether discontinuous transmission is enabled.</summary>
    public bool UseDtx { get; init; }

    /// <summary>
    /// Voice estimate, 0 (music) to 127 (voice). -1, the default, derives it from the
    /// application: 115 for <see cref="OpusApplication.Voip"/> and 48 otherwise.
    /// </summary>
    public int VoiceEstimate { get; init; }

    /// <summary>Upper bound on the bandwidth; default <see cref="OpusBandwidth.Fullband"/>.</summary>
    public OpusBandwidth MaxBandwidth { get; init; }

    /// <summary>A forced bandwidth, or null to select one automatically.</summary>
    public OpusBandwidth? ForcedBandwidth { get; init; }

    /// <summary>
    /// Packet budget in bytes, or 0 when unknown. A known budget enables the reference's low-rate
    /// CELT fallbacks.
    /// </summary>
    public int MaxDataBytes { get; init; }

    /// <summary>Whether the SILK layer has allowed a bandwidth switch in this frame.</summary>
    public bool SilkAllowsBandwidthSwitch { get; init; }

    /// <summary>Whether the SILK layer is in wideband mode with its variable low-pass filter off.</summary>
    public bool SilkInWidebandWithoutVariableLp { get; init; }

    /// <summary>Whether the SILK layer reports that it is ready to switch its internal bandwidth.</summary>
    public bool SilkBandwidthSwitch { get; init; }
}

/// <summary>The mode, bandwidth and transition choices for one frame.</summary>
/// <param name="Mode">The effective coding mode of this frame.</param>
/// <param name="Bandwidth">The audio bandwidth.</param>
/// <param name="StreamChannels">Coded channels: 1 or 2.</param>
/// <param name="Redundancy">Whether this frame carries transition redundancy.</param>
/// <param name="CeltToSilk">Whether the redundant CELT frame belongs at the start of the frame.</param>
/// <param name="ToCelt">Whether the following frame is coded as CELT.</param>
internal readonly record struct OpusModeDecision(
    OpusMode Mode,
    OpusBandwidth Bandwidth,
    int StreamChannels,
    bool Redundancy,
    bool CeltToSilk,
    bool ToCelt);

/// <summary>
/// Chooses the coding mode, bandwidth and transition flags for each frame, transcribed from the
/// reference encoder's rate-dependent logic (RFC 6716 Appendix A, <c>src/opus_encoder.c</c>).
/// </summary>
/// <remarks>
/// <para>
/// The decision compares an equivalent 20 ms bitrate against the reference's documented
/// thresholds. Hysteresis is applied against the previous frame's choices, so a stream near a
/// threshold does not oscillate. The selector is stateful: create one per encoder and call
/// <see cref="Decide"/> once per frame.
/// </para>
/// <para>
/// The encoder input is fixed at 48 kHz, so the reference's per-sample-rate bandwidth caps never
/// bind and are omitted.
/// </para>
/// </remarks>
internal sealed class OpusModeSelector
{
    /// <summary>The encoder's sample rate in Hz.</summary>
    internal const int SampleRate = 48000;

    private const int VoiceThreshold = 48000;
    private const int MusicThreshold = 24000;
    private const int ModeHysteresis = 4000;
    private const int StereoVoiceThreshold = 26000;
    private const int StereoMusicThreshold = 36000;
    private const int StereoHysteresis = 4000;

    // First column: middle threshold for the lower/higher bandwidth pair. Second column:
    // hysteresis. The pairs are NB<->MB, MB<->WB, WB<->SWB and SWB<->FB.
    private static ReadOnlySpan<int> MonoVoiceBandwidth => [11000, 1000, 14000, 1000, 21000, 2000, 29000, 2000];
    private static ReadOnlySpan<int> MonoMusicBandwidth => [14000, 1000, 18000, 2000, 24000, 2000, 33000, 2000];
    private static ReadOnlySpan<int> StereoVoiceBandwidth => [11000, 1000, 14000, 1000, 21000, 2000, 32000, 2000];
    private static ReadOnlySpan<int> StereoMusicBandwidth => [14000, 1000, 18000, 2000, 24000, 2000, 48000, 2000];

    private OpusMode? _previousMode;
    private OpusBandwidth _bandwidth = OpusBandwidth.Fullband;
    private int _streamChannels;
    private bool _first = true;

    /// <summary>Gets the mode chosen for the previous frame, or null before the first decision.</summary>
    internal OpusMode? PreviousMode => _previousMode;

    /// <summary>Gets the bandwidth chosen for the previous frame.</summary>
    internal OpusBandwidth CurrentBandwidth => _bandwidth;

    /// <summary>Restores the selector to its freshly constructed state.</summary>
    internal void Reset()
    {
        _previousMode = null;
        _bandwidth = OpusBandwidth.Fullband;
        _streamChannels = 0;
        _first = true;
    }

    /// <summary>Chooses the mode, bandwidth and transition flags for one frame.</summary>
    /// <param name="request">The frame's inputs.</param>
    /// <returns>The decision.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A request field is outside its legal range.</exception>
    internal OpusModeDecision Decide(in OpusModeRequest request)
    {
        Validate(request);

        int frameSamples = SamplesPerFrame(request.FrameDuration);
        int frameRate = SampleRate / frameSamples;
        if (_streamChannels == 0)
        {
            _streamChannels = request.Channels;
        }

        int streamChannels = DecideStreamChannels(request, frameRate);
        long equivalentRate = request.BitrateBps - (60L * (frameRate - 50));
        int voiceEstimate = request.VoiceEstimate >= 0
            ? request.VoiceEstimate
            : request.Application == OpusApplication.Voip ? 115 : 48;

        OpusMode mode = SelectMode(request, equivalentRate, voiceEstimate);
        if (mode != OpusMode.Celt && frameSamples < SampleRate / 100)
        {
            // 2.5 and 5 ms frames have no SILK or hybrid configuration.
            mode = OpusMode.Celt;
        }

        (mode, bool redundancy, bool celtToSilk, bool toCelt) =
            ApplyTransition(request, mode, frameSamples);

        OpusBandwidth bandwidth = SelectBandwidth(request, mode, equivalentRate, voiceEstimate, frameRate);

        if ((int)bandwidth > (int)request.MaxBandwidth)
        {
            bandwidth = request.MaxBandwidth;
        }

        if (request.ForcedBandwidth is OpusBandwidth forced)
        {
            bandwidth = forced;
        }

        long maxRate = (long)frameRate * request.MaxDataBytes * 8;
        if (mode != OpusMode.Celt && request.MaxDataBytes > 0 && maxRate < 15000)
        {
            // Hybrid is unsafe at very low actual rates.
            bandwidth = Min(bandwidth, OpusBandwidth.Wideband);
        }

        // The encoder input is 48 kHz, so the reference's per-sample-rate caps never bind here.

        if (request.MaxDataBytes > 0)
        {
            int minimumBytes = (frameRate > 50 ? 12000 : 8000) * frameSamples / (SampleRate * 8);
            if (request.MaxDataBytes < minimumBytes)
            {
                mode = OpusMode.Celt;
            }
        }

        if (mode == OpusMode.Celt && bandwidth == OpusBandwidth.Mediumband)
        {
            // CELT has no mediumband configuration (RFC 6716 Table 2).
            bandwidth = OpusBandwidth.Wideband;
        }

        // A SILK frame above wideband becomes hybrid, and hybrid at or below it becomes SILK.
        if (mode == OpusMode.Silk && (int)bandwidth > (int)OpusBandwidth.Wideband)
        {
            mode = OpusMode.Hybrid;
        }
        else if (mode == OpusMode.Hybrid && (int)bandwidth <= (int)OpusBandwidth.Wideband)
        {
            mode = OpusMode.Silk;
        }

        _previousMode = toCelt ? OpusMode.Celt : mode;
        _bandwidth = bandwidth;
        _streamChannels = streamChannels;
        _first = false;

        return new OpusModeDecision(mode, bandwidth, streamChannels, redundancy, celtToSilk, toCelt);
    }

    /// <summary>Selects SILK or CELT from the application, rate thresholds and hysteresis.</summary>
    private OpusMode SelectMode(in OpusModeRequest request, long equivalentRate, int voiceEstimate)
    {
        if (request.Application == OpusApplication.RestrictedLowDelay)
        {
            return OpusMode.Celt;
        }

        long threshold = MusicThreshold
            + (((long)voiceEstimate * voiceEstimate * (VoiceThreshold - MusicThreshold)) >> 14);

        if (_previousMode == OpusMode.Celt)
        {
            threshold -= ModeHysteresis;
        }
        else if (_previousMode is not null)
        {
            threshold += ModeHysteresis;
        }

        OpusMode mode = equivalentRate >= threshold ? OpusMode.Celt : OpusMode.Silk;

        if (request.UseInBandFec && request.PacketLossPercent > (128 - voiceEstimate) >> 4)
        {
            mode = OpusMode.Silk;
        }

        if (request.UseDtx && voiceEstimate > 100)
        {
            mode = OpusMode.Silk;
        }

        return mode;
    }

    /// <summary>Applies the §4.5.1 transition rules and returns the effective mode for this frame.</summary>
    private (OpusMode Mode, bool Redundancy, bool CeltToSilk, bool ToCelt) ApplyTransition(
        in OpusModeRequest request, OpusMode mode, int frameSamples)
    {
        bool redundancy = false;
        bool celtToSilk = false;
        bool toCelt = false;

        if (_previousMode is OpusMode previous
            && ((mode != OpusMode.Celt && previous == OpusMode.Celt)
                || (mode == OpusMode.Celt && previous != OpusMode.Celt)))
        {
            redundancy = true;
            celtToSilk = mode != OpusMode.Celt;
            if (!celtToSilk)
            {
                if (frameSamples >= SampleRate / 100)
                {
                    // Stay in the previous mode for one frame so the redundant CELT frame can
                    // cross-fade at the end of it; the next frame is CELT.
                    mode = previous;
                    toCelt = true;
                }
                else
                {
                    redundancy = false;
                }
            }
        }

        if (request.SilkBandwidthSwitch)
        {
            redundancy = true;
            celtToSilk = true;
        }

        return (mode, redundancy, celtToSilk, toCelt);
    }

    /// <summary>Selects the audio bandwidth from the interpolated threshold tables and hysteresis.</summary>
    private OpusBandwidth SelectBandwidth(
        in OpusModeRequest request, OpusMode mode, long equivalentRate, int voiceEstimate, int frameRate)
    {
        if (mode != OpusMode.Celt && !_first && !request.SilkAllowsBandwidthSwitch)
        {
            return _bandwidth;
        }

        long rate = equivalentRate;
        if (mode != OpusMode.Celt)
        {
            // The reference scales the SILK threshold by complexity and by the CBR penalty.
            rate = (rate * (45 + request.Complexity)) / 50;
            if (!request.UseVbr)
            {
                rate -= 1000;
            }
        }

        bool stereo = request.Channels == 2 && request.ForceChannels != 1;
        ReadOnlySpan<int> voice = stereo ? StereoVoiceBandwidth : MonoVoiceBandwidth;
        ReadOnlySpan<int> music = stereo ? StereoMusicBandwidth : MonoMusicBandwidth;

        OpusBandwidth bandwidth = OpusBandwidth.Fullband;
        while (true)
        {
            int index = 2 * ((int)bandwidth - (int)OpusBandwidth.Mediumband);
            long threshold = Interpolate(music[index], voice[index], voiceEstimate);
            long hysteresis = Interpolate(music[index + 1], voice[index + 1], voiceEstimate);

            if (!_first)
            {
                if ((int)_bandwidth >= (int)bandwidth)
                {
                    threshold -= hysteresis;
                }
                else
                {
                    threshold += hysteresis;
                }
            }

            if (rate >= threshold)
            {
                break;
            }

            bandwidth = (OpusBandwidth)((int)bandwidth - 1);
            if ((int)bandwidth <= (int)OpusBandwidth.Narrowband)
            {
                // Narrowband has no lower pair to test.
                bandwidth = OpusBandwidth.Narrowband;
                break;
            }
        }

        // Do not move to SWB/FB until SILK has fully switched to wideband.
        if (!_first && mode != OpusMode.Celt && !request.SilkInWidebandWithoutVariableLp
            && (int)bandwidth > (int)OpusBandwidth.Wideband)
        {
            bandwidth = OpusBandwidth.Wideband;
        }

        return bandwidth;
    }

    /// <summary>Interpolates a voice/music threshold pair by the squared voice estimate.</summary>
    private static long Interpolate(int music, int voice, int voiceEstimate) =>
        music + (((long)voiceEstimate * voiceEstimate * (voice - music)) >> 14);

    /// <summary>Chooses the coded channel count with the reference's rate-dependent stereo rule.</summary>
    private int DecideStreamChannels(in OpusModeRequest request, int frameRate)
    {
        if (request.ForceChannels != 0 && request.Channels == 2)
        {
            return request.ForceChannels;
        }

        if (request.Channels != 2)
        {
            return request.Channels;
        }

        int voiceEstimate = request.VoiceEstimate >= 0
            ? request.VoiceEstimate
            : request.Application == OpusApplication.Voip ? 115 : 48;
        long threshold = StereoMusicThreshold
            + (((long)voiceEstimate * voiceEstimate * (StereoVoiceThreshold - StereoMusicThreshold)) >> 14);
        threshold += _streamChannels == 2 ? -StereoHysteresis : StereoHysteresis;
        long equivalentRate = request.BitrateBps - (60L * (frameRate - 50));
        return equivalentRate > threshold ? 2 : 1;
    }

    /// <summary>Returns the smaller of two bandwidths.</summary>
    private static OpusBandwidth Min(OpusBandwidth left, OpusBandwidth right) =>
        (int)left < (int)right ? left : right;

    /// <summary>Returns the number of samples in one frame at 48 kHz.</summary>
    private static int SamplesPerFrame(OpusFrameDuration duration) => duration switch
    {
        OpusFrameDuration.Ms2_5 => 120,
        OpusFrameDuration.Ms5 => 240,
        OpusFrameDuration.Ms10 => 480,
        OpusFrameDuration.Ms20 => 960,
        OpusFrameDuration.Ms40 => 1920,
        OpusFrameDuration.Ms60 => 2880,
        _ => throw new ArgumentOutOfRangeException(nameof(duration), duration, "Unknown frame duration."),
    };

    /// <summary>Validates every request field.</summary>
    private static void Validate(in OpusModeRequest request)
    {
        if (request.BitrateBps <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), request.BitrateBps, "Bitrate must be positive.");
        }

        if (request.Application is not (OpusApplication.Voip or OpusApplication.Audio
            or OpusApplication.RestrictedLowDelay))
        {
            throw new ArgumentOutOfRangeException(nameof(request), request.Application, "Unknown application.");
        }

        if (request.Channels is not (1 or 2))
        {
            throw new ArgumentOutOfRangeException(nameof(request), request.Channels, "Channels must be 1 or 2.");
        }

        if (request.ForceChannels is < 0 or > 2)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request), request.ForceChannels, "ForceChannels must be 0 (auto), 1 or 2.");
        }

        if (!Enum.IsDefined(request.FrameDuration))
        {
            throw new ArgumentOutOfRangeException(nameof(request), request.FrameDuration, "Unknown frame duration.");
        }

        if (request.Complexity is < 0 or > 10)
        {
            throw new ArgumentOutOfRangeException(nameof(request), request.Complexity, "Complexity must be 0 to 10.");
        }

        if (request.VoiceEstimate is < -1 or > 127)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request), request.VoiceEstimate, "VoiceEstimate must be -1 (automatic) to 127.");
        }

        if (request.PacketLossPercent is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request), request.PacketLossPercent, "Packet loss must be 0 to 100 percent.");
        }

        if (!Enum.IsDefined(request.MaxBandwidth))
        {
            throw new ArgumentOutOfRangeException(nameof(request), request.MaxBandwidth, "Unknown bandwidth.");
        }

        if (request.ForcedBandwidth is OpusBandwidth forced && !Enum.IsDefined(forced))
        {
            throw new ArgumentOutOfRangeException(nameof(request), forced, "Unknown bandwidth.");
        }

        if (request.MaxDataBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), request.MaxDataBytes, "MaxDataBytes cannot be negative.");
        }
    }
}
