using System.Diagnostics;
using System.IO;

namespace Kiln.Audio.Fuzz;

/// <summary>
/// Fuzzes <see cref="OpusDecoder.Decode"/> and <see cref="OpusDecoder.DecodeLoss"/> over every
/// output rate and channel count, with in-band FEC on and off.
/// </summary>
/// <remarks>
/// <para>
/// Decoders are pooled and reused, so the targets also exercise changes of mode, bandwidth,
/// frame duration and channel count between packets, including at random points after a
/// concealed loss. That state is part of the attack surface: a packet stream is untrusted
/// input in its entirety, not one packet at a time.
/// </para>
/// <para>
/// The contract is:
/// </para>
/// <list type="bullet">
/// <item><description>a malformed packet throws <see cref="InvalidDataException"/> and leaves
/// the decoder usable;</description></item>
/// <item><description>a valid packet returns exactly the documented sample count;</description></item>
/// <item><description>no other exception type escapes;</description></item>
/// <item><description>float output is finite;</description></item>
/// <item><description>one packet decodes in less than the time limit;</description></item>
/// <item><description>valid packets allocate nothing.</description></item>
/// </list>
/// </remarks>
internal sealed class DecoderTarget : IDisposable
{
    /// <summary>The API sample rates, matching RFC 6716 §2.</summary>
    internal static readonly int[] SampleRates = [8000, 12000, 16000, 24000, 48000];

    private const int MaxSamplesPerChannel = 5760;
    private const int MaxInterleaved = MaxSamplesPerChannel * 2;
    private const int HistoryLength = 8;

    private readonly OpusDecoder?[] _decoders = new OpusDecoder?[SampleRates.Length * 2];
    private readonly List<FuzzStep>[] _histories = new List<FuzzStep>[SampleRates.Length * 2];
    private readonly float[] _floatPcm = new float[MaxInterleaved];
    private readonly short[] _shortPcm = new short[MaxInterleaved];

    /// <summary>Creates the empty per-decoder histories.</summary>
    internal DecoderTarget()
    {
        for (int i = 0; i < _histories.Length; i++)
        {
            _histories[i] = [];
        }
    }

    /// <summary>Decodes one mutated packet and checks the whole contract.</summary>
    /// <param name="packet">The packet; any bytes, including empty and malformed, are legal.</param>
    /// <param name="rng">Generator state for the per-exec configuration choice.</param>
    /// <exception cref="FuzzFailureException">The resilience contract was violated.</exception>
    internal void Run(byte[] packet, FuzzRng rng)
    {
        int rate = SampleRates[rng.Next(SampleRates.Length)];
        int channels = 1 + rng.Next(2);
        bool fec = rng.Chance(1, 2);
        bool useFloat = rng.Chance(1, 2);
        int index = (Array.IndexOf(SampleRates, rate) * 2) + (channels - 1);
        OpusDecoder decoder = GetDecoder(index, rate, channels);
        List<FuzzStep> history = _histories[index];

        if (rng.Chance(1, 8))
        {
            AddStep(history, new FuzzStep([], rate, channels, false, false, Loss: true));
            DecodeLoss(decoder, rate, channels, _shortPcm, history);
        }

        AddStep(history, new FuzzStep(packet, rate, channels, fec, useFloat, Loss: false));
        Decode(decoder, packet, rate, channels, fec, useFloat, _floatPcm, _shortPcm, history);

        if (rng.Chance(1, 8))
        {
            AddStep(history, new FuzzStep([], rate, channels, false, false, Loss: true));
            DecodeLoss(decoder, rate, channels, _shortPcm, history);
        }
    }

    /// <summary>Decodes one packet once with a fresh decoder, for reproducing a finding.</summary>
    /// <param name="packet">The packet to decode.</param>
    /// <param name="sampleRate">API sample rate.</param>
    /// <param name="channels">API channel count.</param>
    /// <param name="decodeFec">Whether to request in-band FEC.</param>
    /// <param name="useFloat">Whether to use the float overload.</param>
    /// <returns>True when the same contract violation reproduces.</returns>
    internal static bool Reproduces(byte[] packet, int sampleRate, int channels, bool decodeFec, bool useFloat)
    {
        try
        {
            Trace(packet, sampleRate, channels, decodeFec, useFloat);
            return false;
        }
        catch (FuzzFailureException)
        {
            return true;
        }
    }

    /// <summary>Replays a decoder sequence with a fresh decoder, for reproducing a finding.</summary>
    /// <param name="sequence">The steps, oldest first.</param>
    /// <returns>True when any step violates the contract.</returns>
    internal static bool ReproducesSequence(IReadOnlyList<FuzzStep> sequence)
    {
        try
        {
            TraceSequence(sequence);
            return false;
        }
        catch (FuzzFailureException)
        {
            return true;
        }
    }

