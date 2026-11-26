// Portions derived from the RFC 6716 reference implementation (Appendix A), (c) IETF Trust and the persons identified as authors, Simplified BSD License; see THIRD-PARTY-NOTICES.md.

using System.IO;
using Kiln.Audio.Celt;
using Kiln.Audio.Decoder;

namespace Kiln.Audio;

/// <summary>Opus (RFC 6716) decoder.</summary>
/// <remarks>
/// <para>
/// The decoder supports every Opus mode and configuration: SILK-only, hybrid and CELT-only at all
/// bandwidths and frame durations, packet layouts 0 to 3 (RFC 6716 §3.2), and the transition
/// redundancy of §4.5.1. Decoding is conformant with the RFC 8251 test vectors through this
/// public API at every API sample rate and channel count.
/// </para>
/// <para>
/// Output is interleaved 16-bit PCM at <see cref="OpusDecoderOptions.SampleRate"/> and
/// <see cref="OpusDecoderOptions.Channels"/>. A stereo stream decoded to mono uses the left
/// channel, and a mono stream decoded to stereo duplicates it, matching the reference decoder.
/// </para>
/// <para>
/// Packet loss is handled in the mode of the last good frame (RFC 6716 §4.4): SILK extrapolates
/// from its LPC/LTP state with comfort noise and a smooth re-entry, and CELT repeats the pitch
/// period before switching to shaped noise. <see cref="Decode(ReadOnlySpan{byte},Span{short},bool)"/>
/// with <c>decodeFec: true</c> recovers the preceding frame from the in-band LBRR data.
/// </para>
/// </remarks>
public sealed class OpusDecoder : IDisposable
{
    private const int MaxPacketMilliseconds = 120;

    private readonly OpusDecoderCore _core;
    private readonly float[] _floatScratch;
    private bool _disposed;

    /// <summary>Creates a decoder after validating <paramref name="options"/>.</summary>
    /// <param name="options">Decoder configuration.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An option value is illegal.</exception>
    public OpusDecoder(OpusDecoderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        Options = options;
        _core = new OpusDecoderCore(options.SampleRate, options.Channels);
        _floatScratch = new float[options.SampleRate * MaxPacketMilliseconds / 1000 * options.Channels];
    }

    /// <summary>The validated options this decoder was created with.</summary>
    public OpusDecoderOptions Options { get; }

    /// <summary>
    /// Gets the final range of the last decoded frame (RFC 8251), which is the value a
    /// conformant decoder must reproduce for every packet.
    /// </summary>
    public uint FinalRange => _core.FinalRange;

    /// <summary>Decodes one Opus packet to interleaved 16-bit PCM.</summary>
    /// <param name="packet">The Opus packet, starting at its TOC byte.</param>
    /// <param name="pcm">
    /// Destination for interleaved PCM. It must hold at least the returned count times
    /// <see cref="OpusDecoderOptions.Channels"/> samples.
    /// </param>
    /// <param name="decodeFec">
    /// Decode the in-band FEC (LBRR) data of this packet to recover the frame that preceded it.
    /// The output has the frame size of <paramref name="packet"/> itself, which is not
    /// necessarily the size of the lost frame; an interval without LBRR falls back to packet
    /// loss concealment. CELT-only and hybrid packets have no SILK LBRR for the CELT layer, so
    /// their CELT contribution is concealed as well.
    /// </param>
    /// <returns>Number of samples per channel written to <paramref name="pcm"/>.</returns>
    /// <exception cref="ObjectDisposedException">The decoder has been disposed.</exception>
    /// <exception cref="InvalidDataException">The packet violates RFC 6716 §3 framing.</exception>
    /// <exception cref="ArgumentException"><paramref name="pcm"/> is too short for the packet.</exception>
    public int Decode(ReadOnlySpan<byte> packet, Span<short> pcm, bool decodeFec = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateDestination(packet, pcm.Length);

        int samples = _core.DecodePacket(packet, _floatScratch, decodeFec);
        int count = samples * Options.Channels;
        if (pcm.Length < count)
        {
            throw new ArgumentException(
                FormattableString.Invariant(
                    $"The PCM destination needs {count} samples but holds {pcm.Length}."),
                nameof(pcm));
        }

        for (int i = 0; i < count; i++)
        {
            pcm[i] = CeltPcm.ToInt16(_floatScratch[i]);
        }

        return samples;
    }

