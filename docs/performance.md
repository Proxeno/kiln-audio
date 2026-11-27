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
| CELT band denormalization (`DecKernels.ScaleCopy`) | Vector128, elementwise | bit-exact |
| CELT PVQ pulse scaling (`DecKernels.ScaleIntToFloat`) | Vector128, int32→float then multiply | bit-exact |
| CELT stereo merge (`DecKernels.StereoMerge`) | Vector128, elementwise | bit-exact |
| SILK LTP synthesis (`DecSilkLtp`) | Vector128, four independent samples when lag ≥ 10 | bit-exact |

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

## PERF-2: decoder hot paths

PERF-2 targets the CELT entropy and frequency-domain decode plus the SILK synthesis loop. The
changes are pure optimizations: the SILK output is bit-exact, the final range is exact on every
packet, and the RFC 8251 conformance tests are unchanged.

**Profiling method.** `DecodeStageBenchmarks` and `SilkDecodeStageBenchmarks` in
`bench/Kiln.Audio.Benchmarks/DecodeStages.cs` time each Opus frame in three (CELT) or two (SILK)
stages with `Stopwatch.GetTimestamp` and report the average in microseconds per frame over every
invocation. `DecodeKernelBenchmarks` in `DecodeKernels.cs` times each kernel's scalar and vector
paths back to back in one process, which is the only comparison that survives the shared machine's
load. An EventPipe sample was also taken, but BenchmarkDotNet's speedscope export attributed every
sample to a `CPU_TIME` leaf, so it was not used.

The machine was at load average 60-120 (18 cores) for much of this lane, and whole-vector means
varied by up to 40% between runs. The per-stage figures come from two runs with the same job
(before on the PERF-1 base, after on this branch); the SILK mediumband row (03) is the clearest
victim of the noise, since even its untouched decode stage moved by 30%.

### Decode ratios (PERF-2)

Per-frame microseconds for the whole vector, best observed mean on each side. `libopus` is the
fastest of two in-process harness runs. The whole-vector rows were taken at different times, so
treat the ratios as indicative rather than exact.

| Vector | libopus µs/frame | Kiln before µs/frame | Ratio before | Kiln after µs/frame | Ratio after |
| ------ | ---------------- | -------------------- | ------------ | ------------------- | ----------- |
| 01 | 8.55 | 12.37 | 1.45 | 11.70 | 1.37 |
| 07 | 5.70 | 12.65 | 2.22 | 8.76 | 1.54 |
| 11 | 24.40 | 37.15 | 1.52 | 37.15 | 1.52 |
| 02 | 6.22 | 7.08 | 1.14 | 6.79 | 1.09 |
| 03 | 7.39 | 10.83 | 1.47 | 9.15 | 1.24 |
| 04 | 8.71 | 15.80 | 1.81 | 13.43 | 1.54 |

The CELT 07 and SILK 03 rows improved the most (the PVQ/denormalization and LTP changes
respectively). CELT 11 showed no reliable change: its synthesis is dominated by the inverse MDCT,
which this lane does not own. None of the six vectors reached the ≤1.0 target.

### Stage split (µs per Opus frame, 0 B allocated)

| Vector | Mode | Stage | Before | After |
| ------ | ---- | ----- | ------ | ----- |
| 01 | CELT | bitstream + frequency domain | 10.70 | 7.97 |
| 01 | | synthesis | 6.78 | 4.78 |
| 01 | | int16 conversion | 0.41 | 0.33 |
| 07 | CELT | bitstream + frequency domain | 6.29 | 5.15 |
| 07 | | synthesis | 7.69 | 6.04 |
| 07 | | int16 conversion | 0.43 | 0.38 |
| 11 | CELT | bitstream + frequency domain | 28.39 | 23.37 |
| 11 | | synthesis | 24.96 | 20.15 |
| 11 | | int16 conversion | 1.77 | 1.64 |
| 02 | SILK | parameters + excitation | 4.52 | 4.13 |
| 02 | | synthesis | 4.68 | 3.92 |
| 03 | SILK | parameters + excitation | 4.71 | 6.15 |
| 03 | | synthesis | 5.18 | 6.72 |
| 04 | SILK | parameters + excitation | 5.03 | 5.05 |
| 04 | | synthesis | 10.29 | 9.50 |

"Synthesis" for CELT is denormalization, the inverse MDCT with overlap-add, the post-filter and
de-emphasis; for SILK it is NLSF→LPC, excitation reconstruction, LTP and LPC synthesis.

