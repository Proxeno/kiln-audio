# Test vector fixtures

Oracle vectors for the Opus conformance tests live in `opus_testvectors/`.

- Source: the IETF Opus test vectors (`opus_newvectors`, as updated by RFC 8251). The tarball URL,
  its pinned SHA-256, and the owner's redistribution approval are recorded in
  [`opus_testvectors/SOURCE.md`](opus_testvectors/SOURCE.md).
- The `testvectorNN.bit` packet files (3.4 MB total) are **committed**. The `.dec` reference
  decodes (117 MB) are not: the extracted set is 121 MB, above the 60 MB threshold for committing
  the whole set.
- Run `scripts/fetch-test-vectors.sh` to materialize every `testvector*` file. It verifies the
  pinned SHA-256, is idempotent via a `.sha256` stamp, and preserves `SOURCE.md`.
- The directory contains `testvectorNN.bit`, `testvectorNN.dec`, and `testvectorNNm.dec` for
  `NN` = 01 to 12.
- Expected output is produced by `opus_demo` / libopus, used purely as a black-box oracle. Build
  the pinned tools with `scripts/build-libopus-oracle.sh`; they land in `.oracle/bin` (git-ignored).
- The managed port of the RFC 6716 §6 / RFC 8251 quality metric scores the vectors in
  `tests/Kiln.Audio.Tests/Oracle/OpusCompare.cs`.
- Because the vectors are part of the checkout, a missing vector directory fails the conformance
  tests. The libopus oracle binaries are optional: tests that need them return without asserting
  when they are absent, unless `KILN_AUDIO_REQUIRE_ORACLE=1` is set (CI sets it where the oracle
  builds).
