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

## PERF-4: CELT bitstream decode and SILK WB synthesis

PERF-4 profiles the CELT frequency-domain decoder at stage granularity, then attacks the stages it
measured as dominant. The changes are bit-exact where the path is integer (final range, PVQ
indices, SILK output) and stay far inside the CELT float tolerance everywhere else.

**Profiling method.** `Perf4Profile` (in `CeltDecoder.cs`) and `SilkPerf4Profile` (in
`SilkSynthesis.cs`) are compiled in only when `KILN_PERF4_PROFILE` is defined; in a normal build
`Enabled` is the constant `false` and every timing block folds away, so the shipped code carries
no instrumentation. Build and run with

```bash
dotnet build -c Release -p:DefineConstants=KILN_PERF4_PROFILE
dotnet bench/Kiln.Audio.Benchmarks/bin/Release/net10.0/Kiln.Audio.Benchmarks.dll micro perf4
```

`Perf4ProfileMeasure` (`bench/Kiln.Audio.Benchmarks/Perf4Profile.cs`) runs the three CELT and
three SILK comparison vectors and prints microseconds per frame per stage. The timers overlap: a
stage that contains another (the partition recursion contains PVQ and the angle decode) is
reported once per nesting level, so its column overcounts. The top-level columns are the reliable
comparison; the leaf columns show shares.

### Stage profile (instrumented, µs per frame)

The instrumentation itself costs roughly 1-3 µs/frame at these call counts, so the table is for
attribution, not for absolute totals.

| Stage | 01 | 07 | 11 |
| --- | --- | --- | --- |
| flags (silence/post-filter/transient/intra) | 0.019 | 0.023 | 0.043 |
| coarse energy (Laplace) | 0.509 | 0.327 | 0.403 |
| time/frequency | 0.078 | 0.068 | 0.082 |
| dynamic allocation | 0.082 | 0.070 | 0.093 |
| allocation (of which interpolation) | 0.539 (0.266) | 0.477 (0.252) | 0.554 (0.277) |
| fine energy | 0.098 | 0.048 | 0.108 |
| **PVQ band shapes (top level)** | **6.581** | **3.146** | **19.128** |
| ├ pulse vector decode (`AlgUnquant`) | 4.168 | 1.644 | 12.465 |
| ├ spread rotation | 1.297 | 0.377 | 4.904 |
| ├ angle (`ComputeTheta`) | 0.531 | 0.219 | 1.610 |
| └ renormalise / anti-collapse | 0.039 | 0.096 | 0.150 |
| synthesis: denormalise | 0.189 | 0.173 | 0.268 |
| synthesis: inverse MDCT | 1.621 | 1.758 | 5.191 |
| synthesis: post-filter | 0.213 | 0.254 | 0.767 |
| synthesis: de-emphasis | 0.621 | 0.640 | 2.294 |
| **synthesis total** | **2.737** | **2.918** | **8.611** |

CELT decode is PVQ-bound: the pulse-vector unrank, its spread rotation and the partition
bookkeeping around them account for most of the frequency-domain time on every vector. Allocation
— the stage PERF-2/3 expected to dominate — is under 0.6 µs/frame. On the synthesis side the
inverse MDCT (owned by another lane) and the serial de-emphasis recursion are the floor.

SILK decode is excitation-bitstream-bound, and SILK synthesis splits between LPC, the excitation
reconstruction, NLSF→LPC and LTP rewhitening:

| Stage | 02 | 03 | 04 |
| --- | --- | --- | --- |
| decode: excitation (pulses, LSBs, signs) | 2.813 | 3.326 | 3.281 |
| decode: NLSF | 0.220 | 0.206 | 0.279 |
| decode: gains / LTP / other | 0.386 | 0.367 | 0.373 |
| **synthesis: LPC** | 0.573 | 0.937 | 1.261 |
| synthesis: excitation reconstruction | 0.457 | 0.722 | 0.939 |
| synthesis: NLSF→LPC | 0.396 | 0.401 | 0.742 |
| synthesis: LTP (rewhitening + filter) | 0.375 | 0.481 | 0.685 |
| synthesis: stereo unmix | 0.178 | 0.269 | 0.421 |

