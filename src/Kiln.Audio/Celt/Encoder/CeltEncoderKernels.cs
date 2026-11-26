// Portions derived from the RFC 6716 reference implementation (Appendix A), (c) IETF Trust and the persons identified as authors, Simplified BSD License; see THIRD-PARTY-NOTICES.md.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Kiln.Audio.Simd;

namespace Kiln.Audio.Celt.Encoder;

/// <summary>
/// The scalar hot kernels of the CELT encoder (RFC 6716 §4.3).
/// </summary>
/// <remarks>
/// <para>
/// Every kernel is a static method with a scalar reference implementation, so the scalar path is
/// always available to tests and to platforms without vector support. The float kernels are
/// deterministic: the vector paths use the same left-to-right accumulation order where the result
/// feeds the bitstream, so the encoded bytes do not depend on the instruction set.
/// </para>
/// <para>
/// The kernels take spans and write in place; they allocate nothing.
/// </para>
/// </remarks>
internal static class CeltEncoderKernels
{
    /// <summary>Pre-emphasis coefficient at 48 kHz (RFC 6716 Appendix A, celt/modes.c: preemph[0]).</summary>
    internal const float PreEmphasisCoefficient = EncKernels.PreEmphasisCoefficient;

    /// <summary>Largest sample magnitude the reference passes to the encoder, before clipping.</summary>
    private const float ClipLimit = 65536f;

