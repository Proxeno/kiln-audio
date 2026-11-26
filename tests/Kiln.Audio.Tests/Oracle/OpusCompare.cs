/* Copyright (c) 2011-2012 Xiph.Org Foundation, Mozilla Corporation
   Written by Jean-Marc Valin and Timothy B. Terriberry */
/*
   Redistribution and use in source and binary forms, with or without
   modification, are permitted provided that the following conditions
   are met:

   - Redistributions of source code must retain the above copyright
   notice, this list of conditions and the following disclaimer.

   - Redistributions in binary form must reproduce the above copyright
   notice, this list of conditions and the following disclaimer in the
   documentation and/or other materials provided with the distribution.

   THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
   ``AS IS'' AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
   LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
   A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER
   OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL,
   EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO,
   PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR
   PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF
   LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING
   NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
   SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
*/

using System;

namespace Kiln.Audio.Tests.Oracle;

/// <summary>
/// Managed port of the Opus conformance quality metric defined by RFC 6716 §6 and updated by
/// RFC 8251, the same metric implemented by libopus <c>opus_compare</c>.
/// </summary>
/// <remarks>
/// The metric windowed-DFT analyses both signals, applies Bark-band masking, and compares the
/// masked band energies through a log-ratio error. A test vector passes when the resulting
/// quality is non-negative; 100 % means the signals are indistinguishable to the metric.
///
/// This is the reference scoring procedure for the RFC 8251 test vectors, not a codec module:
/// it lives in the test project and is never referenced from <c>src/</c>. The BSD notice above
/// is retained because the algorithm is a direct port of libopus <c>src/opus_compare.c</c>.
///
/// The reference signal is always interleaved stereo, as <c>opus_compare</c> reads it; for mono
/// comparisons its two channels are averaged. <paramref name="test"/> has one or two channels.
/// </remarks>
internal static class OpusCompare
{
    private const float OpusPi = 3.14159265f;

    private const int Nbands = 21;
    private const int Nfreqs = 240;
    private const int TestWinSize = 480;
    private const int TestWinStep = 120;

    /// <summary>Bark-derived CELT band edges (RFC 6716 §6.2), 21 bands.</summary>
    private static readonly int[] BandEdges =
    [
        0, 2, 4, 6, 8, 10, 12, 14, 16, 20, 24, 28, 32, 40, 48, 56, 68, 80, 96, 120, 156, 200,
    ];

    /// <summary>The outcome of the metric: verdict, quality percentage, and internal weighted error.</summary>
    /// <param name="Passed">True when the quality is non-negative (a conformant test vector).</param>
    /// <param name="Quality">Quality percentage; 100 means indistinguishable.</param>
    /// <param name="Error">The internal weighted error before conversion to quality.</param>
    internal readonly record struct Result(bool Passed, double Quality, double Error);

    /// <summary>
    /// Compares <paramref name="test"/> against <paramref name="reference"/> using the RFC 6716 §6
    /// metric as updated by RFC 8251.
    /// </summary>
    /// <param name="reference">Reference PCM, interleaved stereo (two samples per frame).</param>
    /// <param name="test">Test PCM, interleaved with <paramref name="stereo"/> channel(s).</param>
    /// <param name="rate">Comparison sample rate: 8000, 12000, 16000, 24000, or 48000.</param>
    /// <param name="stereo">True when <paramref name="test"/> has two channels.</param>
    /// <returns>The metric result.</returns>
    /// <exception cref="ArgumentNullException">A signal array is null.</exception>
    /// <exception cref="ArgumentException">The signals are empty, too short, or have mismatched lengths.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="rate"/> is not a supported rate.</exception>
    internal static Result Compare(short[] reference, short[] test, int rate, bool stereo)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(test);

        int nchannels = stereo ? 2 : 1;
        int downsample = rate switch
        {
            48000 => 1,
            24000 => 2,
            16000 => 3,
            12000 => 4,
            8000 => 6,
            _ => throw new ArgumentOutOfRangeException(
                nameof(rate), rate, "Sample rate must be 8000, 12000, 16000, 24000, or 48000."),
        };
        int ybands = rate switch
        {
            8000 => 13,
            12000 => 15,
            16000 => 17,
            24000 => 19,
            _ => Nbands,
        };
        int yfreqs = Nfreqs / downsample;

        if (reference.Length % 2 != 0)
        {
            throw new ArgumentException("Reference PCM must be interleaved stereo.", nameof(reference));
        }

        int xlength = reference.Length / 2;
        var x = new float[xlength * nchannels];
        if (nchannels == 1)
        {
            // .5 is a double in the reference implementation, so the product is double precision.
            for (int i = 0; i < xlength; i++)
            {
                x[i] = (float)(0.5 * ((float)reference[2 * i] + (float)reference[(2 * i) + 1]));
            }
        }
        else
        {
            for (int i = 0; i < reference.Length; i++)
            {
                x[i] = reference[i];
            }
        }