### Decode ratios (same-window)

`... micro decode` (tier-1 warmed) and `scripts/bench-libopus-inproc.sh`, built and run back to
back under the machine-wide lock. Kiln total is decode + synthesis + int16.

| Vector | libopus µs/frame | Kiln µs/frame | Ratio | PERF-3 ratio |
| --- | --- | --- | --- | --- |
| 01 | 6.734 | 9.22 | 1.37 | 1.39 |
| 07 | 4.444 | 6.22 | 1.40 | 1.43 |
| 11 | 19.423 | 25.58 | 1.32 | 1.32 |
| 02 | 4.651 | 5.29 | 1.14 | 1.20 |
| 03 | 5.854 | 6.71 | 1.15 | 1.18 |
| 04 | 6.372 | 7.25 | 1.14 | 1.40 |

SILK moves substantially: the division-free symbol decode and the dependency-broken order-16 LPC
loop cut vector 04 from 1.40 to 1.14 and the NB/MB vectors to 1.14-1.15. CELT is unchanged within
noise because its time is in PVQ work that this lane only trimmed at the edges. **No vector meets
the ≤ 1.0 target.**

### Changes

- **Division-free SILK symbol decode.** `RangeDecoder.DecodePdf` decodes a 256-total probability
  table by inverting the search condition: instead of computing `fs = 256 - (val/(rng/256) + 1)`
  and scanning for `fs < fh[k]`, it compares `val` against `(256 - fh[k]) * (rng/256)`. That is
  the same symbol and the same state update (bit-exact), but a multiply per candidate replaces
  one hardware divide per symbol. Every SILK PDF symbol — pulse counts, locations, LSBs, signs,
  gains, NLSF, LTP — now takes this path; the excitation decode alone is thousands of symbols per
  frame.
- **Allocation table.** `c * width * BandAllocation[q][band] << lm >> 2` depends only on
  `(lm, channels, q, band)`, so a 7 KiB `AllocScaled` table replaces the multiply/shift pair in
  the bisection and the final pair of vectors. The per-band balance divisor is dispatched to
  constant divisions instead of a variable divide.
- **PVQ.** `Cwrsi` accumulates the collapse mask during the unranking walk (a per-block counter
  and bit shift) instead of a second pass over the pulse vector, and halves `V(N-1,K)+V(N,K)` with
  a shift.
- **Spread rotation.** `cos(a)` and `cos(pi/2 - a)` are the cosine and sine of one angle, so a
  single `Math.SinCos` replaces two `Math.Cos` calls. The argument rounding differs in the last
  bit, which is far inside the CELT tolerance.
- **SILK order-16 LPC synthesis.** The synthesis is latency-bound because each output needs the
  previous one. Only the `c[0]` tap reads the newest output; the other fifteen taps read history
  at least two samples old. They are summed in `Vector128<int>` lanes and reduced once per sample,
  then the single `c[0]` `Smulwb` is added, so an out-of-order core can overlap the dot product
  with the feedback chain. The order-10 and generic paths reduce their lanes once per sample too.
  Integer addition is associative modulo 2^32, so the output is bit-exact.
- **NLSF selector.** The stage-2 PDF selector strings are converted once to 0-based PDF indices,
  removing a string index and a character load per NLSF coefficient.

Static tables are built once at type initialization; the decode and synthesis paths still allocate
0 B per frame.

### Remaining gaps

1. **CELT PVQ is the wall.** `AlgUnquant`/`Cwrsi` plus the spread rotation are three quarters or
   more of CELT frequency-domain decode on all three vectors (the leaf timers overcount by one
   nesting level, but the top-level `pvq-bands` share is 76-94 %). The unrank is a serial index decomposition with
   two table reads and a monotone `k` walk per coefficient; the rotation for stride 1 is a strict
   recurrence (each sample reads the one just written) and cannot be vectorized without changing
   the float result. Closing the remaining 1.32-1.40 needs a different codeword-read strategy, not
   another micro-optimization.
2. **CELT synthesis is at the MDCT/de-emphasis floor.** The inverse MDCT is 1.6-5.2 µs/frame
   (`Mdct.cs`, another lane) and de-emphasis 0.6-2.3 µs/frame; de-emphasis is a one-pole recursion
   the reference also runs scalar. The post-filter is already a vectorized kernel.
