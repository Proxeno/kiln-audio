/*
 * libopus_bench: an in-process timing harness for the libopus 1.5.2 reference.
 *
 * This is our own small C program; it links directly against the static
 * libopus.a that scripts/build-libopus-oracle.sh keeps under .oracle/lib (run
 * that script with --keep-lib). It exists so the Kiln decode benchmarks can be
 * compared against libopus with the same in-memory loop, no file IO, no process
 * startup and no shell timing noise.
 *
 * Modes:
 *   decode <file.bit> <rate> <channels> [iterations]
 *   encode <file.pcm> <rate> <channels> <application> <bitrate> \
 *          <frame_ms> <complexity> <cbr> [iterations]
 *
 * The <file.bit> format is the RFC 8251 test-vector layout: for every packet an
 * 8-byte header (a 32-bit big-endian length followed by a 32-bit big-endian
 * encoder final range), then the packet bytes. <file.pcm> is interleaved
 * signed 16-bit little-endian PCM.
 *
 * Only opus_decode or opus_encode is inside the timed region. Each iteration
 * decodes or encodes the whole input, and the best of the timed iterations is
 * reported, together with microseconds per Opus frame and how many times faster
 * than real time the codec ran.
 */

#include <errno.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

#include <opus.h>

#define MAX_PACKET_BYTES 1500
#define DEFAULT_ITERATIONS 5

struct packet {
    unsigned char *data;
    int length;
};

static void fail(const char *message)
{
    fprintf(stderr, "libopus_bench: %s\n", message);
    exit(1);
}

static void fail_file(const char *path)
{
    fprintf(stderr, "libopus_bench: %s: %s\n", path, strerror(errno));
    exit(1);
}

static unsigned char *read_file(const char *path, long *size_out)
{
    FILE *file = fopen(path, "rb");
    unsigned char *bytes;
    long size;

    if (file == NULL) {
        fail_file(path);
    }
    if (fseek(file, 0, SEEK_END) != 0) {
        fail_file(path);
    }
    size = ftell(file);
    if (size < 0) {
        fail_file(path);
    }
    rewind(file);

    bytes = (unsigned char *)malloc((size_t)size + 1u);
    if (bytes == NULL) {
        fail("out of memory reading input");
    }
    if (size > 0 && fread(bytes, 1u, (size_t)size, file) != (size_t)size) {
        fail_file(path);
    }
    fclose(file);

    *size_out = size;
    return bytes;
}

static uint32_t read_u32_be(const unsigned char *p)
{
    return ((uint32_t)p[0] << 24) | ((uint32_t)p[1] << 16) |
           ((uint32_t)p[2] << 8) | (uint32_t)p[3];
}

static struct packet *read_bit_file(const char *path, int *count_out)
{
    long size;
    unsigned char *bytes = read_file(path, &size);
    long offset = 0;
    int capacity = 1024;
    int count = 0;
    struct packet *packets = (struct packet *)malloc(sizeof(struct packet) * (size_t)capacity);

    if (packets == NULL) {
        fail("out of memory reading packets");
    }

    while (offset < size) {
        uint32_t length;

        if (size - offset < 8) {
            fail("truncated record header in .bit file");
        }
        length = read_u32_be(bytes + offset);
        offset += 8;
        if (length > (uint32_t)(size - offset)) {
            fail("record length runs past the end of the .bit file");
        }
        if (count == capacity) {
            struct packet *grown;

            capacity *= 2;
            grown = (struct packet *)realloc(packets, sizeof(struct packet) * (size_t)capacity);
            if (grown == NULL) {
                fail("out of memory growing the packet table");
            }
            packets = grown;
        }
        packets[count].data = bytes + offset;
        packets[count].length = (int)length;
        count++;
        offset += (long)length;
    }

    *count_out = count;
    return packets;
}

static double now_seconds(void)
{
    struct timespec ts;

    if (clock_gettime(CLOCK_MONOTONIC, &ts) != 0) {
        fail("clock_gettime(CLOCK_MONOTONIC) failed");
    }
    return (double)ts.tv_sec + (double)ts.tv_nsec * 1e-9;
}