        if (test.Length % nchannels != 0)
        {
            throw new ArgumentException(
                stereo ? "Test PCM must be interleaved stereo." : "Test PCM must be interleaved mono.",
                nameof(test));
        }

        int ylength = test.Length / nchannels;
        if (xlength != ylength * downsample)
        {
            throw new ArgumentException(
                $"Sample counts do not match ({xlength} != {ylength * downsample}).", nameof(test));
        }

        if (xlength < TestWinSize)
        {
            throw new ArgumentException(
                $"Insufficient sample data ({xlength} < {TestWinSize}).", nameof(test));
        }

        // Identical stereo input scores exactly 100: every float operation is the same on both
        // sides, the log-ratio is exactly zero, and the error is zero.
        if (stereo && reference.Length == test.Length && reference.AsSpan().SequenceEqual(test))
        {
            return new Result(true, 100.0, 0.0);
        }

        int nframes = ((xlength - TestWinSize) + TestWinStep) / TestWinStep;

        var y = new float[test.Length];
        for (int i = 0; i < test.Length; i++)
        {
            y[i] = test[i];
        }

        var xb = new float[nframes * Nbands * nchannels];
        var referenceSpectrum = new float[nframes * Nfreqs * nchannels];
        var testSpectrum = new float[nframes * yfreqs * nchannels];

        BandEnergy(xb, referenceSpectrum, BandEdges, Nbands, x, nchannels, nframes, TestWinSize, TestWinStep, 1);
        BandEnergy(
            null,
            testSpectrum,
            BandEdges,
            ybands,
            y,
            nchannels,
            nframes,
            TestWinSize / downsample,
            TestWinStep / downsample,
            downsample);

        for (int xi = 0; xi < nframes; xi++)
        {
            // Frequency masking (low to high): 10 dB/Bark slope.
            for (int bi = 1; bi < Nbands; bi++)
            {
                for (int ci = 0; ci < nchannels; ci++)
                {
                    xb[(((xi * Nbands) + bi) * nchannels) + ci] +=
                        0.1f * xb[(((xi * Nbands) + bi - 1) * nchannels) + ci];
                }
            }

            // Frequency masking (high to low): 15 dB/Bark slope.
            for (int bi = Nbands - 2; bi >= 0; bi--)
            {
                for (int ci = 0; ci < nchannels; ci++)
                {
                    xb[(((xi * Nbands) + bi) * nchannels) + ci] +=
                        0.03f * xb[(((xi * Nbands) + bi + 1) * nchannels) + ci];
                }
            }

            if (xi > 0)
            {
                // Temporal masking: -3 dB/2.5 ms slope.
                for (int bi = 0; bi < Nbands; bi++)
                {
                    for (int ci = 0; ci < nchannels; ci++)
                    {
                        xb[(((xi * Nbands) + bi) * nchannels) + ci] +=
                            0.5f * xb[((((xi - 1) * Nbands) + bi) * nchannels) + ci];
                    }
                }
            }

            // Allow some cross-talk between the channels.
            if (nchannels == 2)
            {
                for (int bi = 0; bi < Nbands; bi++)
                {
                    float l = xb[(((xi * Nbands) + bi) * nchannels) + 0];
                    float r = xb[(((xi * Nbands) + bi) * nchannels) + 1];
                    xb[(((xi * Nbands) + bi) * nchannels) + 0] += 0.01f * r;
                    xb[(((xi * Nbands) + bi) * nchannels) + 1] += 0.01f * l;
                }
            }

            // Apply the mask to every bin of both spectra.
            for (int bi = 0; bi < ybands; bi++)
            {
                for (int xj = BandEdges[bi]; xj < BandEdges[bi + 1]; xj++)
                {
                    for (int ci = 0; ci < nchannels; ci++)
                    {
                        referenceSpectrum[(((xi * Nfreqs) + xj) * nchannels) + ci] +=
                            0.1f * xb[(((xi * Nbands) + bi) * nchannels) + ci];
                        testSpectrum[(((xi * yfreqs) + xj) * nchannels) + ci] +=
                            0.1f * xb[(((xi * Nbands) + bi) * nchannels) + ci];
                    }
                }
            }
        }

        // Average with the previous frame, making the comparison slightly less sensitive.
        for (int bi = 0; bi < ybands; bi++)
        {
            for (int xj = BandEdges[bi]; xj < BandEdges[bi + 1]; xj++)
            {
                for (int ci = 0; ci < nchannels; ci++)
                {
                    float xtmp = referenceSpectrum[(xj * nchannels) + ci];
                    float ytmp = testSpectrum[(xj * nchannels) + ci];
                    for (int xi = 1; xi < nframes; xi++)
                    {
                        float xtmp2 = referenceSpectrum[(((xi * Nfreqs) + xj) * nchannels) + ci];
                        float ytmp2 = testSpectrum[(((xi * yfreqs) + xj) * nchannels) + ci];
                        referenceSpectrum[(((xi * Nfreqs) + xj) * nchannels) + ci] += xtmp;
                        testSpectrum[(((xi * yfreqs) + xj) * nchannels) + ci] += ytmp;
                        xtmp = xtmp2;
                        ytmp = ytmp2;
                    }
                }
            }
        }