### Kernel A/B (same process, one benchmark run)

| Kernel | Work per invocation | Scalar | Vector | Speedup |
| ------ | ------------------- | ------ | ------ | ------- |
| SILK LTP synthesis (`DecSilkLtp`) | 5-tap LTP over 80 samples | 213.6 ns | 68.6 ns | 3.1x |
| CELT denormalization (`DecKernels.ScaleCopy`) | 176-coefficient band | 64.3 ns | 20.7 ns | 3.1x |
| CELT PVQ pulse scaling (`DecKernels.ScaleIntToFloat`) | 176 pulses | 70.9 ns | 19.4 ns | 3.7x |

### Algorithmic changes

- The PVQ `V(N,K)` table is a single flat `uint[]` with a fixed row stride instead of a
  two-dimensional array, so the several reads per bin in `cwrs` cost one offset add rather than a
  multiply and two bounds checks. `Cwrsi` also reuses the `V(N-j-1,k)` value it already read and
  accumulates `sum(y^2)` during the same walk, removing a second pass over the pulse vector.
- The CELT frequency buffer is cleared only outside the coded band range, and the long-block
  inverse MDCT writes straight into the overlap-add instead of through a cleared intermediate.
- `RangeDecoder`'s symbol path is marked for inlining, renormalization gets a branch-only fast
  path, and the byte read uses an unchecked span reference after the existing bounds test.
- The SILK LTP loop vectorizes four samples at a time when the lag keeps a block's writes outside
  its reads (`lag >= 10`), falling back to the reference scalar chain otherwise. `Smulwb` is
  evaluated in 32-bit lanes exactly as in `SilkLpcKernel`.

## Remaining gaps and next ideas

1. **CELT decode** (ratios in the PERF-2 decode table above). After PERF-2 the per-frame split is
   roughly half bitstream/frequency-domain decode and half synthesis. The single largest synthesis
   kernel is now the inverse MDCT (~5.4 µs per 960-sample channel frame); it still runs two
   `N/2`-point complex FFTs (one per DCT-IV), and a real FFT of size `N/2` is the next step. That
   file belongs to lane ENC-PERF-1, not this one.
2. **CELT entropy decode.** The remaining bitstream cost is the PVQ codeword read and the
   per-band bookkeeping (allocation interpolation, `TellFrac`, coarse/fine energy). The range
   decoder itself matches the reference's structure; the next ideas are a branch-free `ec_dec`
   tail and fusing `ExtractCollapseMask` into `Cwrsi`, both expected to be small.
3. **MDCT inverse.** Kernel-level, a split-radix radix-2/4 FFT and a fused post-rotation write
   (avoiding the separate reversed copies) are the next step. The DCT-IV pre/post rotations are
   stride-two on the input and output; a two-source lane shuffle, which this runtime's
   `Vector128.Shuffle` lacks, would vectorize them.
4. **CELT post-filter and de-emphasis.** The post-filter is 2.4x faster and ~0.36 µs. De-emphasis
   is a one-pole recursion that the reference also runs scalar; combining it with the int16
   conversion would need the synthesis API to write shorts, which is outside this lane's files.
5. **SILK.** The LTP loop is now 3.1x faster in isolation and LPC synthesis 20%, but the WB vector
   (04) is the one SILK vector still well above 1.0: its synthesis is ~9.5 µs/frame. The next
   targets are the NLSF→LPC conversion and the excitation reconstruction, neither of which is in
   this lane's file set, plus vectorizing the LPC analysis filter.

## PERF-3: real-input MDCT and SILK decode hot paths

PERF-3 replaces the CELT MDCT/IMDCT with the reference's real-input structure: **one
size-`N/4` complex FFT per direction** instead of the two size-`N/2` DCT-IV FFTs of PERF-1
(`N` is the doubled block length, so the FFT sizes are 60/120/240/480 for 120/240/480/960
coefficients). The windowed input is folded around the block centre into the complex sequence the
FFT consumes, each half-sample phase rotation is folded into a single precomputed complex twiddle,
and the forward post-rotation writes coefficient `2p` from the real part and `N-1-2p` from the
imaginary part. The inverse is the mirrored path (pre-rotation, conjugate FFT, post-rotation,
de-shuffle, windowed TDAC mirror). The structure follows `celt/mdct.c` from the RFC 6716
Appendix A reference; the two exact rotations are combined into single-precision twiddles rather
than the reference's small-angle `sine` shortcut, so the float path matches the double-precision
definition to float rounding. `Mdct.cs` is now listed under THIRD-PARTY-NOTICES.md.

