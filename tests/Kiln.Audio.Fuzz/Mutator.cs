namespace Kiln.Audio.Fuzz;

/// <summary>
/// The mutation operators. Every operator is deterministic given the generator state and never
/// throws, so a finding can be chased down from the session seed and execution count.
/// </summary>
internal static class Mutator
{
    /// <summary>Largest packet the mutator produces, in bytes.</summary>
    internal const int MaxPacketBytes = 4096;

    /// <summary>The interesting values for Opus length and frame-count fields.</summary>
    private static readonly byte[] InterestingBytes = [0, 1, 2, 3, 4, 5, 6, 17, 48, 49, 250, 251, 252, 253, 254, 255];

    /// <summary>
    /// Applies one to six random operators to <paramref name="seed"/> and returns the result,
    /// which may be <paramref name="seed"/> itself.
    /// </summary>
    /// <param name="seed">The seed packet; not modified.</param>
    /// <param name="rng">Generator state.</param>
    /// <returns>A packet of 0 to <see cref="MaxPacketBytes"/> bytes.</returns>
    internal static byte[] Mutate(byte[] seed, FuzzRng rng)
    {
        int operators = rng.Next(1, 7);
        byte[] packet = seed;
        for (int i = 0; i < operators; i++)
        {
            packet = Apply(packet, rng, rng.Next(12));
        }

        if (packet.Length > MaxPacketBytes)
        {
            packet = packet.AsSpan(0, MaxPacketBytes).ToArray();
        }

        return packet;
    }

    /// <summary>
    /// Builds a packet from scratch with a random TOC and random bytes, which reaches framing
    /// shapes the test vectors never contain.
    /// </summary>
    /// <param name="rng">Generator state.</param>
    /// <returns>A packet of 0 to 64 bytes, usually starting with a code 3 count byte.</returns>
    internal static byte[] RandomPacket(FuzzRng rng)
    {
        int length = rng.Next(0, 65);
        var packet = new byte[length];
        for (int i = 0; i < length; i++)
        {
            packet[i] = (byte)rng.Next(256);
        }

        if (length > 1 && rng.Chance(1, 2))
        {
            // Force the code 3 layout so the count, padding and VBR paths are reached often.
            packet[0] = (byte)((packet[0] & 0xFC) | 3);
            packet[1] = (byte)(rng.Next(256) & 0x7F);
        }

        return packet;
    }

    private static byte[] Apply(byte[] packet, FuzzRng rng, int operation) => operation switch
    {
        0 => BitFlips(packet, rng, 1 + rng.Next(8)),
        1 => XorBytes(packet, rng, 1 + rng.Next(8)),
        2 => InsertBytes(packet, rng),
        3 => DeleteBytes(packet, rng),
        4 => MutateLengthField(packet, rng),
        5 => SweepToc(packet, rng),
        6 => Truncate(packet, rng),
        7 => DuplicateBlock(packet, rng),
        8 => RandomizeBlock(packet, rng),
        9 => Extend(packet, rng),
        10 => MutateCountByte(packet, rng),
        _ => MutatePadding(packet, rng),
    };

    /// <summary>Flips <paramref name="count"/> individual bits.</summary>
    private static byte[] BitFlips(byte[] packet, FuzzRng rng, int count)
    {
        byte[] result = Copy(packet);
        for (int i = 0; i < count && result.Length > 0; i++)
        {
            int index = rng.Next(result.Length);
            result[index] ^= (byte)(1 << rng.Next(8));
        }

        return result;
    }

    /// <summary>Exclusive-ors random bytes with random 8-bit masks.</summary>
    private static byte[] XorBytes(byte[] packet, FuzzRng rng, int count)
    {
        byte[] result = Copy(packet);
        for (int i = 0; i < count && result.Length > 0; i++)
        {
            int index = rng.Next(result.Length);
            result[index] ^= (byte)(1 + rng.Next(255));
        }

        return result;
    }

    /// <summary>Inserts random bytes at a random offset.</summary>
    private static byte[] InsertBytes(byte[] packet, FuzzRng rng)
    {
        int count = 1 + rng.Next(8);
        int at = rng.Next(packet.Length + 1);
        var result = new byte[Math.Min(packet.Length + count, MaxPacketBytes)];
        if (at > result.Length)
        {
            at = result.Length;
        }

        int tail = Math.Min(count, result.Length - at);
        packet.AsSpan(0, at).CopyTo(result);
        for (int i = 0; i < tail; i++)
        {
            result[at + i] = (byte)rng.Next(256);
        }

        packet.AsSpan(at, Math.Min(packet.Length, result.Length - at - tail))
            .CopyTo(result.AsSpan(at + tail));
        return result;
    }

