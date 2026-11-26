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
    /// All three applications currently map onto the same CELT-only settings. Automatic SILK and
    /// hybrid mode selection is added separately.
    /// </remarks>
    public OpusApplication Application { get; init; } = OpusApplication.Voip;

    /// <summary>Target bitrate in bits per second. Default 24000; must be positive.</summary>
    public int BitrateBps { get; init; } = 24000;

    /// <summary>Frame duration. Default <see cref="OpusFrameDuration.Ms20"/>.</summary>
    public OpusFrameDuration FrameDuration { get; init; } = OpusFrameDuration.Ms20;

    /// <summary>Encoder complexity, 0 (fastest) to 10 (best quality). Default 5.</summary>
    public int Complexity { get; init; } = 5;

    /// <summary>
    /// Whether to use variable bitrate. Default false, which keeps the constant-bitrate behaviour
    /// of earlier releases.
    /// </summary>
    public bool UseVbr { get; init; }

    /// <summary>
    /// Whether variable bitrate is constrained by the reference's bit reservoir. Default true.
    /// Ignored when <see cref="UseVbr"/> is false.
    /// </summary>
    public bool ConstrainedVbr { get; init; } = true;

    /// <summary>Whether to emit in-band forward error correction (LBRR). Default false.</summary>
    public bool UseInbandFec { get; init; }

    /// <summary>Whether to enable discontinuous transmission. Default false.</summary>
    public bool UseDtx { get; init; }

    /// <summary>
    /// Optional bandwidth override. For <see cref="OpusApplication.Voip"/> it also selects the
    /// SILK-only path: narrowband (8 kHz internal), mediumband (12 kHz) or wideband (16 kHz).
    /// When null, <see cref="OpusApplication.Voip"/> defaults to wideband. Hybrid and
    /// super-wideband/fullband SILK are not implemented yet.
    /// </summary>
    public OpusBandwidth? Bandwidth { get; init; }

    /// <summary>
    /// Whether the SILK encoder targets a constant packet size. Default false (variable rate).
    /// Ignored by the CELT path, which is always constant-rate.
    /// </summary>
    public bool UseCbr { get; init; }

    /// <summary>Validates every field.</summary>
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
    }
}