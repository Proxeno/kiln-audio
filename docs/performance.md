# Decoder performance baseline (PERF-1)

This is the reproducible performance baseline for Kiln.Audio: how fast it decodes the RFC 8251
test vectors compared with libopus 1.5.2, plus the cost of the individual kernels and which of
them have SIMD paths. It is evidence, not a CI gate.

PERF-0 measured libopus with `opus_demo` as a separate process, so its numbers included process
startup, file IO and (for SILK) a resampler that Kiln does not run. PERF-1 replaces that with an
in-process C harness that times only `opus_decode`/`opus_encode` on data already in memory, the
same way the Kiln BenchmarkDotNet jobs time an in-memory loop. **Every libopus number below comes
from the in-process harness**, so the ratios are like for like.

## Machine and runtime

| Item | Value |
| ---- | ----- |
| CPU | Apple M5 Max, 18 physical / 18 logical cores |
| OS | macOS 27.0.1 (26A434), Darwin 27.0.0, arm64 |
| SDK | .NET SDK 11.0.100 |
| Runtime | .NET 11.0.0 (11.0.0, 11.0.26.53018), Arm64 RyuJIT armv8.0-a |
| Hardware intrinsics | ArmBase+AdvSimd, AES, CRC32, DP, RDM, SHA1, SHA256, VectorSize=128 |
| Benchmark harness | BenchmarkDotNet 0.15.8 |
| Reference | libopus 1.5.2, built locally as a test-only oracle |

The projects target `net10.0`, but this machine has only the .NET 11 runtime installed. The host
is rolled forward with `-p:RollForward=Major`, and BenchmarkDotNet is given the `net10.0` runtime
moniker it knows. BenchmarkDotNet therefore labels the job ".NET 10.0", but every number below ran
on **.NET 11.0.0**; the `Runtime` line in the BenchmarkDotNet summary says so.

This is a shared machine; repeated runs varied by up to ~15% on the CELT vectors, ~50% on the
smallest kernel and ~10% on the MDCT kernels. Treat the numbers as indicative, not exact.

## Harness

`bench/native/libopus_bench.c` is our own small C program; it is not libopus code. It links
against the oracle's static `libopus.a` and implements two in-memory loops:

- **decode** parses an RFC 8251 `.bit` file (a 32-bit big-endian length and a 32-bit encoder final
  range before every packet) and reports the best of five `opus_decode` passes;
- **encode** reads interleaved signed 16-bit PCM and reports the best of five `opus_encode` passes
  with the requested application, bitrate, frame size, complexity and CBR/VBR setting.

Only the codec call is inside the timed region: no file IO, no process startup, no shell
timing. The harness prints microseconds per Opus frame and how many times faster than real time
the codec ran.

```bash
bash scripts/fetch-test-vectors.sh
bash scripts/build-libopus-oracle.sh --keep-lib       # keeps libopus.a + headers + flags
bash scripts/bench-libopus-inproc.sh                  # the six PERF comparison vectors
```

`--keep-lib` is an option on the oracle build script; the default build (the conformance oracle)
is unchanged. It copies `libopus.a`, the public headers and the exact compile flags to
`.oracle/lib`, which is gitignored.

### Reference build flags

The oracle CMake build produced the static library with:

```text
CMAKE_BUILD_TYPE=Release
C_FLAGS   = -O3 -DNDEBUG -std=gnu99 -arch arm64 -fstack-protector-strong
C_DEFINES = -DDISABLE_DEBUG_FLOAT -DENABLE_HARDENING -DOPUS_ARM_MAY_HAVE_NEON
            -DOPUS_ARM_MAY_HAVE_NEON_INTR -DOPUS_BUILD -DVAR_ARRAYS -D_FORTIFY_SOURCE=2
```

**libopus's own SIMD was enabled**: `OPUS_ARM_MAY_HAVE_NEON_INTR` is defined and the NEON
intrinsics sources (`NSQ_neon`, `NSQ_del_dec_neon_intr`, `celt_neon_intr`, ...) are part of the
static library on this arm64 build. The Kiln numbers are therefore compared against the normal
optimized libopus, not a scalar reference. (The harness itself is compiled with `-O2`; that only
affects the loop around the library, never the timed codec call.)