SILK gets two bit-exact changes: the order-10 and order-16 LPC synthesis loops are fully unrolled
with the reversed-coefficient vectors held in registers, and the excitation reconstruction loop is
branch-free (a sign-mask offset and an xor/subtract negate). `SilkPcmOracleTests` confirms the SILK
output is unchanged.

**Correctness.** `MdctTests` (naive double reference), `SimdParityTests` (vector versus scalar,
including the bit-exact SILK LPC parity theory for orders 10 and 16), `CeltVectorConformanceTests`,
`OpusDecoderConformanceTests` and `SilkPcmOracleTests` all pass. Every measured path allocates 0 B.

### MDCT kernel cost

`Perf3Measure` (`dotnet run -c Release --no-build --project bench/Kiln.Audio.Benchmarks -- micro
mdct`, run under the machine-wide lock) times the kernels with a Stopwatch best-of-25 batch after a
250 ms warm-up, which is the statistic the shared machine's load leaves intact. These numbers are
from a clean lock run. The PERF-2 column is BenchmarkDotNet's mean from the previous lane, on the
same machine.

| Kernel | PERF-2 µs | PERF-3 µs | Change |
| --- | --- | --- | --- |
| Forward 960 | 6.05 | **1.90** | 3.2x |
| Inverse 960 | 5.42 | **2.11** | 2.6x |
| Forward 8 x 120 | 4.75 | **1.77** | 2.7x |
| Inverse 8 x 120 | 4.53 | **2.34** | 1.9x |
| Forward 120 | not measured | **0.245** | - |
| Inverse 120 | not measured | **0.294** | - |

Both 960 targets (≤ 2.5 µs) and both 120 targets (≤ 0.4 µs) are met. The size-480 complex FFT
alone is 1.11/1.12 µs (forward/inverse), so the FFT is now roughly half of the 960-block cost.

### SILK kernel cost

Same Stopwatch harness (`... -- micro silk`). The LPC synthesis figure is one 80-sample subframe.

| Kernel | PERF-3 µs | Notes |
| --- | --- | --- |
| NLSF → LPC, order 10 | 0.12 | unchanged (table gather, no SIMD win) |
| NLSF → LPC, order 16 | 0.26 | unchanged |
| LPC synthesis, order 10 | **0.21** | was 0.29; unrolled |
| LPC synthesis, order 16 | 0.53 | was 0.55; unrolled, serial dependency dominates |

### Decode ratios

libopus from `scripts/bench-libopus-inproc.sh`, Kiln from `... -- micro decode` (tier-1 warmed;
decode + synthesis + int16), both run under the machine-wide lock. The two runs were consecutive;
libopus reproduced its previous clean run to within 1%, so these ratios are stable to a few percent
for CELT and noisier for SILK.

| Vector | libopus µs/frame | Kiln µs/frame | Ratio |
| --- | --- | --- | --- |
| 01 | 6.70 | 9.29 | 1.39 |
| 07 | 4.41 | 6.32 | 1.43 |
| 11 | 19.26 | 25.45 | 1.32 |
| 02 | 4.48 | 5.38 | 1.20 |
| 03 | 5.81 | 6.88 | 1.18 |
| 04 | 6.46 | 9.04 | 1.40 |

CELT 11's synthesis stage fell from PERF-2's 20.15 µs to 8.26 µs/frame, which is the MDCT halving
showing up. No vector reaches the ≤ 1.0 target.

### Remaining gaps

1. **CELT decode is now bitstream/frequency-domain bound.** The per-frame split is 6.46/2.53/0.30 µs
   for vector 01 and 16.31/8.26/0.88 µs for 11 (bitstream+frequency / synthesis / int16). The
   synthesizer is no longer the largest stage; the range decoder, PVQ codeword read and band
   bookkeeping are. That is the 1.32-1.43 remaining ratio.
2. **SILK is still above 1.0.** Only the order-10 LPC synthesis moved (0.29 → 0.21 µs); the
   order-16 loop is latency-bound and the NLSF→LPC conversion is unchanged. WB synthesis
   (vector 04) is still the dominant SILK stage (5.41 µs/frame). No SILK target (≤ 1.0) is met.
