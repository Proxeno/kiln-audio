# Decoder performance baseline

This is the reproducible baseline for PERF-0: how fast Kiln.Audio decodes the RFC 8251
test vectors compared with the libopus 1.5.2 reference, plus the cost of the individual
kernels. It is evidence for prioritising PERF-1, not a CI gate.

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

The projects target `net10.0`, but this machine has only the .NET 11 runtime installed.
The host is rolled forward with `-p:RollForward=Major`, and BenchmarkDotNet is given the
`net10.0` runtime moniker it knows. BenchmarkDotNet therefore labels the job ".NET 10.0",
but every number below ran on **.NET 11.0.0**; the `Runtime` line in the BenchmarkDotNet
summary says so. BenchmarkDotNet 0.15.8 does not recognize the .NET 11 runtime and its
default host job throws, which is why the run below passes `--runtimes net10.0` and
`DOTNET_ROLL_FORWARD=Major` (the latter reaches the benchmark child process).

This is a shared machine; repeated runs varied by up to ~15% on the CELT vectors and up to
~50% on the smallest kernel (see the note under the kernel table). Treat the numbers as
indicative, not exact.

## Commands

```bash
bash scripts/fetch-test-vectors.sh
bash scripts/build-libopus-oracle.sh

# Kiln decoder and kernel benchmarks
DOTNET_ROLL_FORWARD=Major dotnet run -c Release -p:RollForward=Major \
  --project bench/Kiln.Audio.Benchmarks -- \
  --filter '*Decode*' '*Kernel*' --runtimes net10.0

# libopus 1.5.2 reference timings
bash scripts/bench-libopus.sh
```

The oracle build is what `scripts/build-libopus-oracle.sh` performs:

```text
libopus 1.5.2
  cmake -DCMAKE_BUILD_TYPE=Release -DOPUS_BUILD_PROGRAMS=ON \
        -DOPUS_BUILD_TESTING=OFF -DOPUS_BUILD_SHARED_LIBRARY=OFF
  compiler: /usr/bin/cc
  CMAKE_C_FLAGS='' CMAKE_C_FLAGS_RELEASE='-O3 -DNDEBUG'
```

libopus enables its arm64 NEON/RTCD paths for this build, so this is its normal optimized
configuration.

## Method

**Kiln.** The `.bit` files are parsed into memory in `[GlobalSetup]`, so no file IO is
measured. Each benchmark invocation resets the decoder and synthesizer, then decodes the
whole vector. The CELT path mirrors the conformance driver: CELT bitstream decode,
synthesis to float PCM at 48 kHz for two output channels, then `CeltPcm.ToInt16`. The SILK
path decodes each packet with the channel count and frame duration from its TOC and runs
`SilkSynthesis` at the SILK internal rate (8/12/16 kHz for vectors 02/03/04). Allocations
come from BenchmarkDotNet's `[MemoryDiagnoser]` and are per whole-vector operation.

**libopus.** `scripts/bench-libopus.sh` runs `opus_demo -d 48000 2 <vector>.bit /dev/null`
five times and keeps the minimum wall time; for the SILK-only vectors it also runs
`opus_demo -d <internal rate> 2`. Wall time includes reading the `.bit` file and process
startup. The measured startup floor (`opus_demo` with no arguments) is 2 ms, which is a
meaningful part of the short SILK runs.

**Speed vs real time** is audio duration divided by the mean time. Audio duration is
derived from the 48 kHz stereo decode size. **Ratio** is Kiln mean time divided by libopus
minimum time for the same audio (SILK compared at the internal rate); a value above 1 means
Kiln is slower. The SILK comparison is somewhat generous to Kiln: the Kiln benchmark stops
at internal-rate PCM and does not include libopus's resampler or its file IO/startup.

## Decode results

Allocations are 0 B for every vector, which is 0 B per frame. Frame counts (Opus frames
decoded) are 5,524 for 01, 4,186 for 07, 1,501 for 11, 1,185 for 02, 998 for 03 and 1,265
for 04.