    /// <summary>Decodes one packet once with a fresh decoder and lets the failure escape, for a stack trace.</summary>
    /// <param name="packet">The packet to decode.</param>
    /// <param name="sampleRate">API sample rate.</param>
    /// <param name="channels">API channel count.</param>
    /// <param name="decodeFec">Whether to request in-band FEC.</param>
    /// <param name="useFloat">Whether to use the float overload.</param>
    internal static void Trace(byte[] packet, int sampleRate, int channels, bool decodeFec, bool useFloat)
    {
        using var decoder = new OpusDecoder(new OpusDecoderOptions { SampleRate = sampleRate, Channels = channels });
        Decode(
            decoder,
            packet,
            sampleRate,
            channels,
            decodeFec,
            useFloat,
            new float[MaxInterleaved],
            new short[MaxInterleaved],
            sequence: null);
    }

    /// <summary>Replays a sequence with a fresh decoder and lets the first failure escape.</summary>
    /// <param name="sequence">The steps, oldest first.</param>
    internal static void TraceSequence(IReadOnlyList<FuzzStep> sequence)
    {
        var floatPcm = new float[MaxInterleaved];
        var shortPcm = new short[MaxInterleaved];
        OpusDecoder? decoder = null;
        try
        {
            foreach (FuzzStep step in sequence)
            {
                if (decoder is null)
                {
                    decoder = new OpusDecoder(
                        new OpusDecoderOptions { SampleRate = step.SampleRate, Channels = step.Channels });
                }

                if (step.Loss)
                {
                    DecodeLoss(decoder, step.SampleRate, step.Channels, shortPcm, sequence);
                }
                else
                {
                    Decode(
                        decoder,
                        step.Packet,
                        step.SampleRate,
                        step.Channels,
                        step.DecodeFec,
                        step.UseFloat,
                        floatPcm,
                        shortPcm,
                        sequence);
                }
            }
        }
        finally
        {
            decoder?.Dispose();
        }
    }