    /// <summary>
    /// Applies the 48 kHz pre-emphasis filter <c>y[n] = x[n] - 0.85*x[n-1]</c> channel by channel,
    /// zero-stuffing the input to 48 kHz, and reports whether every output sample is zero.
    /// </summary>
    /// <param name="pcm">One frame of interleaved signed 16-bit samples at the API rate.</param>
    /// <param name="channels">The number of interleaved channels.</param>
    /// <param name="upsample">
    /// The 48 kHz upsample factor (RFC 6716 Appendix A, <c>celt.c</c>): one input sample is read
    /// every <paramref name="upsample"/> output samples and the others are zero.
    /// </param>
    /// <param name="frameSamples">The number of 48 kHz samples per frame per channel.</param>
    /// <param name="stride">The distance between channel blocks in <paramref name="destination"/>.</param>
    /// <param name="offset">The offset of the first written sample inside each channel block.</param>
    /// <param name="destination">Channel-major destination of length <c>channels * stride</c>.</param>
    /// <param name="memory">Per-channel filter memory; updated in place.</param>
    /// <param name="silent">Receives true when every pre-emphasized sample is exactly zero.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void PreEmphasis(
        ReadOnlySpan<short> pcm,
        int channels,
        int upsample,
        int frameSamples,
        int stride,
        int offset,
        Span<float> destination,
        Span<float> memory,
        out bool silent)
    {
        // The 48 kHz mono path is the common case and has a bit-identical SIMD kernel.
        if (channels == 1 && upsample == 1 && offset == 0 && stride == frameSamples
            && SimdConfig.UseSimd && Vector128.IsHardwareAccelerated && pcm.Length >= 16)
        {
            ref float state = ref memory[0];
            EncKernels.PreEmphasis(pcm, destination, ref state, out silent);
            return;
        }

        bool allZero = true;
        for (int c = 0; c < channels; c++)
        {
            float state = memory[c];
            int count = 0;
            int source = c;
            Span<float> block = destination.Slice((c * stride) + offset, frameSamples);
            for (int i = 0; i < frameSamples; i++)
            {
                // The reference reads a fresh sample only every `upsample` 48 kHz samples; the
                // others are zero (the original zero-stuffing upsampler).
                float x;
                if (++count == upsample)
                {
                    count = 0;
                    x = pcm[source];
                    source += channels;
                }
                else
                {
                    x = 0f;
                }

                // The reference scales normalized float input by 32768; a short input is already
                // in that domain. It also clips to +/-65536 and drops NaNs.
                x = x > ClipLimit ? ClipLimit : (x < -ClipLimit ? -ClipLimit : x);
                float y = x + state;
                state = -PreEmphasisCoefficient * x;
                block[i] = y;
                allZero &= y == 0f;
            }

            memory[c] = state;
        }

        silent = allZero;
    }

    /// <summary>Scalar reference for the 48 kHz mono pre-emphasis; used by the parity test.</summary>
    internal static void PreEmphasisScalar(ReadOnlySpan<short> pcm, Span<float> destination, ref float memory, out bool silent)
    {
        float state = memory;
        bool allZero = true;
        for (int i = 0; i < pcm.Length; i++)
        {
            float x = pcm[i];
            x = x > ClipLimit ? ClipLimit : (x < -ClipLimit ? -ClipLimit : x);
            float y = x + state;
            state = -PreEmphasisCoefficient * x;
            destination[i] = y;
            allZero &= y == 0f;
        }

        memory = state;
        silent = allZero;
    }

    /// <summary>Returns the sum of squares of <paramref name="samples"/> plus the reference epsilon.</summary>
    /// <param name="samples">The band coefficients.</param>
    /// <returns>The energy of one CELT band (RFC 6716 Appendix A, celt/bands.c: compute_band_energies).</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static float BandEnergy(ReadOnlySpan<float> samples) => BandEnergyScalar(samples);

    /// <summary>
    /// Scalar reference for <see cref="BandEnergy"/>; the encoder keeps this summation order so the
    /// band energy is identical to the reference implementation on every machine.
    /// </summary>
    internal static float BandEnergyScalar(ReadOnlySpan<float> samples)
    {
        ref float values = ref MemoryMarshal.GetReference(samples);
        float sum = 1e-27f;
        for (int j = 0; j < samples.Length; j++)
        {
            float value = Unsafe.Add(ref values, j);
            sum += value * value;
        }

        return sum;
    }

    /// <summary>
    /// Divides one band by its amplitude so that the band has unit energy
    /// (RFC 6716 Appendix A, celt/bands.c: normalise_bands).
    /// </summary>
    /// <param name="frequency">The band coefficients, read and overwritten with the normalized shape.</param>
    /// <param name="amplitude">The band amplitude from <see cref="BandEnergy"/>.</param>
    /// <remarks>
    /// The vector path is element-wise and therefore bit-identical to the scalar path: each
    /// coefficient is multiplied by the same gain with the same IEEE-754 round-to-nearest result.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void NormaliseBand(Span<float> frequency, float amplitude)
    {
        float gain = 1f / (1e-27f + amplitude);
        if (Vector128.IsHardwareAccelerated && frequency.Length >= 8)
        {
            Vector128<float> gainVector = Vector128.Create(gain);
            ref float reference = ref MemoryMarshal.GetReference(frequency);
            int j = 0;
            for (; j + 4 <= frequency.Length; j += 4)
            {
                Vector128<float> value = Vector128.LoadUnsafe(ref reference, (nuint)j);
                Vector128.StoreUnsafe(value * gainVector, ref reference, (nuint)j);
            }

            for (; j < frequency.Length; j++)
            {
                frequency[j] *= gain;
            }

            return;
        }

        NormaliseBandScalar(frequency, gain);
    }

    /// <summary>Scalar reference for <see cref="NormaliseBand"/> that takes the precomputed gain.</summary>
    internal static void NormaliseBandScalar(Span<float> frequency, float gain)
    {
        for (int j = 0; j < frequency.Length; j++)
        {
            frequency[j] *= gain;
        }
    }

    /// <summary>
    /// The transient detector of RFC 6716 §4.3.1 (RFC 6716 Appendix A, celt/celt.c:
    /// transient_analysis), working on the pre-emphasized signal.
    /// </summary>
    /// <param name="input">Channel-major pre-emphasized frame, including the overlap look-ahead.</param>
    /// <param name="channels">The number of channels.</param>
    /// <param name="frameSamples">The number of 48 kHz samples per channel in the frame.</param>
    /// <param name="overlap">The CELT overlap (120 samples).</param>
    /// <param name="tmp">Scratch of at least <c>frameSamples + overlap</c> samples.</param>
    /// <param name="bins">Scratch of at least <c>(frameSamples + overlap) / (overlap / 2)</c> samples.</param>
    /// <returns>True when the reference detector declares the frame transient.</returns>
    internal static bool AnalyzeTransient(
        ReadOnlySpan<float> input,
        int channels,
        int frameSamples,
        int overlap,
        Span<float> tmp,
        Span<float> bins)
    {
        int len = frameSamples + overlap;
        int block = overlap / 2;
        int binCount = len / block;
        float memory0 = 0f;
        float memory1 = 0f;
        bool isTransient = false;

        // The reference adds the channels together before the high-pass filter.
        for (int i = 0; i < len; i++)
        {
            float sum = 0f;
            for (int c = 0; c < channels; c++)
            {
                sum += input[(c * len) + i];
            }

            tmp[i] = sum;
        }

        // High-pass filter: (1 - 2*z^-1 + z^-2) / (1 - z^-1 + .5*z^-2).
        for (int i = 0; i < len; i++)
        {
            float x = tmp[i];
            float y = memory0 + x;
            memory0 = memory1 + y - (2f * x);
            memory1 = x - (0.5f * y);
            tmp[i] = y;
        }

        // First few samples are bad because the filter memory is not propagated.
        for (int i = 0; i < 12 && i < len; i++)
        {
            tmp[i] = 0f;
        }

        for (int i = 0; i < binCount; i++)
        {
            float maxAbs = 0f;
            int offset = i * block;
            for (int j = 0; j < block; j++)
            {
                float value = tmp[offset + j];
                float magnitude = value < 0f ? -value : value;
                if (magnitude > maxAbs)
                {
                    maxAbs = magnitude;
                }
            }

            bins[i] = maxAbs;
        }

        for (int i = 0; i < binCount; i++)
        {
            int consecutive = 0;
            float t1 = 0.15f * bins[i];
            float t2 = 0.4f * bins[i];
            float t3 = 0.15f * bins[i];
            for (int j = 0; j < i; j++)
            {
                if (bins[j] < t1)
                {
                    consecutive++;
                }

                if (bins[j] < t2)
                {
                    consecutive++;
                }
                else
                {
                    consecutive = 0;
                }
            }

            if (consecutive >= 3)
            {
                isTransient = true;
            }

            consecutive = 0;
            for (int j = i + 1; j < binCount; j++)
            {
                if (bins[j] < t3)
                {
                    consecutive++;
                }
                else
                {
                    consecutive = 0;
                }
            }

            if (consecutive >= 7)
            {
                isTransient = true;
            }
        }

        return isTransient;
    }

    /// <summary>
    /// The PVQ search of RFC 6716 §4.3.4.2 (RFC 6716 Appendix A, celt/vq.c: the loop of
    /// <c>alg_quant</c>). Fills <paramref name="iy"/> with the integer pulse vector.
    /// </summary>
    /// <param name="x">The target vector; its sign is removed and restored in place.</param>
    /// <param name="iy">Receives the signed pulse counts (length <c>N</c>).</param>
    /// <param name="signs">Scratch of length <c>N</c> holding the original signs.</param>
    /// <param name="y">Scratch of length <c>N</c> (twice the pulse count per component).</param>
    /// <param name="n">The vector dimension, at least 2.</param>
    /// <param name="k">The number of pulses, at least 1.</param>
    /// <returns>The total squared norm of the integer vector.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static float PvqSearch(Span<float> x, Span<int> iy, Span<float> signs, Span<float> y, int n, int k) =>
        SimdConfig.UseSimd && Vector128.IsHardwareAccelerated
            ? EncPvqKernel.Search(x, iy, signs, y, n, k)
            : PvqSearchScalar(x, iy, signs, y, n, k);

    /// <summary>Scalar reference for <see cref="PvqSearch"/>.</summary>
    internal static float PvqSearchScalar(Span<float> x, Span<int> iy, Span<float> signs, Span<float> y, int n, int k)
    {
        for (int j = 0; j < n; j++)
        {
            if (x[j] > 0f)
            {
                signs[j] = 1f;
            }
            else
            {
                signs[j] = -1f;
                x[j] = -x[j];
            }

            iy[j] = 0;
            y[j] = 0f;
        }

        float xy = 0f;
        float yy = 0f;
        int pulsesLeft = k;

        // Pre-search by projecting on the pyramid.
        if (k > (n >> 1))
        {
            float sum = 0f;
            for (int j = 0; j < n; j++)
            {
                sum += x[j];
            }

            // Prevents infinities and NaNs from causing too many pulses.
            if (!(sum > 1e-15f && sum < 64f))
            {
                x[0] = 1f;
                for (int j = 1; j < n; j++)
                {
                    x[j] = 0f;
                }

                sum = 1f;
            }

            float reciprocal = (k - 1) * (1f / sum);
            for (int j = 0; j < n; j++)
            {
                // Round towards zero.
                int pulse = (int)MathF.Floor(reciprocal * x[j]);
                iy[j] = pulse;
                y[j] = pulse;
                yy += y[j] * y[j];
                xy += x[j] * y[j];
                y[j] *= 2f;
                pulsesLeft -= pulse;
            }
        }

        if (pulsesLeft > n + 3)
        {
            float tmp = pulsesLeft;
            yy += tmp * tmp;
            yy += tmp * y[0];
            iy[0] += pulsesLeft;
            pulsesLeft = 0;
        }

        const float VeryLarge = 1e15f;
        for (int i = 0; i < pulsesLeft; i++)
        {
            int bestId = 0;
            float bestNum = -VeryLarge;
            float bestDen = 0f;

            // The squared magnitude term gets added anyway, so add it outside the loop.
            yy += 1f;
            for (int j = 0; j < n; j++)
            {
                float rxy = xy + x[j];
                float ryy = yy + y[j];
                rxy *= rxy;
                if (bestDen * rxy > ryy * bestNum)
                {
                    bestDen = ryy;
                    bestNum = rxy;
                    bestId = j;
                }
            }

            xy += x[bestId];
            yy += y[bestId];
            y[bestId] += 2f;
            iy[bestId]++;
        }

        for (int j = 0; j < n; j++)
        {
            x[j] *= signs[j];
            if (signs[j] < 0f)
            {
                iy[j] = -iy[j];
            }
        }

        return yy;
    }

    /// <summary>Returns the exact integer inverse square root used by the CELT width search.</summary>
    /// <param name="value">A 32-bit unsigned value.</param>
    /// <returns><c>floor(sqrt(value))</c>.</returns>
    internal static uint IntegerSqrt(uint value)
    {
        uint g = 0;
        int shift = (int)((System.Numerics.BitOperations.Log2(value | 1) - 1) >> 1);
        uint b = 1u << shift;
        do
        {
            uint t = (((g << 1) + b) << shift);
            if (t <= value)
            {
                g += b;
                value -= t;
            }

            b >>= 1;
            shift--;
        }
        while (shift >= 0);

        return g;
    }
}
