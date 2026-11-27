using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace Kiln.Audio.Simd.Kernels;

/// <summary>The instruction-set tiers a kernel set can be built for, weakest first.</summary>
internal enum AudioKernelTier
{
    /// <summary>Plain scalar loops: the reference path and the <c>KILN_AUDIO_DISABLE_SIMD</c> path.</summary>
    Scalar = 0,

    /// <summary>Portable <see cref="Vector128{T}"/>: SSE2-only x64, WASM and any other 128-bit host.</summary>
    Vector128 = 1,

    /// <summary>arm64: <c>AdvSimd</c>/<c>AdvSimd.Arm64</c> directly, with fused multiply-add.</summary>
    AdvSimdArm64 = 2,

    /// <summary>x64 with AVX2 and FMA3.</summary>
    Avx2Fma = 3,
}

/// <summary>
/// Chooses the kernel tier. The choice is made once per decoder or encoder instance (in its
/// constructor), never inside a frame, mirroring <c>H264KernelSet.CreateBest()</c> in the video
/// codec.
/// </summary>
/// <remarks>
/// Rules enforced by the tier design: no <c>Vector256</c>/<c>Vector512</c>/<c>Vector&lt;T&gt;</c> outside the
/// <see cref="AudioKernelTier.Avx2Fma"/> tier, arm64 uses <c>AdvSimd</c> directly, the kernel is the loop (never a
/// per-sample call), and <c>KILN_AUDIO_DISABLE_SIMD</c> forces <see cref="AudioKernelTier.Scalar"/> here.
/// </remarks>
internal static class AudioKernels
{
    private static readonly AudioKernelTier s_best = DetectBest();
    private static readonly AsyncLocal<AudioKernelTier?> s_scope = new();

    /// <summary>Gets the best tier this process supports (Scalar when SIMD is disabled).</summary>
    internal static AudioKernelTier Best => s_best;

    /// <summary>
    /// Gets the tier a new instance should use: the active <see cref="Scope"/> override if any,
    /// otherwise <see cref="Best"/>. Call this from constructors only, never from a kernel.
    /// </summary>
    internal static AudioKernelTier Select() => s_scope.Value is { } forced && IsSupported(forced) ? forced : s_best;

    /// <summary>Returns whether the host can run the given tier.</summary>
    internal static bool IsSupported(AudioKernelTier tier) => tier switch
    {
        AudioKernelTier.Scalar => true,
        AudioKernelTier.Vector128 => Vector128.IsHardwareAccelerated,
        AudioKernelTier.AdvSimdArm64 => AdvSimd.Arm64.IsSupported,
        AudioKernelTier.Avx2Fma => Avx2.IsSupported && Fma.IsSupported,
        _ => false,
    };

    /// <summary>
    /// Forces <see cref="Select"/> to return <paramref name="tier"/> on this async flow until the
    /// scope is disposed. Used for whole-codec parity tests; consulted only at construction.
    /// </summary>
    internal static IDisposable Scope(AudioKernelTier tier)
    {
        AudioKernelTier? previous = s_scope.Value;
        s_scope.Value = tier;
        return new ScopeToken(previous);
    }

    private static AudioKernelTier DetectBest()
    {
        if (SimdConfig.Disabled)
        {
            return AudioKernelTier.Scalar;
        }

        if (IsSupported(AudioKernelTier.Avx2Fma))
        {
            return AudioKernelTier.Avx2Fma;
        }

        if (IsSupported(AudioKernelTier.AdvSimdArm64))
        {
            return AudioKernelTier.AdvSimdArm64;
        }

        return IsSupported(AudioKernelTier.Vector128) ? AudioKernelTier.Vector128 : AudioKernelTier.Scalar;
    }

    private sealed class ScopeToken(AudioKernelTier? previous) : IDisposable
    {
        public void Dispose() => s_scope.Value = previous;
    }
}
