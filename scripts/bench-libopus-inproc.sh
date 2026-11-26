#!/usr/bin/env bash
# Builds and runs the in-process libopus 1.5.2 comparison harness.
#
# bench/native/libopus_bench.c links against the static libopus.a that
# scripts/build-libopus-oracle.sh keeps under .oracle/lib when it is run with
# --keep-lib (or KILN_ORACLE_KEEP_LIB=1). Nothing is written outside .oracle/.
#
# Default (no arguments) decodes the six PERF comparison vectors:
#   01/07/11  CELT, 48 kHz stereo
#   02/03/04  SILK, decoded at the SILK internal rate (8/12/16 kHz) stereo
#
# Usage:
#   scripts/bench-libopus-inproc.sh                 # the six default vectors
#   scripts/bench-libopus-inproc.sh 01 07 11        # selected vectors
#   scripts/bench-libopus-inproc.sh decode <bit> <rate> <channels> [iters]
#   scripts/bench-libopus-inproc.sh encode <pcm> <rate> <channels> \
#       <application> <bitrate> <frame_ms> <complexity> <cbr> [iters]
#
# The libopus build flags and whether its own SIMD was compiled in are printed
# first; that is the reference the Kiln numbers are measured against.
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "${script_dir}/.." && pwd)"
oracle_root="${repo_root}/.oracle"
lib_dir="${oracle_root}/lib"
bin_dir="${oracle_root}/bin"
vectors_dir="${repo_root}/tests/Kiln.Audio.Tests/Fixtures/opus_testvectors"
bench_source="${repo_root}/bench/native/libopus_bench.c"
bench_bin="${bin_dir}/libopus_bench"

if [[ ! -f "${lib_dir}/libopus.a" || ! -d "${lib_dir}/include" ]]; then
    echo "Static libopus is not kept yet; running scripts/build-libopus-oracle.sh --keep-lib."
    bash "${script_dir}/build-libopus-oracle.sh" --keep-lib
fi

# The harness itself is -O2; the measured code is inside the prebuilt static
# libopus, which the oracle build compiled with -O3 -DNDEBUG.
"${CC:-cc}" -O2 -std=c99 -Wall -Wextra \
    -I"${lib_dir}/include" \
    -o "${bench_bin}" "${bench_source}" "${lib_dir}/libopus.a" -lm

echo "== libopus reference build =="
cat "${lib_dir}/flags.txt"
if grep -q 'OPUS_ARM_MAY_HAVE_NEON_INTR' "${lib_dir}/flags.txt"; then
    echo "simd=arm64 NEON intrinsics enabled (compiled into the static library)"
elif grep -q 'OPUS_X86_MAY_HAVE_SSE' "${lib_dir}/flags.txt"; then
    echo "simd=x86 SSE/AVX intrinsics enabled (compiled into the static library)"
else
    echo "simd=none detected in the recorded flags"
fi
echo

run_vector() {
    local number="$1"
    local rate="$2"
    local channels="$3"
    local file="${vectors_dir}/testvector${number}.bit"

    if [[ ! -f "${file}" ]]; then
        echo "missing ${file}; run scripts/fetch-test-vectors.sh" >&2
        exit 1
    fi
    "${bench_bin}" decode "${file}" "${rate}" "${channels}"
}

if [[ $# -eq 0 ]]; then
    run_vector 01 48000 2
    run_vector 07 48000 2
    run_vector 11 48000 2
    run_vector 02 8000 2
    run_vector 03 12000 2
    run_vector 04 16000 2
    exit 0
fi

case "$1" in
    decode | encode)
        "${bench_bin}" "$@"
        ;;
    *)
        for number in "$@"; do
            case "${number}" in
                01 | 07 | 11) run_vector "${number}" 48000 2 ;;
                02) run_vector 02 8000 2 ;;
                03) run_vector 03 12000 2 ;;
                04) run_vector 04 16000 2 ;;
                *)
                    echo "unknown vector '${number}' (expected 01 02 03 04 07 11)" >&2
                    exit 2
                    ;;
            esac
        done
        ;;
esac
