/*
 * Test-only differential probe for the range coder (RFC 6716 Sections 4.1 and 5.1).
 *
 * This program is Kiln.Audio's own code. It is compiled against the pinned libopus
 * sources by scripts/build-libopus-oracle.sh and is never part of the shipped library.
 *
 * Input format (one command per line):
 *   enc <storage>          start an encoder session with <storage> bytes
 *   dec <hex>              start a decoder session over the hexadecimal frame
 *   e <fl> <fh> <ft>       encode / decode a symbol three-tuple
 *   b <fl> <fh> <bits>     encode / decode a power-of-two-total symbol
 *   l <bit> <logp>         encode a bit (decode takes only <logp>)
 *   i <sym> <ftb> <t...>   encode an icdf symbol (decode takes <ftb> and the table)
 *   u <val> <ft>           encode a uint (decode takes <ft>)
 *   r <val> <bits>         encode raw bits (decode takes <bits>)
 *   s <size>               shrink the encoder storage
 *   done                   finalize the encoder / end the decoder session
 *
 * Encoder output: one "state <tell> <tellfrac> <rng> <error>" line per command,
 * then "bytes <hex>" for the whole storage.
 * Decoder output: one "value <v> <tell> <tellfrac> <rng> <error>" line per command.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "entenc.h"
#include "entdec.h"

#define MAX_ICDF 256

static unsigned long next_ul(void)
{
    char *token = strtok(NULL, " \t\r\n");
    return token != NULL ? strtoul(token, NULL, 10) : 0;
}

static int read_icdf(unsigned char *table)
{
    int count = 0;
    char *token;
    while ((token = strtok(NULL, " \t\r\n")) != NULL) {
        if (count < MAX_ICDF) {
            table[count] = (unsigned char)strtoul(token, NULL, 10);
        }
        count++;
    }
    return count;
}

static void print_encoder_state(ec_enc *enc)
{
    printf("state %d %u %u %d\n",
        ec_tell(enc), (unsigned)ec_tell_frac(enc), (unsigned)enc->rng, ec_get_error(enc));
}

static void print_decoded_value(ec_dec *dec, unsigned value)
{
    printf("value %u %d %u %u %d\n",
        value, ec_tell(dec), (unsigned)ec_tell_frac(dec), (unsigned)dec->rng, ec_get_error(dec));
}

static void run_encoder(unsigned storage)
{
    unsigned char *buffer;
    ec_enc enc;
    char line[65536];
    unsigned i;

    buffer = (unsigned char *)calloc(storage > 0 ? storage : 1, 1);
    if (buffer == NULL) {
        fprintf(stderr, "ec_probe: out of memory\n");
        exit(2);
    }

    ec_enc_init(&enc, buffer, storage);
    while (fgets(line, (int)sizeof(line), stdin) != NULL) {
        char *command = strtok(line, " \t\r\n");
        if (command == NULL) {
            continue;
        }

        if (strcmp(command, "done") == 0) {
            ec_enc_done(&enc);
            print_encoder_state(&enc);
            break;
        } else if (strcmp(command, "e") == 0) {
            unsigned fl = (unsigned)next_ul();
            unsigned fh = (unsigned)next_ul();
            unsigned ft = (unsigned)next_ul();
            ec_encode(&enc, fl, fh, ft);
        } else if (strcmp(command, "b") == 0) {
            unsigned fl = (unsigned)next_ul();
            unsigned fh = (unsigned)next_ul();
            unsigned bits = (unsigned)next_ul();
            ec_encode_bin(&enc, fl, fh, bits);
        } else if (strcmp(command, "l") == 0) {
            int bit = (int)next_ul();
            unsigned logp = (unsigned)next_ul();
            ec_enc_bit_logp(&enc, bit, logp);
        } else if (strcmp(command, "i") == 0) {
            int symbol = (int)next_ul();
            unsigned ftb = (unsigned)next_ul();
            unsigned char table[MAX_ICDF];
            read_icdf(table);
            ec_enc_icdf(&enc, symbol, table, ftb);
        } else if (strcmp(command, "u") == 0) {
            opus_uint32 value = (opus_uint32)next_ul();
            opus_uint32 ft = (opus_uint32)next_ul();
            ec_enc_uint(&enc, value, ft);
        } else if (strcmp(command, "r") == 0) {
            opus_uint32 value = (opus_uint32)next_ul();
            unsigned bits = (unsigned)next_ul();
            ec_enc_bits(&enc, value, bits);
        } else if (strcmp(command, "s") == 0) {
            opus_uint32 size = (opus_uint32)next_ul();
            ec_enc_shrink(&enc, size);
            /* The output array is sized to the original storage, but only the new size is
             * meaningful; the C# side compares over the shrunk size. */
            storage = (unsigned)size;
        } else {
            fprintf(stderr, "ec_probe: unknown encoder command '%s'\n", command);
            exit(2);
        }

        print_encoder_state(&enc);
    }

    printf("bytes ");
    for (i = 0; i < storage; i++) {
        printf("%02x", buffer[i]);
    }
    printf("\n");

    free(buffer);
}

