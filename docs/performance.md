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
