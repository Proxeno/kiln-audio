using System.Runtime.CompilerServices;
using Kiln.Audio.Simd;

namespace Kiln.Audio.Celt;

/// <summary>
/// The low-overlap MDCT/IMDCT used by the CELT layer (RFC 6716 §4.3.7).
/// </summary>
/// <remarks>
/// <para>
/// Let <c>N</c> be the number of MDCT coefficients and <c>O</c> the overlap (120 for the 48 kHz
/// modes). One frame consumes <c>N + O</c> time samples - the current frame plus <c>O</c> lookahead
/// samples - and produces <c>N</c> coefficients. The analysis transform is
/// </para>
/// <code>
/// X(k) = sum_{n=0}^{N+O-1} w(n) x(n) cos(pi/N * (n + 1/2 + N - O/2) * (k + 1/2)), 0 &lt;= k &lt; N
/// </code>
/// <para>
/// where <c>w</c> is unity over <c>[O, N)</c>, rises over <c>[0, O)</c> with
/// <see cref="CeltWindow"/> and falls over <c>[N, N+O)</c> as the mirror of the rising half. The
/// phase <c>N - O/2</c> is the low-overlap generalisation of the textbook MDCT phase <c>N/2</c>
/// (which is recovered for <c>O = N</c>); it is what makes the window power-complementary after
/// overlap-add. The inverse is the transpose:
/// </para>
/// <code>
/// y(n) = (2/N) sum_{k=0}^{N-1} X(k) cos(pi/N * (n + 1/2 + N - O/2) * (k + 1/2)), 0 &lt;= n &lt; N+O
/// </code>
/// <para>
/// followed by multiplication by <c>w(n)</c>. Consequently overlap-add of consecutive frames
/// (hop <c>N</c>) reconstructs the input with unity gain: the last <c>O</c> output samples of a
/// frame are added to the first <c>O</c> samples of the next frame.
/// </para>
/// <para>
/// <b>Scaling.</b> For one isolated frame, <c>Inverse(Forward(x))</c> returns
/// <c>w(n)^2 x(n) + a(n)</c>, where <c>a(n)</c> is the MDCT time-domain alias (the cosine basis at
/// <c>n</c> also collects the mirrored samples). The overlap-add above cancels <c>a(n)</c> and
/// leaves <c>(w(n)^2 + w(n+N)^2) x(n) = x(n)</c>. In other words the forward transform is the
/// unnormalised RFC 6716 §4.3.7 sum, the inverse carries the <c>2/N</c> factor, and a decoder that
/// supplies coefficients at the corresponding gain reconstructs the signal with unity gain - no
/// further normalisation is applied.
/// </para>
/// <para>
/// <b>Short-block interleaving.</b> An instance transforms one block. For a transient frame CELT
/// splits it into <c>B</c> short blocks (each a 2.5 ms, <c>N = 120</c> transform) and interleaves
/// their coefficients: coefficient <c>k</c> of short block <c>b</c> is stored at
/// <c>X[k * B + b]</c>. To produce that layout call <see cref="Forward"/> once per block with
/// <c>stride = B</c> and the output span advanced by <c>b</c>; <see cref="Inverse"/> consumes the
/// same layout with the same stride.
/// </para>
/// <para>
/// <b>PERF-1 structure.</b> Each DCT-IV runs one size-<c>N/2</c> complex FFT, and both directions
/// need two of them. The forward folds the windowed input around the block centre into the
/// difference and sum sequences and combines <c>DCT-IV(u)</c> with the reversed
/// <c>DST-IV(v) = DCT-IV((-1)^n v)</c>; the inverse runs the cosine and sine coefficient sequences.
/// The gain over PERF-0 comes from the FFT itself: the generic <c>r * r</c> small-DFT loop is
/// replaced by specialised radix-2/3/4/5 butterflies over separate real and imaginary arrays (see
/// <see cref="FftKernels"/>), so the combine stage vectorises with <c>Vector128</c>. The DCT-IV
/// pack and FFT output are structure-of-arrays in <see cref="MdctTwiddleKernel"/>, and the final
/// window multiply is a contiguous elementwise pass vectorised in <see cref="MdctKernels"/>.
/// </para>
/// <para>
/// Instances hold mutable scratch buffers and are therefore not thread-safe. Allocate one per
/// concurrent decoder. Execute methods allocate no managed memory.
/// </para>
/// </remarks>
internal sealed class Mdct
{
    private readonly int _coefficientCount;
    private readonly int _overlap;
    private readonly int _length;
    private readonly float[] _window;
    private readonly Fft _fft;