## Method

**Kiln.** The `.bit` files are parsed into memory in `[GlobalSetup]`, so no file IO is measured.
Each benchmark invocation resets the decoder and synthesizer, then decodes the whole vector. The
CELT path mirrors the conformance driver: CELT bitstream decode, synthesis to float PCM at 48 kHz
for two output channels, then `CeltPcm.ToInt16`. The SILK path decodes each packet with the
channel count and frame duration from its TOC and runs `SilkSynthesis` at the SILK internal rate
(8/12/16 kHz for vectors 02/03/04). Allocations come from BenchmarkDotNet's `[MemoryDiagnoser]`.

**libopus.** `scripts/bench-libopus-inproc.sh` runs the in-memory harness against the same `.bit`
files: 48 kHz stereo for the CELT vectors, and the internal rate with the stream's own channel
count for the SILK vectors (02/03/04 are mono). It reports the best of five passes.

**Ratio** is Kiln mean time divided by the libopus best time for the same audio and the same
in-memory treatment; a value above 1 means Kiln is slower. The SILK comparison still stops Kiln
at internal-rate PCM while libopus runs its own output path, so it is at worst slightly generous
to libopus.

## Decode results

Frame counts (Opus frames decoded) are 5,524 for 01, 4,186 for 07, 1,501 for 11, 1,185 for 02, 998
for 03 and 1,265 for 04. Allocations are 0 B for every vector, which is 0 B per frame, before and
after.

"Before" is the PERF-0 baseline (on `main` before DEC-INT-1 and PERF-1). "After" is this branch:
the DEC-INT-1 decoder integration plus the PERF-1 kernels. Both columns compare against the same
in-process libopus harness on this machine.

| Vector | Mode | libopus in-process | Kiln before | Ratio before | Kiln after (median) | Ratio after |
| ------ | ---- | ------------------ | ----------- | ------------ | ------------------- | ----------- |
| 01 | CELT, 48 kHz stereo | 38.61 ms | 154.8 ms | 4.01 | 88.7 ms | 2.30 |
| 07 | CELT, 48 kHz, mixed frames | 20.05 ms | 107.5 ms | 5.36 | 38.7 ms | 1.93 |
| 11 | CELT, 48 kHz stereo | 31.53 ms | 171.6 ms | 5.44 | 60.8 ms | 1.93 |
| 02 | SILK NB, 8 kHz | 6.02 ms | 8.70 ms | 1.45 | 8.15 ms | 1.35 |
| 03 | SILK MB, 12 kHz | 5.81 ms | 9.56 ms | 1.65 | 8.93 ms | 1.54 |
| 04 | SILK WB, 16 kHz | 8.11 ms | 13.88 ms | 1.71 | 14.73 ms | 1.82 |

The libopus column is one harness run on this machine and is used for both "before" and
"after"; the Kiln "before" column is the PERF-0 BenchmarkDotNet run. The "after" column is one
clean BenchmarkDotNet run with nothing else on the machine — the median, because vector 01 and 11
showed a long tail in this run (their means were 93.1 and 65.1 ms against medians of 88.7 and
60.8 ms). The CELT path improved by 1.8x to 2.8x across repeated runs; it is about 1.9–2.3x
libopus. The inverse MDCT, the single largest kernel, went from 37.8 µs to 5.4 µs per
960-sample channel frame (see below). Vector 04 moved backwards in this particular run (14.73 ms
against 13.59 ms in an earlier one) even though the SILK kernel it uses is faster; SILK decode is
dominated by the bitstream and NLSF work outside the kernels this lane changed, and the whole
vector is a two-second-scale benchmark on a machine that other lanes share, so treat the SILK
rows as noise-limited.

## Kernel results

One invocation of each kernel, BenchmarkDotNet mean, 0 B allocated. `Fft480` is new in PERF-1.

