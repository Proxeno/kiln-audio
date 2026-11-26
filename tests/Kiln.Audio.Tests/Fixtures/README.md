# Test vector fixtures

Oracle vectors for the Opus conformance tests live here.

- Source: the IETF Opus test vectors (`opus_newvectors`, as updated by RFC 8251).
- They are **fetched by `scripts/fetch-test-vectors.sh`** and are not committed to the repository
  (the extracted directory is in `.gitignore`). Even where the license permits redistribution, we
  keep them out of git; if a vector's license forbids committing it, it must never be added.
- The script pins the archive's SHA-256 and is idempotent. It writes:
  `testvectorNN.bit`, `testvectorNN.dec`, and `testvectorNNm.dec` for `NN` = 01 to 12.
- Expected output is produced by `opus_demo` / libopus, used purely as a black-box oracle. Build
  the pinned tools with `scripts/build-libopus-oracle.sh`; they land in `.oracle/bin` (git-ignored).
- The managed port of the RFC 6716 §6 / RFC 8251 quality metric scores the vectors in
  `tests/Kiln.Audio.Tests/Oracle/OpusCompare.cs`.
- Tests that need the vectors or the oracle must detect their absence and pass without asserting,
  so a clean checkout still passes `dotnet test`. Set `KILN_AUDIO_REQUIRE_ORACLE=1` to make absence
  a failure instead; CI sets it where the oracle builds.
