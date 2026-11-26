#!/usr/bin/env bash
# Times libopus 1.5.2 encoding the encoder corpus, for the ENC-1 performance report.
#
# The measurement includes process start-up and reading the whole PCM file, exactly as
# `opus_demo -e` is invoked. It runs the encode five times and keeps the fastest wall time.
#
# Usage: bench-libopus-encode.sh <mono-48k.pcm> <bitrate-bps> <framesize-ms> <complexity>
# The oracle must have been built by scripts/build-libopus-oracle.sh.
set -euo pipefail

if [[ $# -ne 4 ]]; then
    echo "usage: $0 <mono-48k.pcm> <bitrate-bps> <framesize-ms> <complexity>" >&2
    exit 2
fi

pcm="$1"
bps="$2"
framesize="$3"
complexity="$4"
reps=5

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "${script_dir}/.." && pwd)"
opus_demo="${repo_root}/.oracle/bin/opus_demo"

if [[ ! -x "${opus_demo}" ]]; then
    echo "opus_demo not found; run scripts/build-libopus-oracle.sh" >&2
    exit 1
fi

if [[ ! -f "${pcm}" ]]; then
    echo "PCM file not found: ${pcm}" >&2
    exit 1
fi

tmp_dir="$(mktemp -d "${TMPDIR:-/tmp}/kiln-libopus-encode.XXXXXX")"
trap 'rm -rf "${tmp_dir}"' EXIT

python3 -I - "${opus_demo}" "${pcm}" "${bps}" "${framesize}" "${complexity}" "${reps}" "${tmp_dir}" <<'PY'
import subprocess
import sys
import time

opus_demo, pcm, bps, framesize, complexity, reps, tmp_dir = sys.argv[1:]
best = None
for i in range(int(reps)):
    out = f"{tmp_dir}/out{i}.bit"
    start = time.perf_counter()
    subprocess.run(
        [
            opus_demo,
            "-e", "restricted-lowdelay", "48000", "1", bps,
            "-framesize", framesize, "-cbr", "-complexity", complexity,
            pcm, out,
        ],
        check=True,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
    )
    elapsed = time.perf_counter() - start
    best = elapsed if best is None else min(best, elapsed)

samples = 0
with open(pcm, "rb") as handle:
    handle.seek(0, 2)
    samples = handle.tell() // 2
seconds = best
audio = samples / 48000.0
print(f"libopus encode: bps={bps} framesize={framesize}ms complexity={complexity} "
      f"best={seconds:.3f}s audio={audio:.3f}s realtime={audio/seconds:.1f}x")
PY
