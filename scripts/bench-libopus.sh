#!/usr/bin/env bash
# Times the pinned libopus 1.5.2 oracle on the RFC 8251 vectors used by the Kiln decode
# benchmarks, so docs/performance.md can compare like with like.
#
# Each decode is run five times and the minimum wall time is reported. File IO (reading the
# .bit packets) and process startup are part of the measured time; the PCM destination is
# /dev/null, so writing the output is nearly free.
#
# For the SILK-only vectors the stream is also decoded at the SILK internal rate, which is
# the rate the Kiln SILK benchmarks decode at.
#
# Build the oracle first with scripts/build-libopus-oracle.sh.
set -euo pipefail

runs=5

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "${script_dir}/.." && pwd)"
opus="${repo_root}/.oracle/bin/opus_demo"
vectors="${repo_root}/tests/Kiln.Audio.Tests/Fixtures/opus_testvectors"
cache="${repo_root}/.oracle/build/CMakeCache.txt"

if [[ ! -x "${opus}" ]]; then
    echo "opus_demo not found at ${opus}; run scripts/build-libopus-oracle.sh first." >&2
    exit 1
fi

tmp_dir="$(mktemp -d "${TMPDIR:-/tmp}/kiln-bench-libopus.XXXXXX")"
trap 'rm -rf "${tmp_dir}"' EXIT

TIMEFORMAT='%R'

# Prints the minimum wall-clock seconds of five runs of the given command.
min_time() {
    local min=""
    local i t
    for ((i = 0; i < runs; i++)); do
        t="$( { time "$@" >/dev/null 2>&1; } 2>&1 )"
        if [[ -z "${min}" ]] || awk -v a="${t}" -v b="${min}" 'BEGIN { exit !(a < b) }'; then
            min="${t}"
        fi
    done
    printf '%s' "${min}"
}

# Computes the audio duration in seconds from the 48 kHz stereo decode of a vector.
audio_seconds() {
    local bit_file="$1"
    local pcm="${tmp_dir}/duration.raw"
    "${opus}" -d 48000 2 "${bit_file}" "${pcm}" >/dev/null 2>&1
    local bytes
    bytes="$(wc -c < "${pcm}" | tr -d '[:space:]')"
    rm -f "${pcm}"
    awk -v b="${bytes}" 'BEGIN { printf "%.6f", b / 4 / 48000 }'
}

ratio() {
    awk -v d="$1" -v t="$2" 'BEGIN { if (t > 0) printf "%.1f", d / t; else printf "n/a" }'
}

if [[ -f "${cache}" ]]; then
    compiler="$(grep '^CMAKE_C_COMPILER:' "${cache}" | cut -d= -f2- || true)"
    cflags="$(grep '^CMAKE_C_FLAGS:' "${cache}" | cut -d= -f2- || true)"
    release_flags="$(grep '^CMAKE_C_FLAGS_RELEASE:' "${cache}" | cut -d= -f2- || true)"
    {
        echo "libopus 1.5.2, built by scripts/build-libopus-oracle.sh"
        echo "  cmake -DCMAKE_BUILD_TYPE=Release -DOPUS_BUILD_PROGRAMS=ON -DOPUS_BUILD_TESTING=OFF -DOPUS_BUILD_SHARED_LIBRARY=OFF"
        echo "  compiler: ${compiler}"
        echo "  CMAKE_C_FLAGS='${cflags}' CMAKE_C_FLAGS_RELEASE='${release_flags}'"
        echo
    }
fi

# Process startup baseline: opus_demo with no arguments prints usage and exits, so this is
# the floor that every timed decode includes.
startup_min=""
for ((i = 0; i < runs; i++)); do
    t="$( { time "${opus}" >/dev/null 2>&1; } 2>&1 )" || true
    if [[ -n "${t}" ]] && { [[ -z "${startup_min}" ]] \
        || awk -v a="${t}" -v b="${startup_min}" 'BEGIN { exit !(a < b) }'; }; then
        startup_min="${t}"
    fi
done

echo "Runs per case: ${runs} (minimum wall time; .bit file read and process startup included)"
echo "Process startup floor (opus_demo with no arguments): ${startup_min} s"
echo
printf '| Vector | Decode | Min time | Audio | Faster than real time |\n'
printf '| ------ | ------ | -------- | ----- | --------------------- |\n'

for vector in 01 02 03 04 07 11; do
    bit="${vectors}/testvector${vector}.bit"
    if [[ ! -f "${bit}" ]]; then
        echo "missing ${bit}; run scripts/fetch-test-vectors.sh first." >&2
        exit 1
    fi

    duration="$(audio_seconds "${bit}")"

    seconds="$(min_time "${opus}" -d 48000 2 "${bit}" /dev/null)"
    printf '| %s | 48 kHz stereo | %.1f ms | %.2f s | %sx |\n' \
        "${vector}" "$(awk -v t="${seconds}" 'BEGIN { print t * 1000 }')" \
        "${duration}" "$(ratio "${duration}" "${seconds}")"

    case "${vector}" in
        02) rate=8000 ;;
        03) rate=12000 ;;
        04) rate=16000 ;;
        *) rate="" ;;
    esac

    if [[ -n "${rate}" ]]; then
        seconds="$(min_time "${opus}" -d "${rate}" 2 "${bit}" /dev/null)"
        printf '| %s | %s Hz stereo | %.1f ms | %.2f s | %sx |\n' \
            "${vector}" "${rate}" "$(awk -v t="${seconds}" 'BEGIN { print t * 1000 }')" \
            "${duration}" "$(ratio "${duration}" "${seconds}")"
    fi
done