static int parse_application(const char *name)
{
    if (strcmp(name, "voip") == 0) {
        return OPUS_APPLICATION_VOIP;
    }
    if (strcmp(name, "audio") == 0) {
        return OPUS_APPLICATION_AUDIO;
    }
    if (strcmp(name, "restricted-lowdelay") == 0 || strcmp(name, "lowdelay") == 0) {
        return OPUS_APPLICATION_RESTRICTED_LOWDELAY;
    }
    fprintf(stderr, "libopus_bench: unknown application '%s'\n", name);
    exit(2);
}

static void print_common(const char *mode, const char *path, int rate, int channels,
                         long frames, double audio_seconds, double best_seconds,
                         int iterations)
{
    printf("mode=%s file=%s rate=%d channels=%d frames=%ld audio_s=%.6f "
           "best_s=%.9f us_per_frame=%.3f realtime=%.1f iterations=%d\n",
           mode, path, rate, channels, frames, audio_seconds,
           best_seconds,
           frames > 0 ? best_seconds * 1e6 / (double)frames : 0.0,
           best_seconds > 0.0 ? audio_seconds / best_seconds : 0.0,
           iterations);
}

static int run_decode(const char *path, int rate, int channels, int iterations)
{
    int packet_count = 0;
    struct packet *packets = read_bit_file(path, &packet_count);
    int max_frame = rate * 120 / 1000; /* 120 ms, the largest Opus frame */
    opus_int16 *pcm = (opus_int16 *)malloc(sizeof(opus_int16) * (size_t)max_frame * (size_t)channels);
    long total_samples = 0; /* per channel, one pass */
    long total_frames = 0;
    double best = 0.0;
    int iteration;

    if (pcm == NULL) {
        fail("out of memory allocating PCM");
    }
    if (packet_count == 0) {
        fail("the .bit file contains no packets");
    }

    /* Count Opus frames once; decode is one call per packet. */
    for (iteration = 0; iteration < packet_count; iteration++) {
        int frames = opus_packet_get_nb_frames(packets[iteration].data, packets[iteration].length);

        if (frames < 0) {
            fail("opus_packet_get_nb_frames rejected a packet");
        }
        total_frames += frames;
    }

    for (iteration = 0; iteration < iterations; iteration++) {
        int error = OPUS_OK;
        OpusDecoder *decoder = opus_decoder_create(rate, channels, &error);
        long samples = 0;
        double start;
        double elapsed;
        int index;

        if (error != OPUS_OK || decoder == NULL) {
            fail("opus_decoder_create failed");
        }

        start = now_seconds();
        for (index = 0; index < packet_count; index++) {
            int decoded = opus_decode(decoder, packets[index].data, packets[index].length,
                                      pcm, max_frame, 0);

            if (decoded < 0) {
                fail("opus_decode failed");
            }
            samples += decoded;
        }
        elapsed = now_seconds() - start;
        opus_decoder_destroy(decoder);

        if (iteration == 0 || elapsed < best) {
            best = elapsed;
        }
        total_samples = samples;
    }

    print_common("decode", path, rate, channels, total_frames,
                 (double)total_samples / (double)rate, best, iterations);
    return 0;
}

