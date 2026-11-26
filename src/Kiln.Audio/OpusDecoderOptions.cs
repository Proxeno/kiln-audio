namespace Kiln.Audio;

/// <summary>Configuration for an <see cref="OpusDecoder"/>.</summary>
public sealed record OpusDecoderOptions
{
    /// <summary>Output sample rate in Hz: 8000, 12000, 16000, 24000 or 48000. Default 48000.</summary>
    public int SampleRate { get; init; } = 48000;

    /// <summary>Number of interleaved output channels: 1 or 2. Default 1.</summary>
    public int Channels { get; init; } = 1;

    /// <summary>Validates every field.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A value is outside its legal range.</exception>
    public void Validate()
    {
        OpusValidation.ValidateSampleRate(SampleRate, nameof(SampleRate));
        OpusValidation.ValidateChannels(Channels, nameof(Channels));
    }
}