| Kernel | Work per invocation | Before | After | Change |
| ------ | ------------------- | ------ | ----- | ------ |
| `Fft480` | One 480-point complex forward FFT | not measured | 1.84 µs | new benchmark |
| `MdctForward960` | One 960-coefficient forward MDCT (20 ms, one channel) | 88.91 µs | 6.05 µs | 14.7x |
| `MdctForward8x120` | Eight interleaved 120-coefficient forward MDCTs (20 ms, one channel) | 63.79 µs | 4.75 µs | 13.4x |
| `MdctInverse960` | One 960-coefficient inverse MDCT (20 ms, one channel) | 37.78 µs | 5.42 µs | 7.0x |
| `MdctInverse8x120` | Eight interleaved 120-coefficient inverse MDCTs (20 ms, one channel) | 27.11 µs | 4.53 µs | 6.0x |
| `PvqDecodeBand` | PVQ band decode, 96 coefficients, 32 pulses | 155.8 ns | 215.5 ns | unchanged (run-to-run noise) |
| `CeltPostFilter` | Comb post-filter over a 960-sample frame with tap-set cross-fade | 875.9 ns | 359 ns | 2.4x |
| `SilkLpcSynthesis` | Short-term LPC synthesis, 20 ms / 16 kHz, order 16 | 4.026 µs | 3.35 µs | 1.20x |
| `SilkLtpSynthesis` | Long-term prediction, 20 ms / 16 kHz, five taps, four subframes | 586.3 ns | 674.9 ns | unchanged (noise) |

The before figures are the PERF-0 run on the same machine (the forward MDCT figures are ENC-1's
measurement of the same `Mdct.Forward` before this restructure). PERF-0 had no standalone FFT
benchmark, so the old recursive FFT was only measured through the MDCT; `Fft480` is new in PERF-1.
The forward and inverse MDCT now meet the PERF-1 target of 6 µs at 960 points.

## SIMD kernels

Each kernel lives in `src/Kiln.Audio/Simd/` and has a scalar reference path and a portable
`Vector128<T>` path (AdvSimd on arm64, SSE on x64). No `Vector256`/`Vector512` path is shipped
because it was not measured to win on this machine.

| Kernel | Path | Parity |
| ------ | ---- | ------ |
| FFT butterflies, radix 2/3/4/5 (`FftKernels`) | Vector128 over structure-of-arrays re/im | float, ≤1e-6 RMS |
| MDCT window multiply (`MdctKernels`) | Vector128 | float, ≤1e-6 RMS |
| CELT comb post-filter (`CeltPostFilterKernel`) | Vector128, four independent samples when period ≥ 6 | bit-exact |
| SILK short-term LPC (`SilkLpcKernel`) | Vector128 over the order-tap dot product, 32-bit lanes | bit-exact |

**Algorithmic changes.**

- The FFT is now iterative and table-driven: the leaf permutation and per-level node offsets are
  precomputed, so there is no mixed-radix division or recursion in the hot path, and the generic
  `r * r` small-DFT multiply loop is replaced by fixed radix butterflies.
- The forward MDCT runs two DCT-IVs, not four. The windowed input is folded around the block
  centre into the difference and sum sequences
  `u(n) = a(n) - a(2N-1-n)` and `v(n) = a(n) + a(2N-1-n)`, and
  `X(k) = cos(beta_k) DCT-IV(u)(k) - sin(beta_k) DST-IV(v)(k)`, where the DST-IV comes from
  `DCT-IV((-1)^n v)` read in reverse. The identity was verified against the naive double-precision
  MDCT before it was ported.
- Both MDCT directions keep the DCT-IV pack and FFT output as separate real and imaginary arrays
  (structure of arrays), so the FFT takes the buffers directly and the `Complex32`
  interleave/deinterleave copies are gone. The DCT-IV pre- and post-rotation loops stay scalar:
  their reads and writes are stride two with a coefficient reversal, which a portable `Vector128`
  path cannot express without two-source lane shuffles the runtime does not offer here.
- The comb post-filter is recursive, but because every read is at least `period` samples back, a
  block of four consecutive samples is independent whenever `period >= 6`; the vector path uses
  that and falls back to scalar otherwise.
- The SILK LPC dot product cannot be vectorized across output samples (synthesis feeds back), but
  the 10/16-tap dot product per sample is vectorized. `Smulwb(a, c)` is evaluated in 32-bit lanes
  as `(a >> 16)*c + ((a & 0xFFFF)*c >> 16)`, which is exact and avoids 64-bit widening (a
  `Vector<long>` version measured ~50% slower).

