namespace Kiln.Audio;

/// <summary>
/// Intended application of an Opus encoder instance (RFC 6716 §2; mirrors the libopus
/// application hints). Influences mode selection and look-ahead.
/// </summary>
public enum OpusApplication
{
    /// <summary>Speech-optimised, lowest-latency-friendly voice over IP.</summary>
    Voip = 0,

    /// <summary>General audio (music, mixed content), favouring fidelity.</summary>
    Audio = 1,

    /// <summary>Restricted low-delay mode: CELT only, no SILK/hybrid look-ahead.</summary>
    RestrictedLowDelay = 2,
}
