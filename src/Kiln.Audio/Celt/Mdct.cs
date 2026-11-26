using System.Runtime.CompilerServices;

namespace Kiln.Audio.Celt;

/// <summary>
/// The low-overlap MDCT/IMDCT used by the CELT layer (RFC 6716 §4.3.7), scalar path.
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

    // Scratch. The DCT-IV helper uses _z and _spectrum; Forward/Inverse use _low .. _xs.
    private readonly Complex32[] _z;
    private readonly Complex32[] _spectrum;
    private readonly float[] _low;
    private readonly float[] _high;
    private readonly float[] _temp;
    private readonly float[] _dct0;
    private readonly float[] _dct1;
    private readonly float[] _dct2;
    private readonly float[] _dct3;
    private readonly float[] _xc;
    private readonly float[] _xs;

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

        _z = new Complex32[quarter];
        _spectrum = new Complex32[quarter];
        _low = new float[count];
        _high = new float[count];
        _temp = new float[count];
        _dct0 = new float[count];
        _dct1 = new float[count];
        _dct2 = new float[count];
        _dct3 = new float[count];
        _xc = new float[count];
        _xs = new float[count];
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
    public void Forward(ReadOnlySpan<float> input, Span<float> coefficients, int stride = 1)
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

        for (int n = 0; n < count; n++)
        {
            _low[n] = window[n] * input[n];
        }

        for (int n = 0; n < count; n++)
        {
            int index = count + n;
            _high[n] = index < _length ? window[index] * input[index] : 0f;
        }

        DctIv(_low, _dct0);
        SignFlip(_low, _temp);
        DctIv(_temp, _dct1);
        DctIv(_high, _dct2);
        SignFlip(_high, _temp);
        DctIv(_temp, _dct3);

        int last = count - 1;
        for (int k = 0; k < count; k++)
        {
            float cos = _betaCos[k];
            float sin = _betaSin[k];
            float sign = (k & 1) == 0 ? 1f : -1f;
            float value =
                (cos * _dct0[k])
                - (sin * _dct1[last - k])
                - (sign * cos * _dct3[last - k])
                - (sign * sin * _dct2[k]);
            coefficients[k * stride] = value;
        }
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
    public void Inverse(ReadOnlySpan<float> coefficients, Span<float> output, int stride = 1)
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
        for (int k = 0; k < count; k++)
        {
            float value = coefficients[k * stride];
            float cos = _betaCos[k];
            float sin = _betaSin[k];
            float sign = (k & 1) == 0 ? 1f : -1f;
            _xc[k] = value * cos;
            _xs[k] = sign * value * sin;
        }

        DctIv(_xc, _dct0);
        DctIv(_xs, _dct1);

        float scale = 2f / count;
        int last = count - 1;
        for (int n = 0; n < _length; n++)
        {
            float value;
            if (n < count)
            {
                value = scale * (_dct0[n] - _dct1[last - n]);
            }
            else
            {
                int m = n - count;
                value = -scale * (_dct0[last - m] + _dct1[m]);
            }

            output[n] = _window[n] * value;
        }
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

    private static void SignFlip(ReadOnlySpan<float> input, Span<float> output)
    {
        for (int n = 0; n < input.Length; n++)
        {
            output[n] = (n & 1) == 0 ? input[n] : -input[n];
        }
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
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void DctIv(ReadOnlySpan<float> input, Span<float> output)
    {
        int count = _coefficientCount;
        int quarter = count / 2;
        for (int p = 0; p < quarter; p++)
        {
            float a = input[2 * p];
            float b = input[count - 1 - (2 * p)];
            float cos = _preCos[p];
            float sin = _preSin[p];
            _z[p] = new Complex32((a * cos) + (b * sin), (b * cos) - (a * sin));
        }

        _fft.Forward(_z, _spectrum);

        int last = count - 1;
        for (int k = 0; k < quarter; k++)
        {
            Complex32 spectrum = _spectrum[k];
            float cos = _postCos[k];
            float sin = _postSin[k];
            float real = (spectrum.Real * cos) + (spectrum.Imag * sin);
            float imag = (spectrum.Imag * cos) - (spectrum.Real * sin);
            output[2 * k] = real;
            output[last - (2 * k)] = -imag;
        }
    }
}