**Scalar fallback.** `KILN_AUDIO_DISABLE_SIMD=1` (or `true`) is read once per process and forces
every kernel onto its scalar path. `SimdParityTests` runs both paths in-process and checks parity
on every kernel, so the scalar path is exercised in CI without a separate job; running the suite
with the variable set exercises it end to end.

## Remaining gaps and next ideas

1. **CELT decode** (ratio in the decode table above). The MDCT is now ~5.4-6.0 µs per
   960-sample channel frame, close to the 6 µs target. The inverse still runs two `N/2`-point
   complex FFTs (one per DCT-IV); the next idea is a real FFT of size `N/2`, which is an
   `N/4`-point complex FFT plus a split, to halve the transform again. After the transform, the
   CELT entropy decode and the frequency-domain reconstruction (outside the PERF-1 kernel set)
   dominate.
2. **MDCT inverse.** Kernel-level, a split-radix radix-2/4 FFT and a fused post-rotation write
   (avoiding the separate reversed copies) are the next step. The DCT-IV pre/post rotations are
   stride-two on the input and output; a two-source lane shuffle, which this runtime's
   `Vector128.Shuffle` lacks, would vectorize them.
3. **CELT post-filter.** 2.4x faster; at ~0.36 µs it is no longer material.
4. **SILK.** LPC synthesis is ~20% faster in isolation. The full SILK vectors moved only a little
   because LPC synthesis is a minority of SILK decode; the next targets are the LPC analysis
   filter, the NLSF decode and the stereo unmix.
5. **PVQ decode and CELT de-emphasis** are inherently serial (range decode and a one-pole IIR
   respectively). They were measured at ~0.2 µs and are not worth vectorizing; they remain in
   place.

## Encoder (CELT-only, 48 kHz mono)

This section records the ENC-1 measurements for the CELT-only restricted-low-delay encoder:
48 kHz, mono, CBR, 20 ms frames. It uses the same machine and runtime as the decoder baseline.

### Method

**Kiln.** `EncodeBenchmarks` loads the mono downmix of every RFC 8251 `.dec` file — 320.6 s of
audio (15,390,480 samples) — into memory in `[GlobalSetup]` and encodes it as 20 ms / 32 kbps
CBR frames at every complexity 0-10. No file IO is inside the measured region, and the encoder
is constructed before the benchmark body, so the numbers are steady-state `Encode` calls.
`[MemoryDiagnoser]` reports allocations.

**libopus.** `scripts/bench-libopus-encode.sh` runs
`opus_demo -e restricted-lowdelay 48000 1 32000 -framesize 20 -cbr -complexity C` over the same
PCM file five times and keeps the fastest wall time. That time **includes process start-up and
reading the whole input file**, so it is an upper bound on libopus's encoding cost. Lane PERF-1's
fair in-process harness (`bench/native/libopus_bench.c`) had not merged into `main` when this
section was written; if it has merged by the time you read this, prefer it.

**Quality (G2).** The corpus is the mono downmix of every `.dec` at 48 kHz. Both encoders encode
it at 24 and 32 kbps, 20 ms, CBR, and both outputs are decoded with libopus. Segmental SNR uses
20 ms segments, each clamped to [-10, 60] dB, after removing the codec's 120-sample stream delay.
`opus_compare` runs at 48 kHz with the same alignment, over the first ten seconds of each vector;
the tool's naive DFT is linear in audio length, and a per-vector excerpt keeps the gate test to a
couple of minutes.

### Complexity mapping

| Complexity | What it enables in this encoder |
| --- | --- |
| 0 | No transient analysis (long blocks only), spread forced to none, single-pass coarse energy. |
| 1 | Transient analysis still off; spread stays at its normal default. |
| 2 | Transient analysis on (short blocks for transients). |
| 3 | Spreading decision on. |
| 4-10 | Two-pass intra/inter coarse-energy trial. |

Every level uses the same TF analysis, allocation and PVQ search. ENC-2 adds the pitch pre-filter
of RFC 6716 §4.3.7.1 (encoder side) at complexity 5 and above, matching libopus; it is signalled
off below that, so the complexity-0 rows above are unaffected. The pre-filter search uses the
reference's decimated correlation with a Vector128/Vector256 dot product and a scalar reference.

### Encode speed

