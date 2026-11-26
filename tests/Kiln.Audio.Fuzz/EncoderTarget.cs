namespace Kiln.Audio.Fuzz;

/// <summary>
/// Fuzzes <see cref="OpusEncoder"/> construction and <see cref="OpusEncoder.Encode"/> with random
/// PCM and option sweeps.
/// </summary>
/// <remarks>
/// The encoder's documented failure modes are <see cref="NotSupportedException"/> for legal but
/// unimplemented configurations and <see cref="ArgumentException"/> for illegal option values,
/// wrong-length PCM or a short destination. Any other exception, a packet that does not parse,
/// or a decoded packet whose length differs from the frame is a finding.
/// </remarks>
internal sealed class EncoderTarget : IDisposable
{
    private const int MaxFrameSamples = 960;
    private const int PacketBufferBytes = 4096;

    private static readonly int[] Rates = [8000, 12000, 16000, 24000, 44100, 0, -1, 48000];
    private static readonly int[] Channels = [1, 2, 3, 0, -1];
    private static readonly OpusApplication[] Applications =
        [OpusApplication.Voip, OpusApplication.Audio, OpusApplication.RestrictedLowDelay, (OpusApplication)7];
    private static readonly int[] NearValidBitrates = [6000, 24000, 64000, 128000];
    private static readonly int[] InvalidBitrates = [1, 0, -1, int.MaxValue, 48000];
    private static readonly OpusFrameDuration[] EmptyDurations =
        [OpusFrameDuration.Ms2_5, OpusFrameDuration.Ms20, OpusFrameDuration.Ms40, OpusFrameDuration.Ms60, (OpusFrameDuration)9];
    private static readonly int[] Complexities = [0, 10, 11, -1, 5];

    private readonly OpusEncoder?[] _pool = new OpusEncoder?[4];
    private readonly OpusDecoder _decoder = new(new OpusDecoderOptions { SampleRate = 48000, Channels = 1 });
    private readonly short[] _pcm16 = new short[MaxFrameSamples];
    private readonly float[] _pcmFloat = new float[MaxFrameSamples];
    private readonly float[] _decoded = new float[MaxFrameSamples];
    private readonly byte[] _packet = new byte[PacketBufferBytes];

    /// <summary>Runs one encoder execution.</summary>
    /// <param name="rng">Generator state.</param>
    /// <exception cref="FuzzFailureException">The resilience contract was violated.</exception>
    internal void Run(FuzzRng rng)
    {
        if (rng.Chance(1, 8))
        {
            ConstructionSweep(rng);
        }

        EncodeSweep(rng);
    }

