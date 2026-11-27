#!/usr/bin/env bash
# Builds and runs the in-process libopus 1.5.2 comparison harness.
#
# bench/native/libopus_bench.c links against the static libopus.a that
# scripts/build-libopus-oracle.sh keeps under .oracle/lib when it is run with
# --keep-lib (or KILN_ORACLE_KEEP_LIB=1). Nothing is written outside .oracle/.
#
# Default (no arguments) decodes the six PERF comparison vectors:
#   01/07/11  CELT, 48 kHz stereo
#   02/03/04  SILK, decoded at the SILK internal rate (8/12/16 kHz), mono like the stream
#
# Usage:
#   scripts/bench-libopus-inproc.sh                 # the six default vectors
#   scripts/bench-libopus-inproc.sh 01 07 11        # selected vectors
#   scripts/bench-libopus-inproc.sh pubapi [passes]  # vectors 01-12, 48 kHz stereo and mono
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

# The CMake cache of the build tree is the authoritative record of how the linked library was
# configured; fail rather than benchmark against a library whose flags cannot be read.
cache="${oracle_root}/build/CMakeCache.txt"
if [[ ! -r "${cache}" ]]; then
    echo "cannot read ${cache}; refusing to benchmark a library with unknown flags" >&2
    exit 1
fi
for key in CMAKE_BUILD_TYPE CMAKE_C_FLAGS_RELEASE OPUS_FIXED_POINT OPUS_HARDENING; do
    line="$(grep -E "^${key}:" "${cache}" || true)"
    if [[ -z "${line}" ]]; then
        echo "${key} is missing from ${cache}; refusing to benchmark" >&2
        exit 1
    fi
    echo "cmake ${line}"
done
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

if [[ "${1:-}" == "pubapi" ]]; then
    passes="${2:-15}"
    for channels in 2 1; do
        for n in 01 02 03 04 05 06 07 08 09 10 11 12; do
            echo -n "vector=${n} "
            "${bench_bin}" decode "${vectors_dir}/testvector${n}.bit" 48000 "${channels}" "${passes}" | grep '^mode='
        done
    done
    exit 0
fi

if [[ $# -eq 0 ]]; then
    run_vector 01 48000 2
    run_vector 07 48000 2
    run_vector 11 48000 2
    # Vectors 02/03/04 are mono (TOC byte bit 2 clear), so decode one channel to match the
    # Kiln benchmark, which uses the TOC channel count.
    run_vector 02 8000 1
    run_vector 03 12000 1
    run_vector 04 16000 1
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
                02) run_vector 02 8000 1 ;;
                03) run_vector 03 12000 1 ;;
                04) run_vector 04 16000 1 ;;
                *)
                    echo "unknown vector '${number}' (expected 01 02 03 04 07 11)" >&2
                    exit 2
                    ;;
            esac
        done
        ;;
esac
