# Robustness and fuzzing

The decoder is the trust boundary: its input is whatever a remote peer sends. This document
states the contract every entry point keeps for arbitrary input, and describes the fuzzer that
asserts it.

## The contract

Any byte string is legal input to the decoder. The framing rules of RFC 6716 §3.2–§3.4 decide
whether a packet is decoded or rejected; the decoder never trusts a length, a frame count, a
band index or an entropy-coded value beyond the bounds the rules establish.

| Entry point | Documented exceptions | Everything else |
|---|---|---|
| `OpusDecoder.Decode(ReadOnlySpan<byte>, Span<short>, bool)` | `InvalidDataException` (framing), `ArgumentException` (destination too short), `ObjectDisposedException` | Never thrown |
| `OpusDecoder.Decode(ReadOnlySpan<byte>, Span<float>, bool)` | Same as above | Never thrown; every written sample is finite |
| `OpusDecoder.DecodeLoss(Span<short>)` | `ArgumentException` (destination too short), `ObjectDisposedException` | Never thrown |
| `OpusEncoder(OpusEncoderOptions)` | `ArgumentNullException`, `ArgumentOutOfRangeException` (an illegal option; derives from `ArgumentException`), `NotSupportedException` (a legal but unimplemented configuration) | Never thrown |
| `OpusEncoder.Encode(ReadOnlySpan<short>\|float>, Span<byte>)` | `ArgumentException` (wrong frame length or a short packet buffer), `ObjectDisposedException` | Never thrown |

`OpusValidation` throws only `ArgumentOutOfRangeException`, so the full set of exception types
reachable from the public API is:

- `System.ArgumentException` and its `ArgumentOutOfRangeException` subclass,
- `System.InvalidDataException`,
- `System.NotSupportedException`,
- `System.ObjectDisposedException`.

`IndexOutOfRangeException`, `NullReferenceException`, `OverflowException`, `DivideByZeroException`,
`StackOverflowException` and `OutOfMemoryException` are defects, not contract.

### Output shape

- A valid packet returns exactly `frameCount * samplesPerFrame` samples per channel, where
  `samplesPerFrame` is the RFC 6716 §2, Table 2 value for the TOC configuration and the API sample
  rate. A code 3 packet can carry 1 to 48 frames, so the maximum is 120 ms.
- An empty packet is concealed rather than rejected; the return value is the concealment length
  (see `DecodeLoss`).
- `DecodeLoss` conceals the frame duration of the last successfully decoded packet, or 2.5 ms
  before any packet has been decoded.
- A packet that violates framing throws `InvalidDataException` before any decoder state changes.
  The decoder is usable afterwards and starts from the state it had before the rejected packet.
- Float output never contains NaN or infinity.
- Decoding a valid packet and calling `DecodeLoss` allocate no managed memory after construction.
- One call takes bounded time: there is no loop whose trip count grows without bound with the
  packet contents.

## The fuzzer

`tests/Kiln.Audio.Fuzz` is a standalone, single-threaded, deterministic mutational fuzzer. It is a
separate project so long sessions do not run inside the test host; the same engine sources are
compiled into `tests/Kiln.Audio.Tests` for the smoke test.

Coverage-guided instrumentation (SharpFuzz + libFuzzer) needs a native libFuzzer driver and a
Linux toolchain, which this repository does not build for the Mac CI runner. The harness instead
uses structure-aware mutation with a fixed seed: the same seed and corpus replay the same
sequence on every machine, and a finding is reproducible from the seed, the corpus, and the
execution number.

### Corpus and operators

The seed corpus is the committed RFC 8251 `testvectorNN.bit` packets (20,075 packets). Every
execution picks a seed and applies one to six operators:

- bit flips and random-byte exclusive-or;
- byte insertion, deletion, block duplication, block randomisation and extension;
- length-field writes with the interesting values (0, 251, 252, 255 — the one- and two-byte
  length encoding boundaries);
- TOC sweeps over all 32 configurations, both stereo flags and all four frame-count codes;
- frame-count-byte mutations (count, CBR/VBR, padding) and padding-chain writes;
- truncation.

Ten percent of executions build a structured random packet instead, biased to the code 3 layout,
so the count, padding and VBR paths are reached often. Mutations are biased toward valid framing
because that is where the decoder's deep paths live; a framing-invalid packet only exercises the
parser's rejection path.

### Targets