    // Phase and twiddle tables, computed once in double and stored as float.
    private readonly float[] _betaCos;
    private readonly float[] _betaSin;
    private readonly float[] _preCos;
    private readonly float[] _preSin;
    private readonly float[] _postCos;
    private readonly float[] _postSin;

    // Scratch. The DCT-IV packing and FFT output are structure-of-arrays so the pre/post twiddle
    // stages are contiguous and vectorizable; Forward uses _low, _high, _dct*.
    private readonly float[] _packReal;
    private readonly float[] _packImaginary;
    private readonly float[] _spectrumReal;
    private readonly float[] _spectrumImaginary;
    private readonly float[] _low;
    private readonly float[] _high;
    private readonly float[] _dct0;
    private readonly float[] _dct1;
    private readonly float[] _dct0Reverse;
    private readonly float[] _dct1Reverse;

    /// <summary>
    /// Initializes a transform for one CELT block size.
    /// </summary>
    /// <param name="coefficientCount">
    /// The number of MDCT coefficients: 120, 240, 480 or 960 at 48 kHz.
    /// </param>
    /// <param name="overlap">The overlap length; the CELT 48 kHz modes use 120 samples.</param>
    public Mdct(int coefficientCount, int overlap = CeltWindow.DefaultOverlap)
    {
        if (coefficientCount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(coefficientCount), "The coefficient count must be positive.");
        }