| Vector | Mode | Audio | Kiln mean | Kiln vs real time | libopus vs real time | Ratio (Kiln/libopus) | Alloc/frame |
| ------ | ---- | ----- | --------- | ----------------- | -------------------- | -------------------- | ----------- |
| 01 | CELT, 48 kHz stereo | 29.48 s | 150.82 ms | 195x | 719x | 3.68 | 0 B |
| 07 | CELT, 48 kHz, mixed frames | 22.61 s | 97.72 ms | 231x | 1,076x | 4.65 | 0 B |
| 11 | CELT, 48 kHz stereo | 30.02 s | 155.51 ms | 193x | 938x | 4.86 | 0 B |
| 02 | SILK NB, 8 kHz | 25.03 s | 8.40 ms | 2,979x | 3,129x | 1.05 | 0 B |
| 03 | SILK MB, 12 kHz | 21.16 s | 9.25 ms | 2,287x | 2,351x | 1.03 | 0 B |
| 04 | SILK WB, 16 kHz | 26.63 s | 13.50 ms | 1,973x | 2,421x | 1.23 | 0 B |

For reference, libopus decoding the SILK vectors at 48 kHz (including its resampler) runs
at 2,086x (02), 1,763x (03) and 1,664x (04). Subtracting the 2 ms startup floor from the
libopus timings raises the ratios to about 3.9/5.1/5.2 for CELT and 1.4/1.3/1.5 for SILK:
Kiln's SILK synthesis is within roughly 1.5x of optimized C, while the CELT path is about
4–5x slower and holds nearly all of the gap.

## Kernel results

One invocation of each kernel. These are the steady-state means from `KernelBenchmarks`;
the same MDCT instances give the same numbers as the pre-existing `MdctBenchmarks` class
(960-point inverse: 37.8 µs here vs 38.9 µs there).

| Kernel | Work per invocation | Mean | Allocated |
| ------ | ------------------- | ---- | --------- |
| `MdctInverse960` | One 960-coefficient inverse MDCT (20 ms, one channel) | 37.78 µs | 0 B |
| `MdctInverse8x120` | Eight interleaved 120-coefficient inverse MDCTs (20 ms, one channel) | 27.11 µs | 0 B |
| `PvqDecodeBand` | PVQ band decode, 96 coefficients (band 18 at 20 ms), 32 pulses | 155.8 ns | 0 B |
| `CeltPostFilter` | Comb post-filter over a 960-sample frame with tap-set cross-fade | 875.9 ns | 0 B |
| `SilkLpcSynthesis` | Short-term LPC synthesis, 20 ms / 16 kHz, order 16 | 4.026 µs | 0 B |
| `SilkLtpSynthesis` | Long-term prediction, 20 ms / 16 kHz, five taps, four subframes | 586.3 ns | 0 B |

The SILK LPC and LTP benchmarks call the same internal `SilkFixedPoint` helpers and copy
the inner loops of `SilkSynthesis.SynthesizeChannel` verbatim; that method is private and
this lane is measurement-only, so it cannot be invoked directly. The CELT kernels call the
production methods through `InternalsVisibleTo`.

Note on variance: the 8x120 MDCT measured 27.1 µs in the run above and 40.1 µs in a
separate `MdctBenchmarks` run on the same machine; the 960-point case agreed to ~3%. This
is the largest run-to-run spread in the kernel set.

## Top hotspots and PERF-1 ideas

1. **Inverse MDCT (37.8 µs for 960, 27.1 µs for 8x120, per channel per 20 ms).** This is
   by far the largest kernel: a 20 ms stereo CELT frame spends roughly 55–76 µs in the
   inverse transform alone. The scalar DCT-IV packs to a complex FFT with pre/post
   rotations and runs four of those passes. PERF-1 idea: replace the packing with a SIMD
   real FFT plus vectorized pre/post rotations, and for transient frames run the eight
   short blocks as eight SIMD lanes instead of eight scalar transforms.
2. **SILK LPC synthesis (4.03 µs per 20 ms wideband channel-frame).** The order-16
   `Smlawb` MAC per output sample is the dominant SILK kernel; LTP is only 0.59 µs. PERF-1
   idea: vectorize the 16-tap fixed-point dot product and produce several output samples
   per iteration with `Vector<int>`/`Vector<long>`, keeping the exact Q-format shifts,
   rounding and saturation so the output stays bit-exact.
3. **CELT post-filter (0.88 µs per 960-sample channel-frame).** The five-tap comb with a
   cross-faded tap set is the next CELT cost after the MDCT; PVQ (0.16 µs) and LTP are an
   order of magnitude smaller. PERF-1 idea: keep the tap-set choice outside the loop (it
   already is) and vectorize the comb across contiguous samples at the two period offsets.

The CELT path is the priority: it is ~4–5x off libopus and its MDCT accounts for most of
the per-frame kernel time, whereas SILK synthesis is already within ~1.5x.

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

