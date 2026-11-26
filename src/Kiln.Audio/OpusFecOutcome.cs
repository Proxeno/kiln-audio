namespace Kiln.Audio;

/// <summary>Outcome of the in-band FEC (LBRR) decode requested by the last decoder call.</summary>
/// <remarks>
/// In-band FEC is requested with <c>decodeFec: true</c> on
/// <see cref="OpusDecoder.Decode(ReadOnlySpan{byte},Span{short},bool)"/> (RFC 6716 §4.2.4–§4.2.5):
/// the packet's LBRR data describes the audio of the lost interval that preceded it.
/// <see cref="OpusDecoder.LastFecOutcome"/> reports whether that data was present, and
/// <see cref="OpusPacket.HasLbrr"/> answers the same question for a packet without decoding it.
/// </remarks>
public enum OpusFecOutcome
{
    /// <summary>The last call did not request in-band FEC decoding.</summary>
    NotRequested = 0,

    /// <summary>
    /// The packet's first SILK frame carried LBRR data for the lost interval and the decoder
    /// used it. In a hybrid packet only the SILK layer is recovered; the CELT layer has no
    /// in-band FEC and is still concealed.
    /// </summary>
    Recovered = 1,

    /// <summary>
    /// In-band FEC was requested but no LBRR data was present for the lost interval (a CELT-only
    /// packet, LBRR flags clear, or a frame too short to carry a SILK header), so packet loss
    /// concealment was used instead.
    /// </summary>
    Concealed = 2,
}