3. **SILK still misses 1.0 by ~0.9 µs/frame on WB (04).** Synthesis is now 3.62 µs/frame (was
   5.41 in PERF-3). The remaining candidates are vectorizing the LTP rewhitening FIR across output
   samples (it is independent per sample and bit-exact integer), and the serial excitation LCG.
   Decode is dominated by the excitation symbol stream, which has no obvious redundancy left.
4. **Zero allocations hold**, and the profiler is compiled out of Release builds.

## PERF-5: CELT PVQ, rotation and de-emphasis hot paths

Same-window measurement (`micro decode` and `scripts/bench-libopus-inproc.sh`, one lock window per
change set). Ratio is Kiln (decode + synthesis + int16) over libopus 1.5.2 in-process.

| Vector | Before | PVQ only | PVQ + rotation + de-emphasis |
| --- | --- | --- | --- |
| 01 | 1.40 | 1.37 | 1.28 |
| 07 | 1.44 | 1.42 | 1.31 |
| 11 | 1.33 | 1.31 | 1.18 |
| 02 / 03 / 04 (SILK) | 1.15 / 1.16 / 1.15 | 1.16 / 1.14 / 1.14 | 1.15 / 1.13 / 1.14 |

Per-stage (µs/frame, vector 01 / 07 / 11): decode 6.55 / 3.44 / 16.42 to 6.06 / 3.24 / 14.99;
synthesis 2.61 / 2.69 / 8.33 to 2.16 / 2.24 / 6.68.

- **CWRS unrank** (`Cwrsi`): a `(remaining, k)` table of the prefix sum `S` and `V(N-1,K)` turns a
  zero pulse into one unsigned compare, stops at `k == 0` (rest of the vector is zero), and takes
  the last dimension as +-k. Energy is accumulated in int32. Index-exact; about 0.1-0.2 us/frame.
- **Spread rotation** (`ExpRotation1`): stride 1 keeps the recurrence value in a register;
  stride >= 4 runs four independent iterations per `Vector128` step. Same operation order, so
  bit-identical to the scalar loop.
- **De-emphasis**: four samples per step from precomputed powers of the coefficient, so the
  loop-carried chain is one multiply-add per four samples. Differs from the serial loop only by
  float reassociation. Output indexing is fused with the interleave.

Not done: closed-form small-N unranking, vectorized inverse MDCT changes, fusing int16 conversion.
Remaining gap is mostly the PVQ band-decode path (about 6 us/frame on 01) and the inverse MDCT.

## PERF-6: CELT quant_bands/renorm/IMDCT hot paths

Same-window measurement (`micro decode` and `scripts/bench-libopus-inproc.sh`, one lock window each
side). Ratio is Kiln (decode + synthesis + int16) over libopus 1.5.2 in-process.

| Vector | PERF-5 base (this window) | PERF-6 |
| --- | --- | --- |
| 01 | 1.27 | 1.18 |
| 07 | 1.31 | 1.16 |
| 11 | 1.19 | 1.08 |
| 02 / 03 / 04 (SILK) | 1.16 / 1.13 / 1.14 | 1.16 / 1.13 / 1.13 |

Per-stage (us/frame, vector 01 / 07 / 11): decode 6.15 / 3.28 / 15.16 to 6.04 / 3.23 / 15.43
(unchanged); synthesis 2.15 / 2.24 / 6.69 to 1.75 / 1.80 / 5.20; int16 0.31 / 0.25 / 0.90 to
0.05 / 0.05 / 0.18.

- **Bulk int16 conversion** (`CeltPcm.ToInt16(span, span)`): Vector128 scale, clamp, add 0.5,
  floor, convert and narrow; bit-identical to the scalar overload. Used by `OpusDecoder` and the
  benchmarks.
- **Inverse MDCT**: the stride-1 pre-rotation deinterleaves four coefficient pairs per step, the
  de-shuffle is a vector interleave, and the mirror's middle copies are vector copies. The `2/N`
  scale and the synthesizer's `N/2` cancel, so `InverseUnscaled` skips both multiplies.
