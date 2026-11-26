namespace Kiln.Audio.Fuzz;

/// <summary>The resilience contract every fuzz target asserts.</summary>
internal static class FuzzContract
{
    /// <summary>
    /// The per-packet time bound. A legitimate decode is milliseconds; two seconds is a generous
    /// ceiling that still turns an accidental super-linear loop into a finding instead of a hang.
    /// </summary>
    internal static readonly TimeSpan PacketTimeLimit = TimeSpan.FromSeconds(2);

    /// <summary>Throws a parser failure when <paramref name="samples"/> contains a non-finite value.</summary>
    /// <param name="samples">PCM to check.</param>
    /// <param name="packet">The input being decoded.</param>
    /// <param name="sampleRate">The decoder's API rate.</param>
    /// <param name="channels">The decoder's channel count.</param>
    /// <param name="decodeFec">Whether FEC decoding was requested.</param>
    internal static void RequireFinite(
        ReadOnlySpan<float> samples,
        byte[] packet,
        int sampleRate,
        int channels,
        bool decodeFec)
    {
        for (int i = 0; i < samples.Length; i++)
        {
            if (!float.IsFinite(samples[i]))
            {
                throw new FuzzFailureException(
                    FuzzTargetKind.Decoder,
                    packet,
                    FormattableString.Invariant($"PCM sample {i} is {samples[i]}."),
                    null)
                {
                    SampleRate = sampleRate,
                    Channels = channels,
                    DecodeFec = decodeFec,
                };
            }
        }
    }
}
