namespace Kiln.Audio;

/// <summary>
/// Compile-time encoder tuning profile. Mirrors the kernel-tier pattern: a static-abstract
/// interface implemented by two structs, so generic code specialised over the profile has no
/// per-sample branch on the mode.
/// </summary>
internal interface IEncoderProfile
{
    /// <summary>Gets a value indicating whether this is the speed-first profile.</summary>
    static abstract bool Relaxed { get; }
}

/// <summary>The default profile: Kiln 1.0 output, byte for byte.</summary>
internal readonly struct PreciseProfile : IEncoderProfile
{
    /// <inheritdoc/>
    public static bool Relaxed => false;
}

/// <summary>The speed-first profile (<see cref="OpusPerformanceMode.Relaxed"/>).</summary>
internal readonly struct RelaxedProfile : IEncoderProfile
{
    /// <inheritdoc/>
    public static bool Relaxed => true;
}