static int run_encode(const char *path, int rate, int channels, const char *application_name,
                      int bitrate, double frame_ms, int complexity, int cbr, int iterations)
{
    long byte_count = 0;
    unsigned char *bytes = read_file(path, &byte_count);
    const opus_int16 *pcm = (const opus_int16 *)bytes;
    long total_samples_interleaved = byte_count / (long)sizeof(opus_int16);
    int frame_size = (int)((double)rate * frame_ms / 1000.0 + 0.5);
    long frame_samples = (long)frame_size * (long)channels;
    int application = parse_application(application_name);
    unsigned char *encoded;
    long frames;
    double best = 0.0;
    long toc_count[3] = {0, 0, 0};
    int iteration;

    if (frame_size <= 0 || frame_samples <= 0) {
        fail("invalid frame size");
    }
    if (total_samples_interleaved < frame_samples) {
        fail("the PCM file is shorter than one frame");
    }
    frames = total_samples_interleaved / frame_samples;
    encoded = (unsigned char *)malloc(MAX_PACKET_BYTES);
    if (encoded == NULL) {
        fail("out of memory allocating the encoded packet");
    }

    for (iteration = 0; iteration < iterations; iteration++) {
        int error = OPUS_OK;
        OpusEncoder *encoder = opus_encoder_create(rate, channels, application, &error);
        double start;
        double elapsed;
        long frame;

        if (error != OPUS_OK || encoder == NULL) {
            fail("opus_encoder_create failed");
        }
        /* Optional pins, to compare like with like: KILN_BENCH_BANDWIDTH = NB|MB|WB|SWB|FB sets the
           bandwidth; KILN_BENCH_FORCE_MODE = 1000 (SILK) | 1001 (hybrid) | 1002 (CELT) forces the
           mode (OPUS_SET_FORCE_MODE, opus_private.h request 11002). */
        {
            const char *bandwidth = getenv("KILN_BENCH_BANDWIDTH");
            const char *force_mode = getenv("KILN_BENCH_FORCE_MODE");

            if (bandwidth != NULL) {
                int value = strcmp(bandwidth, "NB") == 0 ? OPUS_BANDWIDTH_NARROWBAND
                          : strcmp(bandwidth, "MB") == 0 ? OPUS_BANDWIDTH_MEDIUMBAND
                          : strcmp(bandwidth, "WB") == 0 ? OPUS_BANDWIDTH_WIDEBAND
                          : strcmp(bandwidth, "SWB") == 0 ? OPUS_BANDWIDTH_SUPERWIDEBAND
                          : OPUS_BANDWIDTH_FULLBAND;
                opus_encoder_ctl(encoder, OPUS_SET_BANDWIDTH(value));
            }
            if (force_mode != NULL) {
                opus_encoder_ctl(encoder, 11002, atoi(force_mode));
            }
        }
        if (opus_encoder_ctl(encoder, OPUS_SET_BITRATE(bitrate)) != OPUS_OK ||
            opus_encoder_ctl(encoder, OPUS_SET_COMPLEXITY(complexity)) != OPUS_OK ||
            opus_encoder_ctl(encoder, OPUS_SET_VBR(cbr ? 0 : 1)) != OPUS_OK) {
            fail("opus_encoder_ctl failed");
        }

        start = now_seconds();
        for (frame = 0; frame < frames; frame++) {
            int produced = opus_encode(encoder,
                                       pcm + frame * frame_samples,
                                       frame_size, encoded, MAX_PACKET_BYTES);

            if (produced < 0) {
                fail("opus_encode failed");
            }
            toc_count[(encoded[0] >> 3) < 12 ? 0 : (encoded[0] >> 3) < 16 ? 1 : 2]++;
        }
        elapsed = now_seconds() - start;
        opus_encoder_destroy(encoder);

        if (iteration == 0 || elapsed < best) {
            best = elapsed;
        }
    }

    printf("packet modes (all passes): silk=%ld hybrid=%ld celt=%ld\n", toc_count[0], toc_count[1], toc_count[2]);
    print_common("encode", path, rate, channels, frames,
                 (double)frames * (double)frame_size / (double)rate, best, iterations);
    return 0;
}

static void usage(void)
{
    fprintf(stderr,
            "usage:\n"
            "  libopus_bench decode <file.bit> <rate> <channels> [iterations]\n"
            "  libopus_bench encode <file.pcm> <rate> <channels> <application> "
            "<bitrate> <frame_ms> <complexity> <cbr> [iterations]\n"
            "  application: voip | audio | restricted-lowdelay\n");
}

int main(int argc, char **argv)
{
    if (argc < 2) {
        usage();
        return 2;
    }

    if (strcmp(argv[1], "decode") == 0) {
        int iterations = DEFAULT_ITERATIONS;

        if (argc < 5 || argc > 6) {
            usage();
            return 2;
        }
        if (argc == 6) {
            iterations = atoi(argv[5]);
        }
        if (iterations <= 0) {
            fail("iterations must be positive");
        }
        printf("libopus %s\n", opus_get_version_string());
        return run_decode(argv[2], atoi(argv[3]), atoi(argv[4]), iterations);
    }

    if (strcmp(argv[1], "encode") == 0) {
        int iterations = DEFAULT_ITERATIONS;

        if (argc < 10 || argc > 11) {
            usage();
            return 2;
        }
        if (argc == 11) {
            iterations = atoi(argv[10]);
        }
        if (iterations <= 0) {
            fail("iterations must be positive");
        }
        printf("libopus %s\n", opus_get_version_string());
        return run_encode(argv[2], atoi(argv[3]), atoi(argv[4]), argv[5],
                          atoi(argv[6]), atof(argv[7]), atoi(argv[8]),
                          atoi(argv[9]), iterations);
    }

    usage();
    return 2;
}