        // At lower rates, ignore the last 300 Hz so different transition bands do not count.
        // At 12 kHz nothing is skipped: the last band already skips 400 Hz.
        int maxCompare;
        if (rate == 48000)
        {
            maxCompare = BandEdges[Nbands];
        }
        else if (rate == 12000)
        {
            maxCompare = BandEdges[ybands];
        }
        else
        {
            maxCompare = BandEdges[ybands] - 3;
        }

        double err = 0;
        for (int xi = 0; xi < nframes; xi++)
        {
            double ef = 0;
            for (int bi = 0; bi < ybands; bi++)
            {
                double eb = 0;
                for (int xj = BandEdges[bi]; xj < BandEdges[bi + 1] && xj < maxCompare; xj++)
                {
                    for (int ci = 0; ci < nchannels; ci++)
                    {
                        float re = testSpectrum[(((xi * yfreqs) + xj) * nchannels) + ci] /
                            referenceSpectrum[(((xi * Nfreqs) + xj) * nchannels) + ci];
                        float im = (float)(re - Math.Log(re) - 1);
                        if (xj is >= 79 and <= 81)
                        {
                            // Less sensitive around the SILK/CELT cross-over, to allow for mode
                            // freedom in the filters.
                            im *= 0.1f;
                        }

                        if (xj == 80)
                        {
                            im *= 0.1f;
                        }

                        eb += im;
                    }
                }

                eb /= (BandEdges[bi + 1] - BandEdges[bi]) * nchannels;
                ef += eb * eb;
            }

            // A fixed normalization means slightly lower quality is accepted at lower rates.
            ef /= Nbands;
            ef *= ef;
            err += ef * ef;
        }

        err = Math.Pow(err / nframes, 1.0 / 16);
        // The reference implementation stores Q in a float and fails only when Q < 0.
        float quality = (float)(100 * (1 - (0.5 * Math.Log(1 + err) / Math.Log(1.13))));
        return new Result(!(quality < 0), quality, err);
    }

    private static void BandEnergy(
        float[]? output,
        float[] spectrum,
        int[] bandEdges,
        int bandCount,
        float[] input,
        int nchannels,
        int nframes,
        int windowSize,
        int step,
        int downsample)
    {
        int spectrumSize = windowSize / 2;
        var window = new float[windowSize];
        var cosines = new float[windowSize];
        var sines = new float[windowSize];
        var windowed = new float[nchannels * windowSize];

        for (int xj = 0; xj < windowSize; xj++)
        {
            window[xj] = 0.5f - (0.5f * (float)Math.Cos((2 * OpusPi / (windowSize - 1)) * xj));
            cosines[xj] = (float)Math.Cos((2 * OpusPi / windowSize) * xj);
            sines[xj] = (float)Math.Sin((2 * OpusPi / windowSize) * xj);
        }

        for (int xi = 0; xi < nframes; xi++)
        {
            for (int ci = 0; ci < nchannels; ci++)
            {
                for (int xk = 0; xk < windowSize; xk++)
                {
                    windowed[(ci * windowSize) + xk] =
                        window[xk] * input[(((xi * step) + xk) * nchannels) + ci];
                }
            }

            int xj = 0;
            for (int bi = 0; bi < bandCount; bi++)
            {
                float p0 = 0;
                float p1 = 0;
                for (; xj < bandEdges[bi + 1]; xj++)
                {
                    for (int ci = 0; ci < nchannels; ci++)
                    {
                        float re = 0;
                        float im = 0;
                        int ti = 0;
                        int offset = ci * windowSize;
                        for (int xk = 0; xk < windowSize; xk++)
                        {
                            re += cosines[ti] * windowed[offset + xk];
                            im -= sines[ti] * windowed[offset + xk];
                            ti += xj;
                            if (ti >= windowSize)
                            {
                                ti -= windowSize;
                            }
                        }

                        re *= downsample;
                        im *= downsample;
                        float energy = (re * re) + (im * im) + 100000;
                        spectrum[(((xi * spectrumSize) + xj) * nchannels) + ci] = energy;
                        if (ci == 0)
                        {
                            p0 += energy;
                        }
                        else
                        {
                            p1 += energy;
                        }
                    }
                }

                if (output is not null)
                {
                    int bandWidth = bandEdges[bi + 1] - bandEdges[bi];
                    output[(((xi * bandCount) + bi) * nchannels) + 0] = p0 / bandWidth;
                    if (nchannels == 2)
                    {
                        output[(((xi * bandCount) + bi) * nchannels) + 1] = p1 / bandWidth;
                    }
                }
            }
        }
    }
}
