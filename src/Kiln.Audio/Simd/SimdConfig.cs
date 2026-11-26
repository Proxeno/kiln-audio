namespace Kiln.Audio.Simd;

/// <summary>
/// The process-wide SIMD switch.
/// </summary>
/// <remarks>
/// The environment variable <c>KILN_AUDIO_DISABLE_SIMD</c> is read exactly once, when this
/// type is first touched. Setting it to <c>1</c> (or <c>true</c>) forces every kernel that has
/// a vector path onto its scalar reference path. This is used by the parity tests and by the
/// scalar conformance run; it is not a runtime toggle and changing it after first use has no
/// effect.
/// </remarks>
internal static class SimdConfig
{
    private static readonly bool s_disabled = ReadDisabled();

    /// <summary>Gets a value indicating whether the scalar paths are forced.</summary>
    public static bool Disabled => s_disabled;

    /// <summary>Gets a value indicating whether the vector paths may be used.</summary>
    public static bool UseSimd => !s_disabled;

    /// <summary>Parses the <c>KILN_AUDIO_DISABLE_SIMD</c> value; exposed for the parity tests.</summary>
    /// <param name="value">The environment value, or <see langword="null"/> when unset.</param>
    /// <returns><see langword="true"/> when the scalar paths are requested.</returns>
    internal static bool IsDisabledValue(string? value) =>
        value is not null
        && (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase));

    private static bool ReadDisabled() =>
        IsDisabledValue(Environment.GetEnvironmentVariable("KILN_AUDIO_DISABLE_SIMD"));
}