- **Synthesis**: the overlap-add writes straight into the rolling history (no `_rawOutput` pass);
  short-block accumulation is vectorized.
- `MathF.Sqrt` replaces `(float)Math.Sqrt` on float values in PVQ gain (identical result).

Not done: closed-form N=2/3/4 unranking, explicit-stack theta recursion, fused de-emphasis with
int16. PVQ decode (about 6 us/frame on 01) is unchanged and is the remaining gap: roughly 1.2 us
of the 1.18 ratio on 01 is decode. Target of 1.0 is not met on any vector.

## PERF-7: CELT PVQ/quant_bands decode hot paths

Same-window measurement (`micro decode` and `scripts/bench-libopus-inproc.sh`, one lock window per
side). Ratio is Kiln (decode + synthesis + int16) over libopus 1.5.2 in-process. Gates: 375 tests
pass, 0 warnings, 0 errors, final range exact, 0 B allocated.

| Vector | Before | PERF-7 |
| --- | --- | --- |
| 01 | 1.19 (decode 6.12) | 1.14 (decode 5.78) |
| 07 | 1.18 (decode 3.28) | 1.12 (decode 3.05) |
| 11 | 1.04 (decode 14.84) | 1.03 (decode 14.58) |

**Profile** (`micro perf4`, instrumented, us/frame; the timers cost about 20 ns per call, so
nested stages overcount). The profiler gained `noisefill`, `quantband` (whole `QuantBand`) and
`lowband-out` slots. Vector 01 before: pvq-bands 6.63, quantband 5.64, pulses 3.52 (32.6 leaves per
frame, of which rotation 0.96), theta 0.48 (17.3 per frame), noise fill 0.16, lowband copy 0.19,
coarse energy 0.48, allocation 0.51, fine energy 0.09, TF 0.07, denormalise 0.18, anti-collapse
about 0. After: pvq-bands 6.25, quantband 5.19, pulses 3.00, rotation 0.69 (now only 7.9 calls),
theta 0.56. Vector 07 before/after: pvq-bands 3.40 / 3.15, pulses 1.34 / 1.05. Vector 11:
18.1 / 18.1 (pulses 10.8 / 9.9). Raw output is in the lane scratch files, not committed.

- **Spread rotation**: `(cos, sin)` depends only on `(spread, length, k)`, so a 512-entry
  direct-mapped cache per decoder replaces the double-precision `Math.SinCos`. A hit returns the
  value the miss computed (bit-identical). The `2k >= n` and no-spread early outs moved to the
  caller so dense leaves skip the call.
- **Pulse search**: the pulse-cache row is resolved once per leaf; `Bits2Pulses` and the
  bit-cost lookups index it by reference (the unused `Pulses2Bits`/`CacheRow` helpers are gone).
- **CWRS unrank**: for the last two dimensions `S(2,k) = 2k-1`, so the search for the pulse
  count is closed form, `(i+1)/2` clipped to `k-1`. This removes a data-dependent loop (and its
  mispredicts) from every vector. Index-exact; `Perf7Tests` checks the bijection for all
  `k <= 128` and the conformance vectors check the order.
- **`TellFrac`**: the three fractional bits are the count of seven precomputed thresholds the
  16-bit range has reached, replacing three dependent multiply/shift rounds. `Perf7Tests`
  compares it with the reference loop for every normalized range.

Tried and not done: fusing de-emphasis with the int16 pass (int16 is 0.05 us/frame, nothing to
gain), explicit-stack theta recursion and closed-form N=3/4 unranking (the leaf cost is dominated by
the two range-decoder divides, the data-dependent unrank branches and `1/sqrt`; none of these is
removed by restructuring). Remaining gap on 01/07 is about 0.9 us/frame decode-side; 11 is 3 % over.

## PERF-8: division-free range decode (measured, no gain, not merged as code)

Same-window measurement on the M5 Max (`micro decode`, and `scripts/bench-libopus-inproc.sh` in the
same lock window). Ratio is Kiln (decode + synthesis + int16) over libopus 1.5.2 in-process.