static int hex_digit(char c)
{
    if (c >= '0' && c <= '9') {
        return c - '0';
    }
    if (c >= 'a' && c <= 'f') {
        return c - 'a' + 10;
    }
    if (c >= 'A' && c <= 'F') {
        return c - 'A' + 10;
    }
    return 0;
}

static void run_decoder(const char *hex)
{
    unsigned char *buffer;
    ec_dec dec;
    char line[65536];
    size_t length = strlen(hex) / 2;
    size_t i;

    buffer = (unsigned char *)calloc(length > 0 ? length : 1, 1);
    if (buffer == NULL) {
        fprintf(stderr, "ec_probe: out of memory\n");
        exit(2);
    }
    for (i = 0; i < length; i++) {
        buffer[i] = (unsigned char)((hex_digit(hex[2 * i]) << 4) | hex_digit(hex[2 * i + 1]));
    }

    ec_dec_init(&dec, buffer, (opus_uint32)length);
    while (fgets(line, (int)sizeof(line), stdin) != NULL) {
        char *command = strtok(line, " \t\r\n");
        if (command == NULL) {
            continue;
        }

        if (strcmp(command, "done") == 0) {
            break;
        } else if (strcmp(command, "e") == 0) {
            unsigned fl = (unsigned)next_ul();
            unsigned fh = (unsigned)next_ul();
            unsigned ft = (unsigned)next_ul();
            unsigned fs = ec_decode(&dec, ft);
            ec_dec_update(&dec, fl, fh, ft);
            print_decoded_value(&dec, fs);
        } else if (strcmp(command, "b") == 0) {
            unsigned fl = (unsigned)next_ul();
            unsigned fh = (unsigned)next_ul();
            unsigned bits = (unsigned)next_ul();
            unsigned fs = ec_decode_bin(&dec, bits);
            ec_dec_update(&dec, fl, fh, 1u << bits);
            print_decoded_value(&dec, fs);
        } else if (strcmp(command, "l") == 0) {
            unsigned logp = (unsigned)next_ul();
            int bit = ec_dec_bit_logp(&dec, logp);
            print_decoded_value(&dec, (unsigned)bit);
        } else if (strcmp(command, "i") == 0) {
            unsigned ftb = (unsigned)next_ul();
            unsigned char table[MAX_ICDF];
            int symbol;
            read_icdf(table);
            symbol = ec_dec_icdf(&dec, table, ftb);
            print_decoded_value(&dec, (unsigned)symbol);
        } else if (strcmp(command, "u") == 0) {
            opus_uint32 ft = (opus_uint32)next_ul();
            opus_uint32 value = ec_dec_uint(&dec, ft);
            print_decoded_value(&dec, (unsigned)value);
        } else if (strcmp(command, "r") == 0) {
            unsigned bits = (unsigned)next_ul();
            opus_uint32 value = ec_dec_bits(&dec, bits);
            print_decoded_value(&dec, (unsigned)value);
        } else {
            fprintf(stderr, "ec_probe: unknown decoder command '%s'\n", command);
            exit(2);
        }
    }

    free(buffer);
}

int main(void)
{
    char line[65536];
    char *command;

    if (fgets(line, (int)sizeof(line), stdin) == NULL) {
        return 1;
    }

    command = strtok(line, " \t\r\n");
    if (command == NULL) {
        return 1;
    }

    if (strcmp(command, "enc") == 0) {
        run_encoder((unsigned)next_ul());
    } else if (strcmp(command, "dec") == 0) {
        char *hex = strtok(NULL, " \t\r\n");
        run_decoder(hex != NULL ? hex : "");
    } else {
        fprintf(stderr, "ec_probe: expected 'enc' or 'dec', got '%s'\n", command);
        return 2;
    }

    return 0;
}
