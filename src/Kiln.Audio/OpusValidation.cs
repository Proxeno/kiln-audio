namespace Kiln.Audio;

/// <summary>Shared argument validation for the options records.</summary>
internal static class OpusValidation
{
    internal static void ValidateSampleRate(int sampleRate, string paramName)
    {
        if (sampleRate is not (8000 or 12000 or 16000 or 24000 or 48000))
        {
            throw new ArgumentOutOfRangeException(paramName, sampleRate, "Sample rate must be 8000, 12000, 16000, 24000 or 48000 Hz.");
        }
    }

    internal static void ValidateChannels(int channels, string paramName)
    {
        if (channels is not (1 or 2))
        {
            throw new ArgumentOutOfRangeException(paramName, channels, "Channels must be 1 or 2.");
        }
    }
}