    /// <summary>
    /// Asserts that decoding valid packets performs no managed allocation.
    /// </summary>
    /// <param name="packets">Candidate packets; invalid ones and the empty packet are skipped.</param>
    /// <returns>The number of valid packets measured.</returns>
    /// <exception cref="FuzzFailureException">A valid packet allocated.</exception>
    internal static int CheckAllocations(IReadOnlyList<byte[]> packets)
    {
        var valid = new List<byte[]>();
        foreach (byte[] packet in packets)
        {
            if (valid.Count == 32)
            {
                break;
            }

            if (packet.Length > 1 && IsValid(packet))
            {
                valid.Add(packet);
            }
        }

        using var decoder = new OpusDecoder(new OpusDecoderOptions { SampleRate = 48000, Channels = 2 });
        var pcm = new float[MaxInterleaved];
        var pcmShort = new short[MaxInterleaved];
        for (int pass = 0; pass < 2; pass++)
        {
            foreach (byte[] packet in valid)
            {
                decoder.Decode(packet, pcm);
            }
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        foreach (byte[] packet in valid)
        {
            decoder.Decode(packet, pcm);
        }

        decoder.DecodeLoss(pcmShort);
        long after = GC.GetAllocatedBytesForCurrentThread();
        if (after != before)
        {
            throw new FuzzFailureException(
                FuzzTargetKind.Decoder,
                valid.Count > 0 ? valid[0] : [],
                FormattableString.Invariant($"Decoding {valid.Count} valid packets allocated {after - before} bytes."));
        }

        return valid.Count;
    }

    /// <summary>Disposes every pooled decoder.</summary>
    public void Dispose()
    {
        Reset();
        GC.SuppressFinalize(this);
    }

    /// <summary>Disposes and forgets every pooled decoder, discarding all cross-frame state.</summary>
    internal void Reset()
    {
        for (int i = 0; i < _decoders.Length; i++)
        {
            _decoders[i]?.Dispose();
            _decoders[i] = null;
            _histories[i].Clear();
        }
    }

    private static void AddStep(List<FuzzStep> history, FuzzStep step)
    {
        history.Add(step);
        if (history.Count > HistoryLength)
        {
            history.RemoveAt(0);
        }
    }

    private static void Decode(
        OpusDecoder decoder,
        byte[] packet,
        int rate,
        int channels,
        bool fec,
        bool useFloat,
        float[] floatPcm,
        short[] shortPcm,
        IReadOnlyList<FuzzStep>? sequence)
    {
        int expected = 0;
        bool valid = packet.Length == 0 || TryGetExpectedSamples(packet, rate, out expected);

        int samples;
        long start = Stopwatch.GetTimestamp();
        try
        {
            if (useFloat)
            {
                Span<float> pcm = floatPcm.AsSpan(0, MaxSamplesPerChannel * channels);
                samples = decoder.Decode(packet, pcm, fec);
            }
            else
            {
                Span<short> pcm = shortPcm.AsSpan(0, MaxSamplesPerChannel * channels);
                samples = decoder.Decode(packet, pcm, fec);
            }
        }
        catch (InvalidDataException) when (!valid)
        {
            return;
        }
        catch (InvalidDataException exception)
        {
            throw Failure(
                packet,
                FormattableString.Invariant($"A valid packet was rejected as malformed: {exception.Message}."),
                exception,
                rate,
                channels,
                fec,
                useFloat,
                sequence);
        }
        catch (ArgumentException exception)
        {
            throw Failure(
                packet,
                FormattableString.Invariant($"Decode rejected a full-size destination: {exception.Message}"),
                exception,
                rate,
                channels,
                fec,
                useFloat,
                sequence);
        }
        catch (Exception exception)
        {
            throw Failure(
                packet,
                FormattableString.Invariant($"Decode threw {exception.GetType().Name}: {exception.Message}."),
                exception,
                rate,
                channels,
                fec,
                useFloat,
                sequence);
        }

        if (Stopwatch.GetElapsedTime(start) > FuzzContract.PacketTimeLimit)
        {
            throw Failure(
                packet,
                FormattableString.Invariant($"One decode took longer than {FuzzContract.PacketTimeLimit.TotalSeconds:0} s."),
                null,
                rate,
                channels,
                fec,
                useFloat,
                sequence);
        }

        ValidateResult(packet, samples, expected, valid, rate, channels, useFloat ? floatPcm : null, fec, sequence);
    }

    private static void DecodeLoss(
        OpusDecoder decoder,
        int rate,
        int channels,
        short[] shortPcm,
        IReadOnlyList<FuzzStep>? sequence)
    {
        long start = Stopwatch.GetTimestamp();
        int samples;
        try
        {
            samples = decoder.DecodeLoss(shortPcm.AsSpan(0, MaxSamplesPerChannel * channels));
        }
        catch (Exception exception)
        {
            throw Failure(
                [],
                FormattableString.Invariant($"DecodeLoss threw {exception.GetType().Name}: {exception.Message}."),
                exception,
                rate,
                channels,
                fec: false,
                useFloat: false,
                sequence: sequence);
        }

        if (Stopwatch.GetElapsedTime(start) > FuzzContract.PacketTimeLimit)
        {
            throw Failure(
                [],
                FormattableString.Invariant($"One DecodeLoss took longer than {FuzzContract.PacketTimeLimit.TotalSeconds:0} s."),
                null,
                rate,
                channels,
                fec: false,
                useFloat: false,
                sequence: sequence);
        }

        // Concealment repeats the last frame (at most 60 ms) or 2.5 ms before any packet.
        int minimum = rate / 400;
        int maximum = rate * 60 / 1000;
        if (samples < minimum || samples > maximum)
        {
            throw Failure(
                [],
                FormattableString.Invariant($"DecodeLoss returned {samples} samples per channel; expected {minimum} to {maximum}."),
                null,
                rate,
                channels,
                fec: false,
                useFloat: false,
                sequence: sequence);
        }
    }

    private static void ValidateResult(
        byte[] packet,
        int samples,
        int expectedSamples,
        bool valid,
        int rate,
        int channels,
        float[]? floatPcm,
        bool fec,
        IReadOnlyList<FuzzStep>? sequence)
    {
        if (!valid)
        {
            throw Failure(
                packet,
                FormattableString.Invariant($"A malformed packet decoded successfully ({samples} samples per channel)."),
                null,
                rate,
                channels,
                fec,
                useFloat: floatPcm is not null,
                sequence);
        }

        bool empty = packet.Length == 0;
        int minimum = rate / 400;
        int maximum = rate * 60 / 1000;
        if (empty ? samples < minimum || samples > maximum : samples != expectedSamples)
        {
            throw Failure(
                packet,
                FormattableString.Invariant($"Decode returned {samples} samples per channel; expected {expectedSamples}."),
                null,
                rate,
                channels,
                fec,
                useFloat: floatPcm is not null,
                sequence);
        }

        if (floatPcm is not null)
        {
            FuzzContract.RequireFinite(
                floatPcm.AsSpan(0, samples * channels),
                packet,
                rate,
                channels,
                fec);
        }
    }

    private static bool TryGetExpectedSamples(byte[] packet, int rate, out int expected)
    {
        expected = 0;
        Span<Range> frames = stackalloc Range[OpusPacket.MaxFrames];
        if (OpusPacket.TryParse(packet, frames, out int frameCount, out OpusToc toc, out _) != OpusStatus.Ok)
        {
            return false;
        }

        expected = frameCount * OpusPacket.GetSamplesPerFrame(toc, rate);
        return true;
    }

    private static bool IsValid(byte[] packet)
    {
        Span<Range> frames = stackalloc Range[OpusPacket.MaxFrames];
        return OpusPacket.TryParse(packet, frames, out _, out _, out _) == OpusStatus.Ok;
    }

    private static FuzzFailureException Failure(
        byte[] packet,
        string message,
        Exception? inner,
        int rate,
        int channels,
        bool fec,
        bool useFloat,
        IReadOnlyList<FuzzStep>? sequence) =>
        new(FuzzTargetKind.Decoder, packet, message, inner)
        {
            SampleRate = rate,
            Channels = channels,
            DecodeFec = fec,
            UseFloat = useFloat,
            Sequence = sequence,
        };

    private OpusDecoder GetDecoder(int index, int rate, int channels)
    {
        OpusDecoder? decoder = _decoders[index];
        if (decoder is null)
        {
            decoder = new OpusDecoder(new OpusDecoderOptions { SampleRate = rate, Channels = channels });
            _decoders[index] = decoder;
        }

        return decoder;
    }
}
