namespace Kiln.Audio.Decoder;

/// <summary>
/// The decoder's performance profile (<see cref="OpusPerformanceMode"/>), fixed per instance and
/// resolved at JIT time: every hot method is generic over a profile, so a Relaxed-only branch is a
/// constant that the JIT folds away in the Precise instantiation.
/// </summary>
internal interface IDecoderProfile
{
    /// <summary>Gets a value indicating whether non-bit-exact floating-point shortcuts are allowed.</summary>
    static abstract bool Relaxed { get; }
}

/// <summary>The default profile: output bit-identical to Kiln 1.0.</summary>
internal readonly struct PreciseProfile : IDecoderProfile
{
    public static bool Relaxed => false;
}

/// <summary>The fast profile: approximate transcendental functions and reassociated sums.</summary>
internal readonly struct RelaxedProfile : IDecoderProfile
{
    public static bool Relaxed => true;
}