| Vector | Baseline decode | Reciprocal + rsqrt table decode | Ratio (both) |
| --- | --- | --- | --- |
| 01 | 5.78 us | 5.82 / 5.81 us | 1.14 |
| 07 | 3.06 us | 3.07 / 3.07 us | 1.13 |
| 11 | 14.33 us | 14.62 / 14.49 us | 1.02 |

Tried, bit-exact, and measured as a pair:

- `Decode`: `rng / ft` for `2 <= ft <= 256` (every `DecodeUInt` chunk and small theta totals)
  replaced by `high64(rng * (floor((2^64-1)/ft) + 1))`. Exact for every 32-bit dividend
  (`n*d < 2^64` keeps the rounding excess under the `1/d` gap); tested exhaustively below 2^20 and on
  both sides of every multiple near 2^32 for all 255 divisors.
- `1 / sqrt(energy)` in `AlgUnquant` replaced by a 2048-entry table built with the same expression
  (bit-identical).

Result: no measurable change (within 1 % noise, 11 slightly worse). The 32-bit integer divide and
the float sqrt/divide are cheap on this core, so the per-leaf chain is not latency-bound on them.
The code was therefore not kept. Not tried, by analysis: a float-estimate `val / ext` (needs
`frecpe` plus two Newton steps, about 25 cycles, no better than the divide) and
`ReciprocalSqrtEstimate` (same cost as `sqrt` + `div`). The remaining 0.6-0.9 us/frame on 01/07 is
therefore not in the divides; the next step needs a cycle-level profile (Instruments) of
`QuantBand`/`Cwrsi` branch mispredicts rather than more arithmetic substitution. The target of 1.0
is not met on 01/07; vector 11 is within 2 %.

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
SILK-QUAL-1, SILK-QUAL-2 and SILK-QUAL-3 changed. It is evidence, not a CI gate; the gate lives in
`SilkEncoderQualityTests`.

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
| NB | 20 ms | 8  | 4.01 | 3.60 | +0.41 |
| NB | 60 ms | 8  | 4.10 | 3.64 | +0.46 |
| NB | 20 ms | 12 | 5.52 | 5.51 | +0.01 |
| NB | 60 ms | 12 | 5.55 | 5.49 | +0.06 |
| MB | 20 ms | 8  | 4.06 | 2.77 | +1.29 |
| MB | 60 ms | 8  | 4.12 | 2.81 | +1.30 |
| MB | 20 ms | 12 | 5.34 | 5.01 | +0.33 |
| MB | 60 ms | 12 | 5.40 | 4.84 | +0.56 |
| MB | 20 ms | 20 | 6.55 | 6.73 | -0.19 |
| MB | 60 ms | 20 | 6.59 | 6.72 | -0.13 |
| WB | 20 ms | 8  | 3.92 | 1.71 | +2.21 |
| WB | 60 ms | 8  | 4.05 | 1.77 | +2.28 |
| WB | 20 ms | 12 | 5.01 | 4.50 | +0.52 |
| WB | 60 ms | 12 | 5.06 | 4.37 | +0.69 |
| WB | 20 ms | 20 | 6.55 | 6.44 | +0.11 |
| WB | 60 ms | 20 | 6.57 | 6.43 | +0.13 |

SILK-QUAL-2 improved every complexity-0 configuration, by 0.24-0.74 dB, with no regressions. Ten of
sixteen met the target outright; the other six trailed by 0.00-0.30 dB, all at the high-rate regime
(above 1.2 bits per internal-rate sample). SILK-QUAL-3 closes four of those six: narrowband 12 kbps
at both frame durations and wideband 20 kbps at both frame durations now meet or beat libopus. Only
mediumband 20 kbps still trails, by 0.19 dB (20 ms) and 0.13 dB (60 ms); both are improvements over
SILK-QUAL-2's 0.21 and 0.30 dB.

### Complexity 5 and 10