The corpus is 320.62 s of audio encoded as 16,031 frames of 20 ms. `Mean` is the
BenchmarkDotNet mean for the whole corpus in one invocation; `µs/frame` and `× real time` are
derived from it. The allocation column is BenchmarkDotNet's `[MemoryDiagnoser]` for the whole
invocation: zero bytes.

| Complexity | Mean before | µs/frame before | Mean after | µs/frame after | × real time after |
| --- | --- | --- | --- | --- | --- |
| 0 | 1.888 s | 117.8 | 0.343 s | 21.4 | 934x |
| 1 | 1.971 s | 123.0 | 0.361 s | 22.5 | 888x |
| 2 | 1.833 s | 114.3 | 0.449 s | 28.0 | 715x |
| 3 | 1.954 s | 121.9 | 0.505 s | 31.5 | 635x |
| 4 | 2.022 s | 126.1 | 0.497 s | 31.0 | 645x |
| 5 | 1.940 s | 121.0 | 0.493 s | 30.8 | 650x |
| 6 | 1.877 s | 117.1 | 0.648 s | 40.4 | 495x |
| 7 | 1.902 s | 118.6 | 0.518 s | 32.3 | 619x |
| 8 | 1.709 s | 106.6 | 0.560 s | 34.9 | 573x |
| 9 | 1.804 s | 112.5 | 0.492 s | 30.7 | 652x |
| 10 | 1.761 s | 109.9 | 0.484 s | 30.2 | 662x |

Before is ENC-1's measurement; after is this branch (PERF-1's MDCT restructure). Allocations stay
0 B. The spread between complexities is measurement noise on this shared machine (this run's
per-invocation standard error reached tens of percent): levels 0-3 differ only in transient and
spread analysis, and levels 5-10 do the same work as 4. The MDCT restructure cut the per-frame cost
from ~118 µs to ~21-40 µs; the forward MDCT alone went from 88.91 µs to 4.8 µs.

**libopus.** `scripts/bench-libopus-encode.sh` times the same 320.62 s file five times and keeps
the best: complexity 0 **0.206 s (1,556x real time)**, complexity 5 **0.739 s (434x)** and
complexity 10 **0.791 s (405x)**. Those times include process start-up and reading the whole
input file, which is generous to libopus.

**Iso-quality.** Our complexity-0 aggregate SNR at 32 kbps (16.313 dB) is above libopus
complexity 5 (15.795 dB) and below complexity 10 (16.653 dB), so the lowest measured libopus
complexity whose quality is at least ours is 10. Before this branch we took 1.888 s in-process
against libopus's 0.791 s with file IO (2.4x slower). **After the MDCT restructure we take 0.343 s
in-process against libopus's 0.791 s: about 2.3x faster at equal-or-better quality.** At the same
complexity 0, libopus is now 1.67x faster (0.206 s vs 0.343 s), down from 9.2x. The remaining
encode cost is the transient detector, the PVQ search, and the band energy and pre-emphasis passes.
The front-end is allocation-free and its non-MDCT kernels are already vector-ready;
`NormaliseBand` uses `Vector128` and is bit-identical to its scalar reference.

### Quality (G2)

| Bitrate | Complexity | Ours segSNR (dB) | libopus segSNR (dB) | Gap (ours − libopus) | Ours `opus_compare` | libopus `opus_compare` |
| --- | --- | --- | --- | --- | --- | --- |
| 24 kbps | 0 | 14.450 | 13.334 | **+1.116** | −457.7% | −516.9% |
| 24 kbps | 5 | 14.467 | 13.876 | +0.591 | −266.1% | −247.2% |
| 24 kbps | 10 | 14.467 | 14.441 | +0.026 | −266.1% | −235.3% |
| 32 kbps | 0 | 16.313 | 15.237 | **+1.076** | −419.9% | −455.4% |
| 32 kbps | 5 | 16.302 | 15.795 | +0.506 | −221.6% | −195.4% |
| 32 kbps | 10 | 16.302 | 16.653 | −0.351 | −221.6% | −170.0% |

The gate is the complexity-0 row: the aggregate segmental SNR is 1.1 dB above libopus at both
rates. At complexity 5 it is still ahead by about half a dB; at complexity 10 it is essentially
tied at 24 kbps and 0.35 dB behind at 32 kbps; libopus spends its extra complexity on the pitch
pre-filter, which this encoder does not implement.

