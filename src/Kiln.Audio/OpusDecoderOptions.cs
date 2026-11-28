namespace Kiln.Audio;

/// <summary>Configuration for an <see cref="OpusDecoder"/>.</summary>
public sealed record OpusDecoderOptions
{
    /// <summary>Output sample rate in Hz: 8000, 12000, 16000, 24000 or 48000. Default 48000.</summary>
    public int SampleRate { get; init; } = 48000;

    /// <summary>Number of interleaved output channels: 1 or 2. Default 1.</summary>
    public int Channels { get; init; } = 1;

    /// <summary>
    /// Selects exactness or speed. <see cref="OpusPerformanceMode.Precise"/> (the default) decodes
    /// bit-identically to Kiln 1.0. <see cref="OpusPerformanceMode.Relaxed"/> lets the CELT and
    /// hybrid synthesis use fused multiply-add, reassociated sums and an approximate reciprocal
    /// square root, so its PCM may differ from Precise by floating-point rounding while still
    /// passing RFC 8251 conformance. SILK-only decoding is integer arithmetic and is identical in
    /// both modes. A Relaxed decoder allocates nothing after construction, like a Precise one.
    /// </summary>
    public OpusPerformanceMode PerformanceMode { get; init; } = OpusPerformanceMode.Precise;

    /// <summary>Validates every field.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A value is outside its legal range.</exception>
    public void Validate()
    {
        OpusValidation.ValidateSampleRate(SampleRate, nameof(SampleRate));
        OpusValidation.ValidateChannels(Channels, nameof(Channels));
        if (PerformanceMode is not (OpusPerformanceMode.Precise or OpusPerformanceMode.Relaxed))
        {
            throw new ArgumentOutOfRangeException(
                nameof(PerformanceMode), PerformanceMode, "Performance mode must be Precise or Relaxed.");
        }
    }
}