Complexity 5 and 10 are reported, not gated. The delayed-decision quantizer and warped shaping pay
off most at the low rates, and are what produce the WB 8 kbps wins (3.55 dB at c5 and 3.84 dB at
c10 for 20 ms, against libopus 2.99 and 3.53 dB). SILK-QUAL-2 applies the same quantization-offset
change at every complexity, so c5/c10 improved as well. The remaining c5/c10 gap to libopus is
0-0.6 dB across the matrix: WB 8 kbps beats it by 0.3-0.6 dB, while NB 8 kbps trails by 0.3-0.5 dB
and the 12-20 kbps cases trail by 0.1-0.6 dB.

### What SILK-QUAL-1 changed

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

### What SILK-QUAL-2 changed

All three are encoder-side decisions; the bitstream stays valid and the decoder follows the
transmitted flags.

1. **Low quantization offset.** The encoder now always transmits quantization offset type 0 (the
   low offset). The reference rule selects the high offset for some voiced frames and sparse
   unvoiced frames; the low offset lowers the reconstruction error and raises segSNR by 0.1-0.5 dB
   at every rate, at no extra cost. This is the single largest and most uniform gain.
2. **Bitrate-gated trellis width and warped shaping.** Above 1.2 bits per internal-rate sample
   (`bitrate >= 1.2 * internal_rate`, which covers NB 12 kbps and MB/WB 20 kbps) the encoder uses a
   single greedy NSQ state and disables warped shaping; below it keeps the four-state trellis and
   warping. At high rate the quantization is fine enough that the trellis and warping move noise
   without lowering it, so the greedy path is both cheaper and slightly better. This is where most
   of the 20 kbps gain comes from.
3. **Rate model at low rate.** Below the threshold the delayed-decision lambda base is raised
   (`-50 * 4` instead of `-50 * 1`, matching the wider search) and the SNR boost starts at 11 kbps
   instead of 14 kbps.

These were chosen from a parameter sweep over the trellis width, warping strength, lambda, SNR
threshold, quantization offset, NLSF interpolation and MSVQ survivors, harmonic/tilt/low-frequency
shaping strength and richer c0 analysis. Richer analysis at complexity 0 (the complexity-4 block)
and stronger shaping both made quality worse; the three above are the survivors and were checked
on vectors 04 and 06 as a held-out pair.

### What SILK-QUAL-3 changed

All four changes act only in the high-rate regime (`HighRate != 0`, i.e. above 1.2 bits per
internal-rate sample: NB 12 kbps, MB 20 kbps, WB 20 kbps). The ten low-rate configurations that
already beat libopus are untouched, so none of them can regress. Tuning used vectors 02 and 03;
vectors 04 and 06 were held out and confirm the result.

1. **Stronger tilt shaping at high rate.** `NoiseShapeAnalysis` scales the tilt shaping coefficient
   by 1.5 in the high-rate path. In the single-state quantizer a stronger tilt lowers reconstruction
   noise uniformly: +0.01-0.09 dB across all six targets, at no extra cost (one multiply per frame).
   The low-rate path keeps the SILK-QUAL-2 strength, which that sweep had already tuned.
2. **Per-band NSQ rate penalty.** The delayed-decision lambda base is 4 for narrowband and 2 for
   medium/wideband at high rate (it was 1 for every high-rate configuration). Narrowband 12 kbps
   was over-producing pulses and gains most (+0.11 dB); the medium/wideband optimum is 2.
3. **Narrowband 12 kbps target-SNR lift.** One extra dB of target SNR at narrowband high rate,
   gated to `FsKhz == 8` and `HighRate != 0`. The narrowband configuration was bit-starved relative
   to libopus; this adds +0.16 dB and is what flips both narrowband 12 kbps rows above libopus.
   It costs about 6% more payload bytes at narrowband (see below).
4. **Voice-activity-weighted per-frame allocation at 60 ms.** For high-rate 60 ms packets the
   per-frame SNR target is distributed by speech activity, normalized so the packet mean stays at
   the configured rate (the bit reservoir damps the excursions). This is worth +0.06 dB on both
   60 ms targets and is neutral at 20 ms.

Rate-distortion gain quantization was implemented and measured first, as planned: enumerating every
decoder-accepted gain symbol and minimizing squared log-gain error (with and without a rate term)
regressed segSNR by 0.01-0.08 dB everywhere. The reference nearest-index-plus-hysteresis rule is a
better fit for the quantizer's interaction with the noise-shaping loop, so it was dropped. The LTP
and NLSF quantizers already include rate terms (SILK-QUAL-1), and forcing the NSQ rate loop to
enforce the packet budget instead of the packet capacity also regressed (0.19-0.33 dB), confirming
that side-information bits do not buy excitation pulses in this rate range.