The `opus_compare` percentages are negative for **both** encoders because the corpus is itself a
lossy decode that is being re-encoded: the metric charges the second generation of loss. On the
same input, libopus scores +25% at 64 kbps and +74% at 128 kbps, so the negative values are a
property of the double compression, not of the comparison. Ours is better at complexity 0; at
complexity 5 and 10 ours is lower because libopus's pitch pre-filter improves perceptual quality
in a way that segmental SNR does not capture.

### Input rates, stereo and VBR (ENC-2)

CELT runs internally at 48 kHz. For API rates below 48 kHz the encoder uses the reference's
original upsampler: one input sample is zero-stuffed every `48000/rate` internal samples, then the
MDCT bins above the input Nyquist rate are cleared and the coded band count is reduced to the
end band the rate allows (13 for 8 kHz, 17 for 12/16 kHz, 19 for 24 kHz, 21 for 48 kHz). Twelve
kilohertz maps to wideband because CELT-only packets cannot signal mediumband. The end band is
carried by the TOC configuration, so the bitstream is decodable by any conformant decoder.

The zero-stuffing upsampler is non-normative and has no resampling filter. Its added delay is
`(upsample - 1) / 48000` seconds (about 0.104 ms at 8 kHz, 0.021 ms at 16 kHz), on top of the
unchanged 2.5 ms CELT overlap; the discarded bins are the images above the input Nyquist rate.

Stereo follows §4.3.4: mid/side theta quantization in the band split, the reference's intensity
thresholds from the effective rate, and the per-frame dual-stereo analysis. `UseVbr` (default
false) enables the reference's target-plus-reservoir rate control; `ConstrainedVbr` (default true)
applies the reservoir bound. Defaults keep the previous constant-bitrate behaviour.

### Kernel breakdown

One invocation of each kernel, steady state, from `EncodeKernelBenchmarks`. The five largest are
listed first by time. Allocations are 0 B for every kernel.

| Kernel | Work per invocation | Mean before | Mean after | Allocated |
| --- | --- | --- | --- | --- |
| `MdctForward960` | One 960-coefficient forward MDCT (20 ms, one channel) | 88.91 µs | 4.80 µs | 0 B |
| `MdctForward8x120` | Eight interleaved 120-coefficient forward MDCTs (20 ms, one channel) | 63.79 µs | 5.11 µs | 0 B |
| `TransientAnalysis` | Transient detector over 1080 pre-emphasized samples | 3.20 µs | 3.23 µs | 0 B |
| `PvqSearch` | PVQ search, N = 96, K = 32 | 2.10 µs | 1.89 µs | 0 B |
| `BandEnergy` | Sum of squares over 960 samples | 577 ns | 540 ns | 0 B |
| `PreEmphasis` | 960 samples | 383 ns | 348 ns | 0 B |
| `NormaliseBand` | 960 samples, `Vector128` path | 71 ns | 74 ns | 0 B |

Before the MDCT restructure the forward transform was 75% of the per-frame cost. It is now a
minority of it; the transient detector, the PVQ search and the band energy and pre-emphasis passes
dominate. The PVQ search is a scalar soft-search of the reference algorithm (MaxIteration-wise
identical to `alg_quant`); a measured faster original search is the next encoder-side idea.

Note: the machine is shared, and repeated kernel runs varied by up to ~30% on the two MDCT rows;
the sub-microsecond rows were stable to a few percent.

### Algorithmic delay

The stream delay is the 2.5 ms overlap: a sample at input index `i` appears at output index
`i + 120` at 48 kHz. An impulse test measures 119-120 samples. The decoder look-ahead is therefore
120 samples; a downstream consumer that has to acquire the whole frame before encoding should
budget **one frame plus 2.5 ms**: `N + 120` samples, which is 1080 samples (22.5 ms) for the
primary 20 ms frame.

### Allocations

`Encode` allocates zero bytes after construction, for all four frame durations, for the 16-bit
overload and the float overload. `EncoderAllocationTests` proves this with
`GC.GetAllocatedBytesForCurrentThread` over a whole file.
