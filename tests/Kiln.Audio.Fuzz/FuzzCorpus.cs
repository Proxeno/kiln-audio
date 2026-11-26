using System.Buffers.Binary;
using System.IO;

namespace Kiln.Audio.Fuzz;

/// <summary>
/// The seed packets the mutator starts from, read from the committed RFC 8251
/// <c>testvectorNN.bit</c> files and from raw packet dumps.
/// </summary>
/// <remarks>
/// A <c>.bit</c> file is a sequence of records: a 4-byte big-endian packet length, a 4-byte
/// big-endian encoder final range, and the packet bytes. The harness ignores the range.
/// </remarks>
internal sealed class FuzzCorpus
{
    private const int RecordHeaderSize = 8;

    private readonly List<byte[]> _packets = [];
    private readonly List<string> _sources = [];

    /// <summary>Gets the seed packets, in load order.</summary>
    internal IReadOnlyList<byte[]> Packets => _packets;

    /// <summary>Gets the files the seeds came from, for the run log.</summary>
    internal IReadOnlyList<string> Sources => _sources;

    /// <summary>Loads every record of an <c>opus_demo</c> <c>.bit</c> file.</summary>
    /// <param name="path">Path to the <c>.bit</c> file.</param>
    /// <exception cref="InvalidDataException">A record header or packet is truncated.</exception>
    internal void LoadBitFile(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        int offset = 0;
        while (offset < bytes.Length)
        {
            if (bytes.Length - offset < RecordHeaderSize)
            {
                throw new InvalidDataException(
                    FormattableString.Invariant($"Truncated record header at byte {offset} of '{path}'."));
            }

            int length = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(offset, sizeof(int)));
            offset += RecordHeaderSize;
            if (length < 0 || length > bytes.Length - offset)
            {
                throw new InvalidDataException(
                    FormattableString.Invariant(
                        $"Record at byte {offset - RecordHeaderSize} of '{path}' announces {length} bytes but only {bytes.Length - offset} remain."));
            }

            _packets.Add(bytes.AsSpan(offset, length).ToArray());
            offset += length;
        }

        _sources.Add(path);
    }

    /// <summary>Loads every <c>*.bit</c> file in <paramref name="directory"/>, sorted by name.</summary>
    /// <param name="directory">Directory to scan.</param>
    internal void LoadBitDirectory(string directory)
    {
        foreach (string path in Directory.GetFiles(directory, "*.bit").Order(StringComparer.Ordinal))
        {
            LoadBitFile(path);
        }
    }

    /// <summary>Loads a raw packet file, one packet per file.</summary>
    /// <param name="path">Path to a raw packet file.</param>
    internal void LoadRawFile(string path)
    {
        _packets.Add(File.ReadAllBytes(path));
        _sources.Add(path);
    }
}