    /// <summary>Decodes one Opus packet to interleaved normalized floating-point PCM.</summary>
    /// <param name="packet">The Opus packet, starting at its TOC byte.</param>
    /// <param name="pcm">
    /// Destination for interleaved samples in [-1, 1]. It must hold at least
    /// the returned count times <see cref="OpusDecoderOptions.Channels"/> samples.
    /// </param>
    /// <param name="decodeFec">
    /// Decode the in-band FEC (LBRR) data of this packet to recover the frame that preceded it.
    /// The output has the frame size of <paramref name="packet"/> itself; an interval without
    /// LBRR falls back to packet loss concealment.
    /// </param>
    /// <returns>Number of samples per channel written to <paramref name="pcm"/>.</returns>
    /// <exception cref="ObjectDisposedException">The decoder has been disposed.</exception>
    /// <exception cref="InvalidDataException">The packet violates RFC 6716 §3 framing.</exception>
    /// <exception cref="ArgumentException"><paramref name="pcm"/> is too short for the packet.</exception>
    public int Decode(ReadOnlySpan<byte> packet, Span<float> pcm, bool decodeFec = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _core.DecodePacket(packet, pcm, decodeFec);
    }

    /// <summary>Synthesises concealment audio for a lost packet (packet loss concealment).</summary>
    /// <param name="pcm">
    /// Destination for interleaved PCM. It must hold at least the returned count times
    /// <see cref="OpusDecoderOptions.Channels"/> samples.
    /// </param>
    /// <returns>Number of samples per channel written to <paramref name="pcm"/>.</returns>
    /// <remarks>
    /// The concealed duration is the frame duration of the last successfully decoded packet, or
    /// 2.5 ms if nothing has been decoded yet. CELT-only streams use the CELT <c>DecodeLost</c>
    /// hook; SILK and hybrid streams repeat the last frame with a decay. See the class remarks.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The decoder has been disposed.</exception>
    /// <exception cref="ArgumentException"><paramref name="pcm"/> is too short.</exception>
    public int DecodeLoss(Span<short> pcm)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int samples = _core.DecodeLoss(_floatScratch);
        int count = samples * Options.Channels;
        if (pcm.Length < count)
        {
            throw new ArgumentException(
                FormattableString.Invariant(
                    $"The PCM destination needs {count} samples but holds {pcm.Length}."),
                nameof(pcm));
        }

        for (int i = 0; i < count; i++)
        {
            pcm[i] = CeltPcm.ToInt16(_floatScratch[i]);
        }

        return samples;
    }

    /// <summary>Throws before decoding when the caller's destination cannot hold the packet.</summary>
    /// <param name="packet">The packet being decoded.</param>
    /// <param name="length">The destination's length in interleaved samples.</param>
    /// <exception cref="InvalidDataException">The packet is malformed.</exception>
    /// <exception cref="ArgumentException">The destination is too short.</exception>
    private void ValidateDestination(ReadOnlySpan<byte> packet, int length)
    {
        if (packet.IsEmpty)
        {
            return;
        }

        int required = OpusDecoderCore.RequiredSamplesPerChannel(packet, Options.SampleRate);
        int count = required * Options.Channels;
        if (length < count)
        {
            throw new ArgumentException(
                FormattableString.Invariant(
                    $"The PCM destination needs {count} samples but holds {length}."),
                "pcm");
        }
    }

    /// <inheritdoc />
    public void Dispose() => _disposed = true;
}