Every level uses the same TF analysis, allocation and PVQ search. The pitch pre-filter of
RFC 6716 §4.3.7.1 (encoder side) is not implemented; the bitstream always signals it off. libopus
also disables it below complexity 5, so the complexity-0 comparison is unaffected. Because the
pre-filter is absent, levels 5-10 are equivalent to 4 here.

### Encode speed

The corpus is 320.62 s of audio encoded as 16,031 frames of 20 ms. `Mean` is the
BenchmarkDotNet mean for the whole corpus in one invocation; `µs/frame` and `× real time` are
derived from it. The allocation column is BenchmarkDotNet's `[MemoryDiagnoser]` for the whole
invocation: zero bytes.

| Complexity | Mean | µs/frame | × real time | Allocated |
| --- | --- | --- | --- | --- |
| 0 | 1.888 s | 117.8 | 170x | 0 B |
| 1 | 1.971 s | 123.0 | 163x | 0 B |
| 2 | 1.833 s | 114.3 | 175x | 0 B |
| 3 | 1.954 s | 121.9 | 164x | 0 B |
| 4 | 2.022 s | 126.1 | 159x | 0 B |
| 5 | 1.940 s | 121.0 | 165x | 0 B |
| 6 | 1.877 s | 117.1 | 171x | 0 B |
| 7 | 1.902 s | 118.6 | 169x | 0 B |
| 8 | 1.709 s | 106.6 | 188x | 0 B |
| 9 | 1.804 s | 112.5 | 178x | 0 B |
| 10 | 1.761 s | 109.9 | 182x | 0 B |

The spread between complexities is measurement noise on this shared machine (the same run's
per-invocation standard error reached 7%): levels 0-3 differ only in transient and spread
analysis, and levels 5-10 do exactly the same work as 4. The encoder runs at roughly 160-190x
real time for the whole corpus at 32 kbps.

**libopus.** `scripts/bench-libopus-encode.sh` times the same 320.62 s file five times and keeps
the best: complexity 0 **0.206 s (1,556x real time)**, complexity 5 **0.739 s (434x)** and
complexity 10 **0.791 s (405x)**. Those times include process start-up and reading the whole
input file, which is generous to us.

**Iso-quality.** Our complexity-0 aggregate SNR at 32 kbps (16.313 dB) is above libopus
complexity 5 (15.795 dB) and below complexity 10 (16.653 dB), so the lowest measured libopus
complexity whose quality is at least ours is 10. Ours takes 1.888 s in-process against libopus's
0.791 s with file IO: **we are 2.4x slower at equal-or-better quality**. At the same complexity 0,
libopus is 9.2x faster (0.206 s vs 1.888 s). The gap is the forward MDCT: 89 µs of the roughly
118 µs per frame is the scalar `Mdct.Forward`, which lane PERF-1 owns. The encoder's own kernels
are the transient detector (3.2 µs), the PVQ search (2.1 µs) and sub-microsecond energy and
pre-emphasis work. When PERF-1's SIMD MDCT lands, this encoder inherits it without a change here.
The front-end is otherwise allocation-free and its non-MDCT kernels are already vector-ready;
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

### Kernel breakdown

One invocation of each kernel, steady state, from `EncodeKernelBenchmarks`. The five largest are
listed first by time. Allocations are 0 B for every kernel.

| Kernel | Work per invocation | Mean | Allocated |
| --- | --- | --- | --- |
| `MdctForward960` | One 960-coefficient forward MDCT (20 ms, one channel) | 88.91 µs | 0 B |
| `MdctForward8x120` | Eight interleaved 120-coefficient forward MDCTs (20 ms, one channel) | 63.79 µs | 0 B |
| `TransientAnalysis` | Transient detector over 1080 pre-emphasized samples | 3.20 µs | 0 B |
| `PvqSearch` | PVQ search, N = 96, K = 32 | 2.10 µs | 0 B |
| `BandEnergy` | Sum of squares over 960 samples | 577 ns | 0 B |
| `PreEmphasis` | 960 samples | 383 ns | 0 B |
| `NormaliseBand` | 960 samples, `Vector128` path | 71 ns | 0 B |

The forward MDCT is 75% of the per-frame cost; the transient detector and PVQ search are the only
other microsecond-scale work. The PVQ search is a scalar soft-search of the reference algorithm
(MaxIteration-wise identical to `alg_quant`); a measured faster original search is the next
encoder-side idea, but the MDCT is where the missing 9x at complexity 0 lives.

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