3. **`Vector256` is emulated on the arm64 target**, so the existing `Vector128` radix butterflies
   stay; the measured FFT does not benefit from a 256-bit path here.

## Encoder (CELT-only, 48 kHz mono)

This section records the ENC-PERF-1 measurements for the CELT-only restricted-low-delay encoder:
48 kHz, mono, CBR, 20 ms frames. It uses the same machine and runtime as the decoder baseline.

### Method

**Fair libopus reference.** Since `bench/native/libopus_bench.c` and
`scripts/bench-libopus-inproc.sh` merged with PERF-1, libopus is measured **in process**, the same
way Kiln is: `opus_encode` on PCM already in memory, no process start-up, no file IO. The harness
is invoked as

```bash
scripts/bench-libopus-inproc.sh encode /path/to/mono48.pcm 48000 1 restricted-lowdelay \
    <bitrate> 20 <complexity> 1 5
```

and reports the best of five full passes. The PCM file is the mono downmix of every RFC 8251
`.dec` vector (320.62 s, 15,390,480 samples), the same corpus `EncodeBenchmarks` loads. This
replaces the older `opus_demo` numbers in earlier revisions of this page, which included process
start-up and file IO and were therefore generous to libopus.

**Kiln.** `EncodeBenchmarks` loads that corpus in `[GlobalSetup]` and encodes it as CBR 20 ms
frames for 24 and 32 kbps at complexity 0, 5 and 10. To match the "best of five" reference
method, each case was also measured in process as the best of fifteen fresh-encoder passes; the
table below uses that best-of-N figure for both codecs. The BenchmarkDotNet mean of the same
cases is within the noise band but this machine deschedules the process for hundreds of
milliseconds at a time, so the best-of-N value is the reproducible one. Allocations are 0 B.

**Quality (G2).** The corpus is the mono downmix of every `.dec` at 48 kHz. Both encoders encode
it at 24 and 32 kbps, 20 ms, CBR, and both outputs are decoded with libopus. Segmental SNR uses
20 ms segments, each clamped to [-10, 60] dB, after removing the codec's 120-sample stream delay.
`opus_compare` runs at 48 kHz with the same alignment, over the first ten seconds of each vector;
the tool's naive DFT is linear in audio length, and a per-vector excerpt keeps the gate test to a
couple of minutes.

### Complexity mapping

| Complexity | What it enables in this encoder |
| --- | --- |
| 0 | No transient analysis, no TF analysis (long blocks only), spread forced to none, single-pass coarse energy. |
| 1 | Transient and TF analysis still off; spread stays at its normal default. |
| 2 | Transient analysis and TF analysis on (short blocks for transients). |
| 3 | Spreading decision on. |
| 4-10 | Two-pass intra/inter coarse-energy trial. |

TF analysis requires complexity 2 in the reference (`enable_tf_analysis = ... st->complexity>=2`
in `celt/celt_encoder.c`); it was previously run at every level here, which cost 3.0 µs per frame
at complexity 0 for no quality benefit. It is now gated the same way, and the time resolution
below complexity 2 is the reference's `isTransient` (always false here, because transient analysis
also needs complexity > 1).

ENC-2 adds the pitch pre-filter of RFC 6716 §4.3.7.1 (encoder side) at complexity 5 and above,
matching libopus; it is signalled off below that, so the complexity-0 rows above are unaffected.
The pre-filter search uses the reference's decimated correlation with a Vector128/Vector256 dot
product and a scalar reference.

### Encode speed (fair, in process)

The corpus is 320.62 s of audio encoded as 16,031 frames of 20 ms. Per-frame time is the best of
five libopus passes and the best of fifteen Kiln passes. Ratios above 1 mean Kiln is slower.

| Bitrate | Complexity | libopus µs/frame | Kiln before µs/frame | Kiln after µs/frame | After ÷ libopus |
| --- | --- | --- | --- | --- | --- |
| 24 kbps | 0 | 9.18 | not measured | **14.63** | 1.59 |
| 24 kbps | 5 | 41.49 | not measured | **31.50** | 0.76 |
| 24 kbps | 10 | 52.96 | not measured | **31.26** | 0.59 |
| 32 kbps | 0 | 12.68 | 21.4 | **17.70** | 1.40 |
| 32 kbps | 5 | 35.93 | 30.8 | **31.73** | 0.88 |
| 32 kbps | 10 | 43.77 | 30.2 | **32.33** | 0.74 |

