# Contributing to Kiln.Audio

Kiln.Audio is a pure-managed, SIMD-accelerated Opus (RFC 6716)
encoder and decoder. It exists specifically so it can be embedded under
Apache-2.0 in products where GPL/LGPL/native codec linkage is a problem.
Contributions are held to that bar — see Originality and citations below.

## Getting set up

You need the **.NET 10 SDK**. The RFC 8251 `.bit` packet files are committed, but their large
`.dec` reference decodes (117 MB) are not, so run the fetch script before `dotnet test`:

```bash
git clone https://github.com/Proxeno/kiln-audio.git
cd kiln-audio

bash scripts/fetch-test-vectors.sh    # required: materializes the RFC 8251 .dec references

dotnet restore
dotnet build -c Release
dotnet test -c Release
```

For the oracle-backed tests, also build the pinned libopus tools (needs CMake and a C compiler):

```bash
bash scripts/build-libopus-oracle.sh  # optional: pinned libopus 1.5.2 -> .oracle/bin
```

Both scripts verify a pinned SHA-256 and are idempotent: a matching stamp makes
the script a no-op. `opus_demo` and `opus_compare` are a **test-only black-box
oracle**; nothing under `src/` may reference libopus.

Because the vectors are committed, the conformance tests fail when they are
missing. The oracle binaries are optional: tests that need them return without
asserting when they are absent. Set `KILN_AUDIO_REQUIRE_ORACLE=1` to turn a
missing oracle into a test failure instead; CI sets it on Linux and macOS.

`dotnet build` must report zero warnings — see [Style](#style) below.

## Repository layout

- [`src/Kiln.Audio`](src/Kiln.Audio) — the library. `Kiln.Audio` is the public
  namespace; internals are accessible to `Kiln.Audio.Tests`,
  `Kiln.Audio.Benchmarks` and `Kiln.Audio.Fuzz` via `InternalsVisibleTo`.
- [`tests/Kiln.Audio.Tests`](tests/Kiln.Audio.Tests) — xunit test project.
- [`bench/Kiln.Audio.Benchmarks`](bench/Kiln.Audio.Benchmarks) —
  BenchmarkDotNet benchmarks.
- [`tests/Kiln.Audio.Fuzz`](tests/Kiln.Audio.Fuzz) — the coverage-blind mutation fuzzer
  (see [`docs/robustness.md`](docs/robustness.md)).
- [`tests/Kiln.Audio.AotSmoke`](tests/Kiln.Audio.AotSmoke) — Native AOT round-trip smoke test.
- [`docs/architecture.md`](docs/architecture.md) — modules, mode selection and SIMD kernel sets.
- [`docs/performance.md`](docs/performance.md) — measurements against libopus.
- [`docs/releasing.md`](docs/releasing.md) — how a release is cut and the public API lock.

## Test layout

- **SIMD/scalar parity tests** — every SIMD kernel has a test asserting
  bit-identical output to its scalar counterpart, self-skipping on hosts
  lacking the target ISA. A new kernel needs one too.
- **Quality gates** — encoder quality is gated against frozen baselines of the
  RFC 6716 Appendix A reference encoder. A frozen value is never lowered; a new
  corpus gets a new gate.
- **Decode/encode-oracle tests** — compare against **`opus_demo` / libopus** as
  the independent correctness oracle, using the IETF/RFC 8251 test vectors
  (see [`tests/Kiln.Audio.Tests/Fixtures/README.md`](tests/Kiln.Audio.Tests/Fixtures/README.md)).
  These pass early when the oracle binary isn't found; install it locally if
  you're touching bitstream syntax or reconstruction.

Test collections run non-parallel within the project
(`tests/Kiln.Audio.Tests/xunit.runner.json`).

## Style

The build is strict on purpose, project-wide (`Directory.Build.props`):

- `TreatWarningsAsErrors` is on, at `AnalysisLevel=latest`. No warnings are
  tolerated.
- `EnforceCodeStyleInBuild` is on — style violations fail the build.
- `Nullable` is enabled project-wide.
- `InvariantGlobalization` is on — comparisons must be ordinal.
- `AllowUnsafeBlocks` is on in `src/Kiln.Audio` and the test/benchmark
  projects for SIMD intrinsics. Keep `unsafe` scoped as tightly as possible.

## Originality and citations

Kiln.Audio is written against the published RFC 6716 (and RFC 8251)
specification text. It may also include code translated from the RFC 6716
reference implementation in Appendix A. Two rules keep this clear:

- **Implement from the spec, and cite it.** When you implement a spec clause,
  cite it (`§3.1`, `Table 2`, etc.) in the doc comment or inline comment next
  to the code it justifies. Numeric tables that reproduce specification data
  must carry a clause/table citation.
- **Original code, with one licensed exception.** Do not copy or translate
  libopus source (BSD-licensed or otherwise) into this repository, and do not
  vendor it. libopus is used only as a black-box **oracle** in tests. Code
  translated from the RFC 6716 Appendix A reference is allowed, provided the
  Simplified BSD notice is kept in `THIRD-PARTY-NOTICES.md` and the derived
  file carries its header comment. If you're unsure whether an approach is
  appropriate, ask in the PR description before writing the code.

## License of contributions

Kiln.Audio is licensed under Apache-2.0, and contributions are accepted under
the same license: when you open a pull request, you agree that your
contribution is licensed under Apache-2.0 (inbound = outbound). You keep the
copyright to your work — there is no copyright assignment.

We use the [Developer Certificate of Origin](https://developercertificate.org/)
(DCO). Sign off every commit:

```
git commit -s
```

## Continuous integration

CI (`.github/workflows/ci.yml`) builds and tests on Linux, Windows and macOS
runners on every push and PR to `main`. The
matrix exercises both SIMD ISA families: Linux and Windows runners are x64
(AVX2/SSSE3), macOS runners are arm64 (NEON). A change that only builds/tests
clean on one architecture is not done.