        if (overlap <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(overlap), "The overlap must be positive.");
        }

        if (coefficientCount < overlap)
        {
            throw new ArgumentOutOfRangeException(
                nameof(overlap), "The overlap cannot exceed the coefficient count.");
        }

        if ((coefficientCount & 1) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(coefficientCount), "The coefficient count must be even.");
        }

        _coefficientCount = coefficientCount;
        _overlap = overlap;
        _length = coefficientCount + overlap;
        _fft = Fft.Get(coefficientCount / 2);
        _window = BuildWindow(coefficientCount, overlap);

        int count = coefficientCount;
        int quarter = count / 2;
        _betaCos = new float[count];
        _betaSin = new float[count];
        double phaseOffset = count - (overlap / 2.0);
        for (int k = 0; k < count; k++)
        {
            double beta = Math.PI * phaseOffset * (k + 0.5) / count;
            _betaCos[k] = (float)Math.Cos(beta);
            _betaSin[k] = (float)Math.Sin(beta);
        }

        _preCos = new float[quarter];
        _preSin = new float[quarter];
        _postCos = new float[quarter];
        _postSin = new float[quarter];
        for (int p = 0; p < quarter; p++)
        {
            double pre = Math.PI * ((4.0 * p) + 1.0) / (4.0 * count);
            _preCos[p] = (float)Math.Cos(pre);
            _preSin[p] = (float)Math.Sin(pre);

            double post = Math.PI * p / count;
            _postCos[p] = (float)Math.Cos(post);
            _postSin[p] = (float)Math.Sin(post);
        }

        _packReal = new float[quarter];
        _packImaginary = new float[quarter];
        _spectrumReal = new float[quarter];
        _spectrumImaginary = new float[quarter];
        _low = new float[count];
        _high = new float[count];
        _dct0 = new float[count];
        _dct1 = new float[count];
        _dct0Reverse = new float[count];
        _dct1Reverse = new float[count];
    }

    /// <summary>Gets the number of MDCT coefficients produced per block.</summary>
    public int CoefficientCount => _coefficientCount;

    /// <summary>Gets the overlap length in samples.</summary>
    public int Overlap => _overlap;

    /// <summary>Gets the number of time samples consumed by <see cref="Forward"/> and produced by <see cref="Inverse"/>.</summary>
    public int Length => _length;

    /// <summary>Computes the windowed low-overlap MDCT of one block.</summary>
    /// <param name="input">
    /// The current frame followed by <see cref="Overlap"/> lookahead samples; at least
    /// <see cref="Length"/> samples.
    /// </param>
    /// <param name="coefficients">
    /// Receives the coefficients. Coefficient <c>k</c> is written to index <c>k * stride</c>.
    /// </param>
    /// <param name="stride">
    /// Distance in samples between consecutive coefficients. Use 1 for a contiguous block, or the
    /// number of short blocks <c>B</c> to write the interleaved transient layout
    /// <c>X[k * B + b]</c> (advance the output span by the block index).
    /// </param>
    public void Forward(ReadOnlySpan<float> input, Span<float> coefficients, int stride = 1) =>
        ForwardCore(input, coefficients, stride, SimdConfig.UseSimd);

    /// <summary>Forward transform on the scalar path, used by the parity tests.</summary>
    /// <param name="input">The windowed input.</param>
    /// <param name="coefficients">Receives the coefficients.</param>
    /// <param name="stride">Distance in samples between consecutive coefficients.</param>
    internal void ForwardScalar(ReadOnlySpan<float> input, Span<float> coefficients, int stride = 1) =>
        ForwardCore(input, coefficients, stride, useVector: false);

    private void ForwardCore(ReadOnlySpan<float> input, Span<float> coefficients, int stride, bool useVector)
    {
        ValidateStride(stride);
        if (input.Length < _length)
        {
            throw new ArgumentException(
                $"The input must contain {_length} samples.", nameof(input));
        }

        if (coefficients.Length < (((long)_coefficientCount - 1) * stride) + 1)
        {
            throw new ArgumentException(
                "The coefficient buffer is too short for the requested stride.", nameof(coefficients));
        }

        int count = _coefficientCount;
        float[] window = _window;
        int length = _length;

        // The forward MDCT needs two real DCT-IVs, not four: fold the windowed input around the
        // block centre into the difference and sum sequences
        //   u(n) = a(n) - a(2N-1-n),  v(n) = a(n) + a(2N-1-n),
        // with a zero outside [0, N+O). The MDCT is then
        //   X(k) = cos(beta_k) DCT-IV(u)(k) - sin(beta_k) DST-IV(v)(k),
        // and DST-IV(v) = DCT-IV((-1)^n v)(N-1-k), so one DCT-IV of the sign-flipped sum supplies
        // the whole transform (see docs/performance.md).
        for (int n = 0; n < count; n++)
        {
            float lower = window[n] * input[n];
            int mirror = (2 * count) - 1 - n;
            float upper = mirror < length ? window[mirror] * input[mirror] : 0f;
            _low[n] = lower - upper;
            float sum = lower + upper;
            _high[n] = (n & 1) == 0 ? sum : -sum;
        }

        DctIv(_low, _dct0, _dct0Reverse, useVector);
        DctIv(_high, _dct1, _dct1Reverse, useVector);

        MdctKernels.CombineForward(
            coefficients, _dct0, _dct1Reverse, _betaCos, _betaSin, count, stride, useVector);
    }

    /// <summary>Computes the windowed inverse MDCT of one block.</summary>
    /// <param name="coefficients">
    /// The coefficients of one block. Coefficient <c>k</c> is read from index <c>k * stride</c>.
    /// </param>
    /// <param name="output">
    /// Receives <see cref="Length"/> windowed samples. The first <see cref="Overlap"/> samples
    /// belong to the overlap with the previous frame; after adding the previous frame's saved tail
    /// the first <see cref="CoefficientCount"/> samples are the reconstructed output and the last
    /// <see cref="Overlap"/> samples are the tail saved for the next frame.
    /// </param>
    /// <param name="stride">
    /// Distance in samples between consecutive coefficients; see <see cref="Forward"/>.
    /// </param>
    public void Inverse(ReadOnlySpan<float> coefficients, Span<float> output, int stride = 1) =>
        InverseCore(coefficients, output, stride, SimdConfig.UseSimd);

    /// <summary>Inverse transform on the scalar path, used by the parity tests.</summary>
    /// <param name="coefficients">The coefficients of one block.</param>
    /// <param name="output">Receives the windowed samples.</param>
    /// <param name="stride">Distance in samples between consecutive coefficients.</param>
    internal void InverseScalar(ReadOnlySpan<float> coefficients, Span<float> output, int stride = 1) =>
        InverseCore(coefficients, output, stride, useVector: false);

    private void InverseCore(ReadOnlySpan<float> coefficients, Span<float> output, int stride, bool useVector)
    {
        ValidateStride(stride);
        if (coefficients.Length < (((long)_coefficientCount - 1) * stride) + 1)
        {
            throw new ArgumentException(
                "The coefficient buffer is too short for the requested stride.", nameof(coefficients));
        }

        if (output.Length < _length)
        {
            throw new ArgumentException(
                $"The output must hold {_length} samples.", nameof(output));
        }

        int count = _coefficientCount;

        // Fuse the beta pre-multiply into one pass that produces the two real DCT-IV inputs:
        // xc(k) = C(k) cos(beta_k) and xs(k) = (-1)^k C(k) sin(beta_k).
        for (int k = 0; k < count; k++)
        {
            float value = coefficients[k * stride];
            _low[k] = value * _betaCos[k];
            _high[k] = ((k & 1) == 0 ? value : -value) * _betaSin[k];
        }

        DctIv(_low, _dct0, _dct0Reverse, useVector);
        DctIv(_high, _dct1, _dct1Reverse, useVector);

        float scale = 2f / count;
        MdctKernels.WindowFirst(output, _dct0, _dct1Reverse, _window, scale, count, useVector);
        MdctKernels.WindowSecond(
            output.Slice(count), _dct0Reverse, _dct1, _window.AsSpan(count), scale, _overlap, useVector);
    }

    private static float[] BuildWindow(int coefficientCount, int overlap)
    {
        int length = coefficientCount + overlap;
        var window = new float[length];
        float[] rising = CeltWindow.Create(overlap);
        Array.Copy(rising, window, overlap);

        for (int n = overlap; n < coefficientCount; n++)
        {
            window[n] = 1f;
        }

        for (int n = coefficientCount; n < length; n++)
        {
            window[n] = rising[length - 1 - n];
        }

        return window;
    }

    private void ValidateStride(int stride)
    {
        if (stride <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(stride), "The stride must be positive.");
        }
    }

    /// <summary>
    /// Computes the size-<c>N</c> DCT-IV of <paramref name="input"/> into <paramref name="output"/>
    /// with one size-<c>N/2</c> complex FFT, using the packing
    /// <c>z[p] = (x[2p] + i*x[N-1-2p]) e^(-i*pi*(4p+1)/(4N))</c>, the FFT, and the post-rotation
    /// <c>t = Z[k] e^(-i*pi*k/N)</c> with <c>D[2k] = Re(t)</c>, <c>D[N-1-2k] = -Im(t)</c>.
    /// </summary>
    /// <param name="input">The real input sequence.</param>
    /// <param name="output">Receives the DCT-IV result.</param>
    /// <param name="outputReverse">
    /// Optional; when present, also receives the result in reverse order so the caller can combine
    /// the two DCT-IV outputs with contiguous elementwise passes.
    /// </param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void DctIv(ReadOnlySpan<float> input, Span<float> output, float[]? outputReverse, bool useVector)
    {
        int count = _coefficientCount;

        MdctTwiddleKernel.PreTwiddle(
            input, count, _preCos, _preSin, _packReal, _packImaginary);

        _fft.Transform(
            _packReal, _packImaginary, _spectrumReal, _spectrumImaginary, inverse: false, useVector);

        MdctTwiddleKernel.PostTwiddle(
            _spectrumReal,
            _spectrumImaginary,
            count,
            _postCos,
            _postSin,
            output,
            outputReverse);
    }
}