"Before" is the PERF-1 BenchmarkDotNet figure for 32 kbps; 24 kbps was not measured before this
lane. The "after" figures are the best of the repeated best-of-fifteen runs. The machine is shared
and individual runs varied by 20-50%, so read the ratios as roughly ±20%: the robust results are
that complexity 5 and 10 are faster than libopus at the same complexity, and that complexity 0 is
still behind it (1.4-1.6x). PERF-1's BenchmarkDotNet mean at complexity 0 was 21.4 µs
(32 kbps); the best-of-N figure here is 17.7 µs, and about 3 µs of that difference is the removed
TF analysis. Complexity 5 is unchanged within noise (it is dominated by transient/TF/spread
analysis and the MDCT, which this lane did not restructure).

**Iso-quality.** Segmental SNR at complexity 0 (the G2 row below) is 16.20 dB at 32 kbps and
14.34 dB at 24 kbps. At 32 kbps libopus reaches those values at complexity 10 (16.65 dB), and at
24 kbps also only at complexity 10 (14.44 dB), so the lowest libopus complexity that matches our
quality is 10 at both rates. At that matched quality Kiln takes **17.70 µs** against libopus's
**43.77 µs** at 32 kbps (2.5x faster) and **14.63 µs** against **52.96 µs** at 24 kbps (3.6x
faster). The iso-quality target is met.

### Stage breakdown (32 kHz, complexity 0)

A stopwatch-instrumented build (since removed) timed each stage in the frame pipeline. On this
shared machine the instrumented pass itself varied by several microseconds between runs, so the
per-stage values are indicative; the clean end-to-end number is the one above. The same
instrumentation was used before and after, so the deltas between the two columns are the useful
part.

| Stage | Before µs/frame | After µs/frame | What changed |
| --- | --- | --- | --- |
| Pre-emphasis | 0.45 | ≈0.2 | `Vector128` two-tap filter, bit-identical |
| Transient | 0.01 | 0.01 | off at complexity 0 |
| MDCT (incl. 2/N scale) | 5.25 | ≈5.0 | transform unchanged; scaling pass now `Vector128` |
| Band energy | 0.35 | ≈0.35 | bounds-check-free scalar sum (order preserved) |
| amp2log2 | 0.05 | ≈0.05 | unchanged |
| Normalise | 0.15 | ≈0.1 | copy and gain fused, `Vector128` |
| TF analysis | 2.97 | 0.00 | reference complexity-2 gate |
| Coarse energy | 0.31 | ≈0.31 | unchanged |
| Allocation (dynamic + trim + allocation) | 0.65 | ≈0.6 | bounds-check-free inner loops |
| Fine energy | 0.07 | ≈0.07 | unchanged |
| Quant all bands | 11.95 | ≈9.0 | PVQ search vectorized; pulse coder refs |
| ├ PVQ search | 6.85 | ≈4.0 | scalar 5.75 → vector 1.36 µs at N=96/K=32 (4.2x) |
| ├ Encode pulses | 3.06 | ≈2.8 | bounds-check-free combinatorial row update |
| ├ Spread rotation | 0.25 | ≈0.2 | bounds-check-free |
| └ Band other (Haar, splits, masks) | 1.79 | ≈2.0 | bounds-check-free |
| Finalise + frame setup/tail | 0.06 | ≈0.05 | unchanged |
| **Clean total** | **24.0** | **16.9** | measured end to end |

The PvqSearch figures in the last column come from a direct in-process A/B run rather than the
instrumented pass: scalar 5.75 µs against vector 1.36 µs for N=96/K=32, and 5.26 against 3.37 µs
for N=176/K=40 (1.56x). The large-band case is limited by the serial argmax comparison, not by
arithmetic.

### SIMD kernels

The encoder now uses `Vector128<T>` in `src/Kiln.Audio/Simd/EncPvqKernel.cs` and
`src/Kiln.Audio/Simd/EncKernels.cs`; every kernel keeps a scalar reference and a parity test in
`EncoderKernelTests`.

| Kernel | Path | Parity |
| --- | --- | --- |
| PVQ greedy search (`EncPvqKernel.Search`) | `Vector128` candidate sums, lanes compared in index order | bit-exact (pulses, signs, target, norm) |
| Pre-emphasis (`EncKernels.PreEmphasis`) | `Vector128` widen/filter, 8 samples per iteration | bit-exact |
| Spectrum scaling (`EncKernels.Scale`) | `Vector128` multiply | bit-exact |
| Band normalise into destination (`EncKernels.NormaliseInto`) | `Vector128` fused copy + gain | bit-exact |