    /// <summary>Disposes every pooled encoder, discarding all state.</summary>
    public void Dispose()
    {
        Reset();
        _decoder.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>Disposes and forgets the pooled encoders.</summary>
    internal void Reset()
    {
        for (int i = 0; i < _pool.Length; i++)
        {
            _pool[i]?.Dispose();
            _pool[i] = null;
        }
    }

    private void ConstructionSweep(FuzzRng rng)
    {
        OpusEncoderOptions options = RandomOptions(rng);
        OpusEncoder? encoder = null;
        try
        {
            encoder = new OpusEncoder(options);
        }
        catch (NotSupportedException)
        {
        }
        catch (ArgumentException)
        {
        }
        catch (Exception exception)
        {
            throw new FuzzFailureException(
                FuzzTargetKind.Encoder,
                [],
                FormattableString.Invariant($"The constructor threw {exception.GetType().Name}: {exception.Message}."),
                exception);
        }
        finally
        {
            encoder?.Dispose();
        }
    }

    private void EncodeSweep(FuzzRng rng)
    {
        OpusFrameDuration duration = (OpusFrameDuration)rng.Next(4);
        OpusEncoder encoder = GetEncoder(duration);
        int frameSamples = encoder.Options.FrameDuration switch
        {
            OpusFrameDuration.Ms2_5 => 120,
            OpusFrameDuration.Ms5 => 240,
            OpusFrameDuration.Ms10 => 480,
            _ => 960,
        };

        bool useFloat = rng.Chance(1, 2);
        int written = useFloat
            ? EncodeFloat(encoder, frameSamples, rng)
            : EncodeShort(encoder, frameSamples, rng);

        ValidatePacket(encoder, written, frameSamples);

        if (rng.Chance(1, 4))
        {
            WrongInput(encoder, frameSamples);
        }
    }

    private int EncodeShort(OpusEncoder encoder, int frameSamples, FuzzRng rng)
    {
        for (int i = 0; i < frameSamples; i++)
        {
            _pcm16[i] = (short)rng.Next(short.MinValue, short.MaxValue + 1);
        }

        try
        {
            return encoder.Encode(_pcm16.AsSpan(0, frameSamples), _packet);
        }
        catch (ArgumentException exception)
        {
            throw new FuzzFailureException(FuzzTargetKind.Encoder, [], $"Encode threw {exception.Message}.", exception);
        }
        catch (Exception exception)
        {
            throw new FuzzFailureException(
                FuzzTargetKind.Encoder,
                [],
                FormattableString.Invariant($"Encode threw {exception.GetType().Name}: {exception.Message}."),
                exception);
        }
    }

    private int EncodeFloat(OpusEncoder encoder, int frameSamples, FuzzRng rng)
    {
        int mode = rng.Next(6);
        for (int i = 0; i < frameSamples; i++)
        {
            _pcmFloat[i] = mode switch
            {
                0 => (float)((rng.NextUInt64() / (double)ulong.MaxValue * 2.2) - 1.1),
                1 => (rng.NextUInt64() & 1) == 0 ? 1e30f : -1e30f,
                2 => float.NaN,
                3 => (rng.NextUInt64() & 1) == 0 ? float.PositiveInfinity : float.NegativeInfinity,
                4 => 0f,
                _ => (float)((rng.NextUInt64() / (double)ulong.MaxValue * 2.0) - 1.0),
            };
        }

        try
        {
            return encoder.Encode(_pcmFloat.AsSpan(0, frameSamples), _packet);
        }
        catch (ArgumentException exception)
        {
            throw new FuzzFailureException(FuzzTargetKind.Encoder, [], $"Encode threw {exception.Message}.", exception);
        }
        catch (Exception exception)
        {
            throw new FuzzFailureException(
                FuzzTargetKind.Encoder,
                [],
                FormattableString.Invariant($"Encode threw {exception.GetType().Name}: {exception.Message}."),
                exception);
        }
    }

    private void ValidatePacket(OpusEncoder encoder, int written, int frameSamples)
    {
        if (written < 1 || written > _packet.Length)
        {
            throw new FuzzFailureException(
                FuzzTargetKind.Encoder,
                _packet.AsSpan(0, Math.Clamp(written, 0, _packet.Length)).ToArray(),
                FormattableString.Invariant($"Encode wrote {written} bytes into a {_packet.Length}-byte buffer."));
        }

        byte[] packet = _packet.AsSpan(0, written).ToArray();
        Span<Range> frames = stackalloc Range[OpusPacket.MaxFrames];
        OpusStatus status = OpusPacket.TryParse(packet, frames, out int frameCount, out _, out _);
        if (status != OpusStatus.Ok || frameCount != 1)
        {
            throw new FuzzFailureException(
                FuzzTargetKind.Encoder,
                packet,
                FormattableString.Invariant($"The encoded packet does not parse ({status}, {frameCount} frames)."));
        }

        int samples;
        try
        {
            samples = _decoder.Decode(packet, _decoded.AsSpan(0, frameSamples));
        }
        catch (Exception exception)
        {
            throw new FuzzFailureException(
                FuzzTargetKind.Encoder,
                packet,
                FormattableString.Invariant($"Decoding the encoded packet threw {exception.GetType().Name}: {exception.Message}."),
                exception);
        }

        if (samples != frameSamples)
        {
            throw new FuzzFailureException(
                FuzzTargetKind.Encoder,
                packet,
                FormattableString.Invariant($"The encoded packet decodes to {samples} samples; expected {frameSamples}."));
        }

        FuzzContract.RequireFinite(_decoded.AsSpan(0, samples), packet, 48000, 1, false);

        if (_decoder.FinalRange != encoder.FinalRange)
        {
            throw new FuzzFailureException(
                FuzzTargetKind.Encoder,
                packet,
                FormattableString.Invariant($"The encoded packet's final range {_decoder.FinalRange:x8} differs from the encoder's {encoder.FinalRange:x8}."));
        }
    }

    private void WrongInput(OpusEncoder encoder, int frameSamples)
    {
        try
        {
            encoder.Encode(_pcm16.AsSpan(0, frameSamples - 1), _packet);
        }
        catch (ArgumentException)
        {
            return;
        }
        catch (Exception exception)
        {
            throw new FuzzFailureException(
                FuzzTargetKind.Encoder,
                [],
                FormattableString.Invariant($"A short frame threw {exception.GetType().Name}: {exception.Message}."),
                exception);
        }

        throw new FuzzFailureException(FuzzTargetKind.Encoder, [], "A short frame was accepted.");
    }

    private OpusEncoder GetEncoder(OpusFrameDuration duration)
    {
        int index = (int)duration;
        OpusEncoder? encoder = _pool[index];
        if (encoder is null)
        {
            encoder = new OpusEncoder(
                new OpusEncoderOptions
                {
                    Application = OpusApplication.RestrictedLowDelay,
                    FrameDuration = duration,
                });
            _pool[index] = encoder;
        }

        return encoder;
    }

    private static OpusEncoderOptions RandomOptions(FuzzRng rng)
    {
        // Half the draws stay near a working configuration so the constructor's own encode path
        // is reached; the rest sweep illegal and unsupported values.
        bool nearValid = rng.Chance(1, 4);
        var options = new OpusEncoderOptions
        {
            SampleRate = nearValid ? 48000 : rng.Pick(Rates),
            Channels = nearValid ? 1 : rng.Pick(Channels),
            Application = nearValid ? OpusApplication.RestrictedLowDelay : rng.Pick(Applications),
            BitrateBps = nearValid ? rng.Pick(NearValidBitrates) : rng.Pick(InvalidBitrates),
            FrameDuration = nearValid ? (OpusFrameDuration)rng.Next(4) : rng.Pick(EmptyDurations),
            Complexity = nearValid ? rng.Next(11) : rng.Pick(Complexities),
            UseInbandFec = !nearValid && rng.Chance(1, 4),
            UseDtx = !nearValid && rng.Chance(1, 4),
        };
        return options;
    }
}