    /// <summary>Deletes a random run of bytes, keeping the packet non-empty when it was.</summary>
    private static byte[] DeleteBytes(byte[] packet, FuzzRng rng)
    {
        if (packet.Length <= 1)
        {
            return packet;
        }

        int count = Math.Min(1 + rng.Next(8), packet.Length - 1);
        int at = rng.Next(packet.Length - count + 1);
        var result = new byte[packet.Length - count];
        packet.AsSpan(0, at).CopyTo(result);
        packet.AsSpan(at + count).CopyTo(result.AsSpan(at));
        return result;
    }

    /// <summary>Overwrites the length-field area after the TOC with interesting values.</summary>
    private static byte[] MutateLengthField(byte[] packet, FuzzRng rng)
    {
        if (packet.Length == 0)
        {
            return packet;
        }

        byte[] result = Copy(packet);
        int writes = 1 + rng.Next(4);
        for (int i = 0; i < writes; i++)
        {
            int index = 1 + rng.Next(Math.Min(4, Math.Max(1, result.Length - 1)));
            if (index < result.Length)
            {
                result[index] = InterestingBytes[rng.Next(InterestingBytes.Length)];
            }
        }

        return result;
    }

    /// <summary>Replaces the TOC with a random byte, a sweep value, or a random frame-count code.</summary>
    private static byte[] SweepToc(byte[] packet, FuzzRng rng)
    {
        byte[] result = packet.Length == 0 ? new byte[1] : Copy(packet);
        int config = rng.Next(32);
        bool stereo = rng.Chance(1, 2);
        int code = rng.Next(4);
        result[0] = (byte)((config << 3) | (stereo ? 0x04 : 0) | code);
        return result;
    }

    /// <summary>Keeps a random prefix, including the empty packet.</summary>
    private static byte[] Truncate(byte[] packet, FuzzRng rng)
    {
        int length = rng.Next(packet.Length + 1);
        return packet.AsSpan(0, length).ToArray();
    }

    /// <summary>Duplicates a random block elsewhere in the packet.</summary>
    private static byte[] DuplicateBlock(byte[] packet, FuzzRng rng)
    {
        if (packet.Length == 0 || packet.Length >= MaxPacketBytes)
        {
            return packet;
        }

        int length = Math.Min(1 + rng.Next(Math.Min(32, packet.Length)), MaxPacketBytes - packet.Length);
        int from = rng.Next(packet.Length - length + 1);
        int at = rng.Next(packet.Length + 1);
        var result = new byte[packet.Length + length];
        packet.AsSpan(0, at).CopyTo(result);
        packet.AsSpan(from, length).CopyTo(result.AsSpan(at));
        packet.AsSpan(at).CopyTo(result.AsSpan(at + length));
        return result;
    }

    /// <summary>Overwrites a random run with random bytes.</summary>
    private static byte[] RandomizeBlock(byte[] packet, FuzzRng rng)
    {
        if (packet.Length == 0)
        {
            return packet;
        }

        byte[] result = Copy(packet);
        int length = 1 + rng.Next(Math.Min(16, result.Length));
        int at = rng.Next(result.Length - length + 1);
        for (int i = 0; i < length; i++)
        {
            result[at + i] = (byte)rng.Next(256);
        }

        return result;
    }

    /// <summary>Appends random bytes.</summary>
    private static byte[] Extend(byte[] packet, FuzzRng rng)
    {
        int count = 1 + rng.Next(16);
        int room = MaxPacketBytes - packet.Length;
        if (room <= 0)
        {
            return packet;
        }

        count = Math.Min(count, room);
        var result = new byte[packet.Length + count];
        packet.CopyTo(result, 0);
        for (int i = 0; i < count; i++)
        {
            result[packet.Length + i] = (byte)rng.Next(256);
        }

        return result;
    }

    /// <summary>Mutates the code 3 frame-count byte: count, CBR/VBR and padding flags.</summary>
    private static byte[] MutateCountByte(byte[] packet, FuzzRng rng)
    {
        if (packet.Length < 2)
        {
            return packet;
        }

        byte[] result = Copy(packet);
        result[0] = (byte)((result[0] & 0xFC) | 3);
        int count = rng.Next(0, 64);
        result[1] = (byte)(count | (rng.Chance(1, 2) ? 0x80 : 0) | (rng.Chance(1, 2) ? 0x40 : 0));
        return result;
    }

    /// <summary>Writes a padding-length chain after the count byte.</summary>
    private static byte[] MutatePadding(byte[] packet, FuzzRng rng)
    {
        if (packet.Length < 3)
        {
            return packet;
        }

        byte[] result = Copy(packet);
        result[0] = (byte)((result[0] & 0xFC) | 3);
        result[1] = (byte)((result[1] & 0xBF) | 0x40);
        int at = 2;
        int links = 1 + rng.Next(3);
        for (int i = 0; i < links && at < result.Length; i++)
        {
            result[at++] = rng.Chance(1, 2) ? (byte)255 : (byte)rng.Next(256);
        }

        return result;
    }

    private static byte[] Copy(byte[] packet) => (byte[])packet.Clone();
}