Payload bytes: the high-rate changes stay in the same range as SILK-QUAL-2 (MB/WB about 13-14%
above libopus). The narrowband SNR lift raises narrowband from about 5% to about 15% above libopus.

### Encode cost

Measured with a stopwatch over the whole vector 02 corpus (1251 frames of 20 ms, MB, 20 kbps) on
the shared build machine, so treat the numbers as indicative:

| Complexity | Greedy NSQ (before) | Delayed decision (after) | Ratio |
| ---------- | ------------------- | ------------------------ | ----- |
| 0 | 71.8 µs/frame | 83.4-85.5 µs/frame | 1.16-1.19x |
| 5 | 268 µs/frame | 274 µs/frame | ~1.02x (within run-to-run noise) |

Both are inside the 1.5x budget. Allocations remain zero per frame and the bitstream stays valid
(`SilkEncoderInteropTests`, `SilkEncoderSmokeTests`, `SilkEncoderAllocationTests`).

SILK-QUAL-2 does not increase encode cost. The low-rate path (below 1.2 bits per sample) is
unchanged in computation; the high-rate path uses one NSQ state instead of four and skips the
warped autocorrelation and warped gain, so it is strictly cheaper. The changes are carried by the
same fixed-point kernels and add no allocations.

SILK-QUAL-3 does not increase encode cost either; the per-frame stopwatch in the quality harness
shows the same or slightly lower per-frame times (a few percent, because the higher narrowband and
mediumband lambda produces fewer excitation pulses). The added work is one multiply for the tilt
scale, one multiply and a few adds for the allocation normalization, and a lookup for the lambda,
all allocation-free. The ratio against SILK-QUAL-2 is about 1.0x, well inside the 1.5x budget.

### Remaining gap and next ideas

SILK-QUAL-3 leaves one gap: MB 20 kbps, 0.19 dB (20 ms) and 0.13 dB (60 ms) behind libopus, both
improved from SILK-QUAL-2. Every other complexity-0 configuration meets or beats libopus. The four
high-rate structural knobs that remained in SILK-QUAL-2's sweep (trellis width, warping, the SNR
threshold and the quantization offset) were re-measured at these rates in SILK-QUAL-3 and still
regress; narrowband and wideband responded to tilt strength, the per-band lambda and the narrowband
SNR lift, while mediumband 20 kbps did not respond to the SNR lift or to stronger shaping beyond
SILK-QUAL-3's 1.5x. What remains for mediumband:

1. **Deeper excitation/noise-shaping work at the 12 kHz internal rate**, where libopus still makes
   up 0.1-0.2 dB with fewer payload bits; the gap there is not rate-control-limited.
2. **LPC analysis improvements** (window, order, noise-floor regularization) at 12 kHz.
3. **Rate-distortion NLSF/gain selection that accounts for the noise-shaping loop**, which the
   plain squared-error RD of SILK-QUAL-3 did not capture.

### Frozen RFC baseline and the ratchet

The hard gate at complexity 0 is a ratchet, not a fixed libopus target. The RFC 6716 Appendix A
figures are frozen in `tests/Kiln.Audio.Tests/SilkEncoder/SilkRfcBaseline.cs`, measured with this
harness (build the RFC source with `FIXED_POINT=1`, then run `SilkRfcBaselineGenerator` with
`KILN_SILK_RFC_OPUS_DEMO` set to its `opus_demo`). Per configuration the test asserts
`ours >= max(RFC, frozen ours - 0.05 dB)` where we still trail libopus, and
`ours >= frozen libopus` where we already beat it. SILK-QUAL-2 and SILK-QUAL-3 raised every floor:
the fourteen configurations that now beat or meet libopus are pinned to its figure, and the two
mediumband 20 kbps rows to their new measured score minus 0.05 dB, so the gain is locked in. The
remaining gaps stay reported in the test output and in the table above.