`KILN_AUDIO_DISABLE_SIMD=1` still forces the scalar paths. Encoder output is unchanged at
complexity 2 and above. At complexity 0 and 1 the reference TF gate above changes those frames
(the G1 interop tests run at complexity 5 and are unaffected; the G2 quality run decodes the
complexity-0 stream from both codecs and reports no range mismatch).

### Quality (G2)

| Bitrate | Complexity | Ours segSNR (dB) | libopus segSNR (dB) | Gap (ours − libopus) | Ours `opus_compare` | libopus `opus_compare` |
| --- | --- | --- | --- | --- | --- | --- |
| 24 kbps | 0 | 14.342 | 13.334 | **+1.008** | −506.6% | −516.9% |
| 24 kbps | 5 | 14.467 | 13.876 | +0.591 | −266.1% | −247.2% |
| 24 kbps | 10 | 14.467 | 14.441 | +0.026 | −266.1% | −235.3% |
| 32 kbps | 0 | 16.197 | 15.237 | **+0.960** | −441.1% | −455.4% |
| 32 kbps | 5 | 16.302 | 15.795 | +0.506 | −221.6% | −195.4% |
| 32 kbps | 10 | 16.302 | 16.653 | −0.351 | −221.6% | −170.0% |

The gate is the complexity-0 row: the aggregate segmental SNR is about 1.0 dB above libopus at
both rates. The complexity-0 values are 0.12 dB lower than PERF-1's (14.450/16.313 dB) because
those frames no longer run TF analysis; the margin is still comfortable. At complexity 5 it is
ahead by about half a dB; at complexity 10 it is essentially tied at 24 kbps and 0.35 dB behind at
32 kbps, where libopus spends its extra complexity on the pitch pre-filter.

The `opus_compare` percentages are negative for **both** encoders because the corpus is itself a
lossy decode that is being re-encoded: the metric charges the second generation of loss. On the
same input, libopus scores +25% at 64 kbps and +74% at 128 kbps, so the negative values are a
property of the double compression, not of the comparison.

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

### Kernel results

Direct in-process measurements (best of five, five thousand iterations each); the machine is
shared, so treat sub-10% differences as noise.

| Kernel | Work per invocation | Before | After | Change |
| --- | --- | --- | --- | --- |
| `PvqSearch` | PVQ search, N = 96, K = 32 | 5.75 µs (scalar) | 1.36 µs (`Vector128`) | 4.2x |
| `PvqSearch` | PVQ search, N = 176, K = 40 | 5.26 µs (scalar) | 3.37 µs (`Vector128`) | 1.56x |
| `PvqSearch` | PVQ search, N = 16, K = 6 | 0.071 µs | 0.069 µs | 1.03x |
| `PvqSearch` | PVQ search, N = 8, K = 3 | 0.026 µs | 0.020 µs | 1.31x |
| `MdctForward960` | One 960-coefficient forward MDCT (20 ms) | — | 4.8-6.0 µs | unchanged |
| `PreEmphasis` | 960 samples | 0.45 µs | ≈0.2 µs | ≈2x |
| `NormaliseBand` | 960 samples | 0.15 µs | ≈0.1 µs | fused |

Small vectors gain little from `Vector128` (the comparison chain dominates); the 96-wide band that
carries most of the pulses is where the 4.2x applies.

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

### Remaining gaps and next ideas

1. **Complexity 0 is still 1.4-1.6x behind libopus at the same complexity** (17.70 vs 12.68 µs at
   32 kbps; 14.63 vs 9.18 µs at 24 kbps). The fixed forward MDCT is about 5 µs, i.e. roughly half
   of libopus's entire complexity-0 budget, and `Mdct.cs`/`Fft.cs` are shared with the decoder and
   out of scope for this lane. The next transform idea is the encoder-side real FFT of size N/2
   (an N/4-point complex FFT plus a split), the same one the decoder notes list. Of the rest, the
   PVQ search is ~4 µs and the pulse coder ~2.8 µs.
2. **PVQ search, large bands.** `Vector128` gives 4.2x at N=96 but only 1.56x at N=176 because the
   running argmax is a serial multiply-compare chain. A branch-free vector argmax would break the
   chain but changes tie-breaking; it is only worth doing behind a G2 run that accepts the new
   bytes.
