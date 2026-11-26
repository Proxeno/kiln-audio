# Test vector fixtures

Oracle vectors for Kiln.Audio will live here once the codec exists.

- Source: the IETF Opus test vectors (`opus_newvectors`, as updated by RFC 8251).
- They are **fetched by `scripts/fetch-test-vectors.sh`** and are not committed to the repository.
  Even where the license permits redistribution, we keep them out of git; if a vector's license
  forbids committing it, it must never be added.
- Expected output is produced by `opus_demo` / libopus, used purely as a black-box oracle.
- Tests that need the vectors must detect their absence and pass without asserting, so a clean
  checkout still passes `dotnet test`.
