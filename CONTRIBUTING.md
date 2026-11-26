# Contributing to Kiln.Audio

Kiln.Audio is a (future) pure-managed, SIMD-accelerated Opus (RFC 6716)
encoder and decoder. It exists specifically so it can be embedded under
Apache-2.0 in products where GPL/LGPL/native codec linkage is a problem.
Contributions are held to that bar — see Originality and citations below.

The repository is currently a skeleton: public API stubs and the TOC parser
exist; the codec does not.

## Getting set up

You need the **.NET 10 SDK** and nothing else to build and run the unit test
suite. The conformance tests additionally use material that is not committed;
two idempotent scripts fetch and build it, each verifying a pinned SHA-256:

```bash
git clone https://github.com/Proxeno/kiln-audio.git
cd kiln-audio

# Optional, for the conformance suite:
bash scripts/fetch-test-vectors.sh    # RFC 8251 test vectors -> tests/Kiln.Audio.Tests/Fixtures/opus_testvectors
bash scripts/build-libopus-oracle.sh  # pinned libopus 1.5.2 -> .oracle/bin (needs CMake and a C compiler)

dotnet restore
dotnet build -c Release
dotnet test -c Release
```

Both scripts are safe to re-run: the vectors and the oracle build are
git-ignored, and a matching stamp makes the script a no-op. `opus_demo` and
`opus_compare` are a **test-only black-box oracle**; nothing under `src/` may
reference libopus.

Oracle tests skip themselves when the vectors or the oracle are missing, so a
clean checkout still passes `dotnet test`. Set `KILN_AUDIO_REQUIRE_ORACLE=1`
to turn absence into a test failure instead; CI sets it on Linux and macOS.

`dotnet build` must report zero warnings — see [Style](#style) below.

## Repository layout

- [`src/Kiln.Audio`](src/Kiln.Audio) — the library. `Kiln.Audio` is the public
  namespace; internals are accessible to `Kiln.Audio.Tests` and
  `Kiln.Audio.Benchmarks` via `InternalsVisibleTo`.
- [`tests/Kiln.Audio.Tests`](tests/Kiln.Audio.Tests) — xunit test project.
- [`bench/Kiln.Audio.Benchmarks`](bench/Kiln.Audio.Benchmarks) —
  BenchmarkDotNet benchmarks.
- [`docs/architecture.md`](docs/architecture.md) — planned modules and status.

## Test layout

- **SIMD/scalar parity tests** — once SIMD kernels exist, each needs a test
  asserting bit-identical output to its scalar counterpart, self-skipping on
  hosts lacking the target ISA.
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

Kiln.Audio is original work written against the published RFC 6716 (and RFC
8251) specification text. Two rules keep it that way:

- **Implement from the spec, and cite it.** When you implement a spec clause,
  cite it (`§3.1`, `Table 2`, etc.) in the doc comment or inline comment next
  to the code it justifies. Numeric tables that reproduce specification data
  must carry a clause/table citation.
- **Contributions must be your own original work.** Do not copy or translate
  libopus source (BSD-licensed or otherwise) into this repository, and do not
  vendor it. libopus is used only as a black-box **oracle** in tests. If
  you're unsure whether an approach is appropriate, ask in the PR description
  before writing the code.

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

CI (`.github/workflows/ci.yml`) builds and tests on `ubuntu-latest`,
`windows-latest`, and `macos-latest` on every push and PR to `main`. The
matrix exercises both SIMD ISA families: Linux and Windows runners are x64
(AVX2/SSSE3), macOS runners are arm64 (NEON). A change that only builds/tests
clean on one architecture is not done.