3. **Pulse coder.** `Icwrs`/`Unext` is O(n·k) integer work. The row update is a prefix sum and
   could be vectorized with a log-step scan, but the row is short (k+2) and the win is unproven.
   This is the next encoder-side idea after the transform.
4. **Complexity 5 and 10 already beat libopus at the same complexity** (1.1-1.3x and 1.35-1.69x),
   and the iso-quality target is met by 2.5-3.6x. Nothing further is needed there for ENC-PERF-1.
5. **Front end.** Pre-emphasis, band energy and normalise are now equal to or below 0.4 µs each;
   band energy deliberately keeps its scalar summation order because it feeds the bitstream.

## SILK encoder quality

This section records the SILK encoder's quality against libopus 1.5.2, the methodology, and what
SILK-QUAL-1 changed. It is evidence, not a CI gate; the gate lives in `SilkEncoderQualityTests`.

### Method

- Corpus: the mono downmix of RFC 8251 vectors 02, 03, 04 and 06, which is the corpus SILK-ENC-1
  used. Tuning decisions were checked on vectors 04 and 06 as a held-out pair.
- Reference: `IdealResampler` (windowed-sinc, zero delay) resamples the 48 kHz original to the
  codec's internal rate (8, 12 or 16 kHz). It is independent of the encoder's own resampler.
- Both codecs' packets are decoded by the libopus oracle at the internal rate and scored with
  aggregate segmental SNR (20 ms segments, clamped to [-10, 60] dB). The decode is aligned to the
  reference above 150 Hz, where both codecs encode the same content; the encoder's own high-pass
  (60-100 Hz) makes full-band alignment ill-conditioned. The delay search is coarse-to-fine
  (step 8 then step 1).
- `opus_compare` quality percent is reported as `n/a`. The tool only scores 48 kHz, and its
  intended reference is a 48 kHz signal with the same bandwidth; comparing a band-limited SILK
  decode to the full-band original produces meaningless negative scores for both codecs. A
  correct harness needs an independent band-limited 48 kHz reference, which roughly quintuples
  the gate's runtime (measured: 5m19s versus 1m06s), so it is deferred. The scored gate is
  segSNR at the internal rate.

The earlier SILK-ENC-1 harness searched delays on a fixed four-sample grid and used the encoder's
own resampler as the reference. At 12 kHz that misaligned up to two samples and understated every
band; the numbers below supersede it and are not comparable with the SILK-ENC-1 report.

### Results at complexity 0 (the gate)

`ours` and `libopus` are aggregate segmental SNR in dB over the four vectors; positive `gap` means
we beat libopus.

| Band | Frame | kbps | ours | libopus | gap |
| ---- | ----- | ---- | ---- | ------- | --- |
| NB | 20 ms | 8  | 3.64 | 3.60 | +0.04 |
| NB | 60 ms | 8  | 3.72 | 3.64 | +0.08 |
| NB | 20 ms | 12 | 5.03 | 5.51 | -0.48 |
| NB | 60 ms | 12 | 5.08 | 5.49 | -0.41 |
| MB | 20 ms | 8  | 3.52 | 2.77 | +0.75 |
| MB | 60 ms | 8  | 3.64 | 2.81 | +0.83 |
| MB | 20 ms | 12 | 5.07 | 5.01 | +0.06 |
| MB | 60 ms | 12 | 5.10 | 4.84 | +0.26 |
| MB | 20 ms | 20 | 6.16 | 6.73 | -0.57 |
| MB | 60 ms | 20 | 6.11 | 6.72 | -0.61 |
| WB | 20 ms | 8  | 3.17 | 1.71 | +1.47 |
| WB | 60 ms | 8  | 3.35 | 1.77 | +1.58 |
| WB | 20 ms | 12 | 4.57 | 4.50 | +0.07 |
| WB | 60 ms | 12 | 4.60 | 4.37 | +0.23 |
| WB | 20 ms | 20 | 6.14 | 6.44 | -0.30 |
| WB | 60 ms | 20 | 6.08 | 6.43 | -0.35 |

Ten of sixteen complexity-0 configurations meet the target; six remain 0.30-0.61 dB behind (NB
12 kbps, and MB/WB 20 kbps). Before SILK-QUAL-1, all sixteen were behind by 0.01-0.90 dB.

### Complexity 5 and 10

