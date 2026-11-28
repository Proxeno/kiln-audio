namespace Kiln.Audio;

/// <summary>Configuration for an <see cref="OpusEncoder"/>.</summary>
public sealed record OpusEncoderOptions
{
    /// <summary>Input sample rate in Hz: 8000, 12000, 16000, 24000 or 48000. Default 48000.</summary>
    public int SampleRate { get; init; } = 48000;

    /// <summary>Number of interleaved input channels: 1 or 2. Default 1.</summary>
    public int Channels { get; init; } = 1;

    /// <summary>Intended application. Default <see cref="OpusApplication.Voip"/>.</summary>
    /// <remarks>
    /// <see cref="OpusApplication.Voip"/> and <see cref="OpusApplication.Audio"/> use the automatic
    /// per-frame SILK/hybrid/CELT policy for mono 48 kHz 10 and 20 ms frames without
    /// <see cref="UseDtx"/>. Otherwise <see cref="OpusApplication.Voip"/> uses the SILK-only encoder
    /// when <see cref="Bandwidth"/> is null, narrowband, mediumband or wideband and the frame is 10,
    /// 20, 40 or 60 ms, and CELT-only for everything else. <see cref="OpusApplication.Audio"/>
    /// outside the automatic case and <see cref="OpusApplication.RestrictedLowDelay"/> always use
    /// CELT-only. See <see cref="OpusEncoder"/> for the full description.
    /// </remarks>
    public OpusApplication Application { get; init; } = OpusApplication.Voip;

    /// <summary>Target bitrate in bits per second. Default 24000; must be positive.</summary>
    public int BitrateBps { get; init; } = 24000;

    /// <summary>Frame duration. Default <see cref="OpusFrameDuration.Ms20"/>.</summary>
    public OpusFrameDuration FrameDuration { get; init; } = OpusFrameDuration.Ms20;

    /// <summary>Encoder complexity, 0 (fastest) to 10 (best quality). Default 5.</summary>
    public int Complexity { get; init; } = 5;

    /// <summary>
    /// Whether the CELT encoder uses variable bitrate. Default false, which is constant bitrate.
    /// </summary>
    public bool UseVbr { get; init; }

    /// <summary>
    /// Whether variable bitrate is constrained by the reference's bit reservoir. Default true.
    /// Ignored when <see cref="UseVbr"/> is false.
    /// </summary>
    public bool ConstrainedVbr { get; init; } = true;

    /// <summary>Whether to emit in-band forward error correction (LBRR). Default false.</summary>
    /// <remarks>
    /// Honoured by the automatic SILK/hybrid/CELT path and by the SILK-only path. The
    /// <see cref="OpusEncoder"/> constructor throws <see cref="NotSupportedException"/> when it is
    /// set with options that select the CELT-only path. LBRR is only emitted when
    /// <see cref="PacketLossPercent"/> is positive and the bitrate is above the reference's LBRR
    /// threshold; otherwise enabling the flag has no effect.
    /// </remarks>
    public bool UseInbandFec { get; init; }

    /// <summary>
    /// Expected packet loss in percent, 0 to 100. Default 0, which disables LBRR. Also lowers the
    /// LBRR rate when loss is high (RFC 6716 Appendix A, <c>silk_setup_LBRR</c>).
    /// </summary>
    public int PacketLossPercent { get; init; }

    /// <summary>Whether to enable discontinuous transmission. Default false.</summary>
    /// <remarks>
    /// Honoured only by the SILK-only path (<see cref="OpusApplication.Voip"/>, narrowband to
    /// wideband, 10 to 60 ms frames); setting it disables the automatic path, so mono 48 kHz Voip
    /// then uses SILK-only. The <see cref="OpusEncoder"/> constructor throws
    /// <see cref="NotSupportedException"/> when it is set with options that select the CELT-only path.
    /// </remarks>
    public bool UseDtx { get; init; }

    /// <summary>
    /// Optional bandwidth limit. In the automatic SILK/hybrid/CELT case it is an upper bound on the
    /// audio bandwidth (null means fullband), not a forced value: the encoder still chooses a
    /// narrower bandwidth at low bitrates, as the reference does, and selects fullband only from
    /// roughly 30 to 40 kbit/s depending on complexity and content. In the SILK-only case it is the SILK internal bandwidth: narrowband
    /// (8 kHz), mediumband (12 kHz) or wideband (16 kHz), with null meaning wideband.
    /// <see cref="OpusBandwidth.SuperWideband"/> and <see cref="OpusBandwidth.Fullband"/> outside
    /// the automatic case select the CELT-only path. Must be a defined <see cref="OpusBandwidth"/>
    /// value.
    /// </summary>
    public OpusBandwidth? Bandwidth { get; init; }

    /// <summary>
    /// Whether the SILK encoder targets a constant packet size. Default false (variable rate).
    /// Applies to the SILK layer only; the CELT encoder is controlled by <see cref="UseVbr"/>.
    /// </summary>
    public bool UseCbr { get; init; }

    /// <summary>
    /// Speed/quality trade-off. Default <see cref="OpusPerformanceMode.Precise"/>, which produces
    /// exactly the output of Kiln 1.0. <see cref="OpusPerformanceMode.Relaxed"/> uses cheaper
    /// searches (fewer noise-shaping quantizer states, a cheaper pitch search, a lane-parallel
    /// pulse search and similar) and non-bit-exact math: it encodes faster at a small cost in
    /// signal-to-noise ratio. The packets are standard Opus and any decoder reads them.
    /// </summary>
    public OpusPerformanceMode PerformanceMode { get; init; } = OpusPerformanceMode.Precise;

    /// <summary>
    /// Validates every field, including that <see cref="Application"/>, <see cref="FrameDuration"/>
    /// and a non-null <see cref="Bandwidth"/> are defined enum values.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A value is outside its legal range.</exception>
    public void Validate()
    {
        OpusValidation.ValidateSampleRate(SampleRate, nameof(SampleRate));
        OpusValidation.ValidateChannels(Channels, nameof(Channels));
        if (!Enum.IsDefined(Application))
        {
            throw new ArgumentOutOfRangeException(nameof(Application), Application, "Unknown application.");
        }

        if (BitrateBps <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(BitrateBps), BitrateBps, "Bitrate must be positive.");
        }

        if (!Enum.IsDefined(FrameDuration))
        {
            throw new ArgumentOutOfRangeException(nameof(FrameDuration), FrameDuration, "Unknown frame duration.");
        }

        if (Complexity is < 0 or > 10)
        {
            throw new ArgumentOutOfRangeException(nameof(Complexity), Complexity, "Complexity must be between 0 and 10.");
        }

        if (PacketLossPercent is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(PacketLossPercent), PacketLossPercent, "Packet loss must be between 0 and 100 percent.");
        }

        if (!Enum.IsDefined(PerformanceMode))
        {
            throw new ArgumentOutOfRangeException(nameof(PerformanceMode), PerformanceMode, "Unknown performance mode.");
        }

        if (Bandwidth is { } bandwidth && !Enum.IsDefined(bandwidth))
        {
            throw new ArgumentOutOfRangeException(nameof(Bandwidth), bandwidth, "Unknown bandwidth.");
        }
    }
}