| Target | What it fuzzes |
|---|---|
| Parser | `OpusPacket.TryParse` for every byte string, plus the short-destination rule; asserts frame ranges are contiguous, inside the packet, at most 1275 bytes, and the packet at most 120 ms |
| Decoder | `OpusDecoder.Decode` and `DecodeLoss` at all five API rates, both API channel counts, `decodeFec` on and off; pooled decoders make the cross-packet state (mode, bandwidth, duration, channel count, PLC history) part of the search |
| Encoder | random option sweeps, random PCM (including NaN, infinities and out-of-range values), wrong frame lengths and short buffers, and a decode round trip of every encoded packet |

Every finding is minimized. A decoder finding that depends on cross-packet state is saved as a
sequence file (`step <rate> <channels> <fec> <float> <loss> <packet hex>`) and replayed on a fresh
decoder; the minimized inputs are committed under `tests/Kiln.Audio.Tests/Robustness/Inputs` with a
regression test beside them.

## Running it

Build once:

```bash
dotnet build -c Release tests/Kiln.Audio.Fuzz/Kiln.Audio.Fuzz.csproj
```

The Mac is shared, so every session runs single-threaded at low priority and is time-boxed to ten
minutes:

```bash
nice -n 19 dotnet tests/Kiln.Audio.Fuzz/bin/Release/net10.0/Kiln.Audio.Fuzz.dll \
    --corpus tests/Kiln.Audio.Tests/Fixtures/opus_testvectors \
    --seconds 600 --seed 4 --out artifacts/fuzz-seed4
```

`--executions N` caps the total target invocations instead of the wall clock, and `--mutations N`
caps the mutated executions after the corpus replay. `--no-encoder`, `--no-alloc-check` and
`--no-replay` trim a session. Findings are written as `<target>-<hash>.bin` (single input) or
`<target>-<hash>.seq` (decoder sequence) plus a text sidecar with the message and parameters.

Reproduce one input:

```bash
dotnet .../Kiln.Audio.Fuzz.dll --repro parser   artifacts/.../parser-<hash>.bin
dotnet .../Kiln.Audio.Fuzz.dll --repro decoder  artifacts/.../decoder-<hash>.bin 48000 2 false true
dotnet .../Kiln.Audio.Fuzz.dll --repro sequence artifacts/.../decoder-<hash>.seq
```

The CI smoke test is `tests/Kiln.Audio.Tests/Robustness/FuzzSmokeTests`: it replays the whole
corpus, measures the zero-allocation contract, and runs 20,000 mutations with seed `0x46555A5A`
(`FUZZ`), in 30 seconds or less.

## Findings

| Finding | Input | Root cause | Fix |
|---|---|---|---|
| SILK concealment overran its output when the lost frame was shorter than the last good frame | sequences `silk-plc-short-after-long-*.seq` | `SilkPlc.Conceal` kept the previous frame's geometry (`_frameLength`), so a 10 ms concealment after a 20 ms frame demanded 20 ms of buffer | `Conceal` now derives the frame geometry from the lost duration, as `silk_Decode()` does (Appendix A, `silk/dec_API.c`) |
| `IndexOutOfRangeException` concealing a CELT-only frame after a hybrid loss | sequence `celt-plc-noise-loss-then-pitch.seq` | Hybrid concealment runs the noise branch and bumps the shared loss counter without a pitch search, leaving the pitch index at zero; the next CELT-only loss reused it and the excitation offset stopped advancing | `PlcPitch` searches for a pitch when the stored index is not valid, not only when the loss count is zero |

Both are covered by `Robustness/CeltPlcConcealmentTests`, `Robustness/SilkPlcDurationTests` and the
committed sequences in `Robustness/FuzzSequenceRegressionTests`.

### Session log

| Date | Build | Seed | Duration | Executions | Findings |
|---|---|---|---|---|---|
| 2026-11-26 | pre-fix | 1 | 20 s | 70,559 | 1 parser-validator defect (harness), 1 decoder exception |
| 2026-11-26 | pre-fix | 1 | 60 s | 305,466 | 5 SILK PLC buffer mismatches, 1 CELT PLC out-of-range |
| 2026-11-26 | SILK PLC fixed | 2 | 60 s | 267,177 | 5 SILK PLC sequences (same defect) |
| 2026-11-26 | pre-CELT-fix | 3 | 600 s | 12,4xx,xxx | 1 CELT PLC out-of-range |

The post-fix sessions are recorded in the pull request.