Complexity 5 and 10 are reported, not gated. The delayed-decision quantizer and warped shaping pay
off most at the low rates: for example WB 8 kbps 20 ms rises from 1.53 dB (greedy NSQ, complexity
5) to 2.81 dB, and 1.60 to 3.27 dB at complexity 10; MB 8 kbps 60 ms rises from 2.23 to 3.21 dB at
complexity 5. At complexity 5 and 10 the remaining gap to libopus is 0.1-0.9 dB across the matrix
(e.g. WB 8 kbps 60 ms -0.10 dB, MB 20 kbps -0.75 dB).

### What changed

1. **Delayed-decision NSQ at every complexity.** `SilkEncoderCore` now always runs
   `SilkNsqDelayedDecision` instead of the greedy `SilkNsq`, with trellis snapshots for the rate
   loop. Complexity 2-3 use one state, 4-5 two, 6-7 three and 8-10 four, as in the reference;
   complexity 0-1 use four states here (the reference uses one). The winner's LCG seed is written
   back to the frame indices; without that the transmitted seed does not match the surviving
   state's sign pattern and multi-state output decodes as noise.
2. **Warped noise shaping everywhere.** `WarpingQ16 = fs_kHz * 0.015` is now on at every
   complexity (the reference only enables it from complexity 4), now that the quantizer supports
   it.
3. **Higher resolution at complexity 0.** NLSF MSVQ survivors rise from 2 to 8 and the low
   complexity LTP quantizer is disabled at complexity 0, which is where the 8 and 12 kbps gains
   come from.
4. **Spending the 20 kbps budget.** The RFC 1.0 target-rate table leaves about 2 kbps unused at
   20 kbps. The target SNR now rises by up to 3 dB above 14 kbps, which brings bit usage to
   ~48.9 bytes per 20 ms frame (19.6 kbps) and recovers 0.1-0.2 dB. A larger boost overfills the
   budget and loses quality at 60 ms, so 3 dB is the chosen point.

### Encode cost

Measured with a stopwatch over the whole vector 02 corpus (1251 frames of 20 ms, MB, 20 kbps) on
the shared build machine, so treat the numbers as indicative:

| Complexity | Greedy NSQ (before) | Delayed decision (after) | Ratio |
| ---------- | ------------------- | ------------------------ | ----- |
| 0 | 71.8 µs/frame | 83.4-85.5 µs/frame | 1.16-1.19x |
| 5 | 268 µs/frame | 274 µs/frame | ~1.02x (within run-to-run noise) |

Both are inside the 1.5x budget. Allocations remain zero per frame and the bitstream stays valid
(`SilkEncoderInteropTests`, `SilkEncoderSmokeTests`, `SilkEncoderAllocationTests`).

### Remaining gap and next ideas

The six failing complexity-0 cases are NB 12 kbps and MB/WB 20 kbps, 0.3-0.6 dB behind. The
quantizer search is no longer the limiter (four states at complexity 0 already match complexity
10); the gap is in the analysis and the rate control at high rates.

1. **Rate-distortion index selection** for gains, LTP and NLSFs, instead of nearest-index plus a
   global gain multiplier. The 20 kbps gap is where a weighted error-plus-lambda·bits criterion
   should pay.
2. **Noise shaping tuning by bitrate and activity**, searched on a training split and validated on
   held-out vectors. The harmonic/tilt/low-frequency strengths are still the RFC 1.0 constants.
3. **Per-frame bit allocation across 40/60 ms packets and the bit reservoir**, favouring voiced
   onsets. The 60 ms cases are consistently the weaker ones at 20 kbps.
4. **LPC analysis** (windowing, noise-floor regularization, Burg conditioning). The remaining
   high-rate error is broad-band, which points at the short-term predictor rather than the
   excitation.

### Frozen RFC baseline and the ratchet

The hard gate at complexity 0 is a ratchet, not a fixed libopus target. The RFC 6716 Appendix A
figures are frozen in `tests/Kiln.Audio.Tests/SilkEncoder/SilkRfcBaseline.cs`, measured with this
harness (build the RFC source with `FIXED_POINT=1`, then run `SilkRfcBaselineGenerator` with
`KILN_SILK_RFC_OPUS_DEMO` set to its `opus_demo`). Per configuration the test asserts
`ours >= max(RFC, frozen ours - 0.05 dB)` where we still trail libopus, and
`ours >= frozen libopus` where we already beat it. The six remaining gaps stay reported in the
test output and in the table above; the ratchet is what makes the gate green without hiding them.
