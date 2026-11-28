namespace Kiln.Audio;

/// <summary>Selects between exactness/quality and speed for an encoder or decoder instance.</summary>
public enum OpusPerformanceMode
{
    /// <summary>The default. Encoder: quality-first tuning (Kiln 1.0 output, byte for byte).
    /// Decoder: output bit-identical to Kiln 1.0.</summary>
    Precise = 0,

    /// <summary>Faster. The encoder may give up a little quality and the decoder may use
    /// non-bit-exact floating-point math. Bitstreams stay fully standard: any Opus decoder
    /// decodes Relaxed output, and the Relaxed decoder passes RFC 8251 conformance.</summary>
    Relaxed = 1,
}
