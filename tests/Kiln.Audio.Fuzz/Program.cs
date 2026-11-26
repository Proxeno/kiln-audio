using System.Globalization;
using System.IO;

namespace Kiln.Audio.Fuzz;

/// <summary>Command-line entry point for the standalone fuzz sessions.</summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            PrintUsage();
            return 2;
        }
    }

    private static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 2;
        }

        var corpus = new FuzzCorpus();
        string? outputDirectory = null;
        ulong seed = 1;
        double seconds = 60;
        long executions = long.MaxValue;
        bool includeEncoder = true;
        bool checkAllocations = true;
        bool replayCorpus = true;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--corpus":
                    AddCorpus(corpus, Next(args, ref i, "--corpus"));
                    break;
                case "--seconds":
                    seconds = double.Parse(Next(args, ref i, "--seconds"), CultureInfo.InvariantCulture);
                    break;
                case "--executions":
                    executions = long.Parse(Next(args, ref i, "--executions"), CultureInfo.InvariantCulture);
                    break;
                case "--seed":
                    seed = ulong.Parse(Next(args, ref i, "--seed"), CultureInfo.InvariantCulture);
                    break;
                case "--out":
                    outputDirectory = Next(args, ref i, "--out");
                    break;
                case "--no-encoder":
                    includeEncoder = false;
                    break;
                case "--no-alloc-check":
                    checkAllocations = false;
                    break;
                case "--no-replay":
                    replayCorpus = false;
                    break;
                case "--repro":
                    return Repro(args, ref i);
                case "--help":
                case "-h":
                    PrintUsage();
                    return 0;
                default:
                    throw new ArgumentException(FormattableString.Invariant($"Unknown argument '{args[i]}'."));
            }
        }

        if (corpus.Packets.Count == 0)
        {
            throw new ArgumentException("No corpus was loaded; pass --corpus <dir-or-.bit-file>.");
        }

        var options = new FuzzOptions
        {
            Seed = seed,
            MaxExecutions = executions,
            MaxDuration = TimeSpan.FromSeconds(seconds),
            OutputDirectory = outputDirectory,
            IncludeEncoder = includeEncoder,
            CheckAllocations = checkAllocations,
            ReplayCorpus = replayCorpus,
            Log = Console.WriteLine,
        };

        Console.WriteLine(FormattableString.Invariant(
            $"corpus: {corpus.Packets.Count} packets from {corpus.Sources.Count} files; seed {seed}; {seconds:0} s limit"));
        FuzzReport report = FuzzEngine.Run(corpus, options);
        Console.WriteLine(FormattableString.Invariant(
            $"execs {report.Executions} ({report.Mutations} mutations; parser {report.ParserExecutions}, decoder {report.DecoderExecutions}, encoder {report.EncoderExecutions}); {report.Executions / Math.Max(1, report.Elapsed.TotalSeconds):0} execs/s; {report.Seeds} seeds; {report.AllocationChecks} allocation checks; {report.Findings.Count} findings"));

        foreach (FuzzFinding finding in report.Findings)
        {
            Console.WriteLine(FormattableString.Invariant(
                $"  {finding.Target}: {finding.Message} [{finding.Packet.Length} bytes, rate {finding.SampleRate}, {finding.Channels} ch, fec {finding.DecodeFec}, float {finding.UseFloat}]"));
        }

        return report.Findings.Count == 0 ? 0 : 1;
    }

    private static int Repro(string[] args, ref int index)
    {
        string target = Next(args, ref index, "--repro");
        string path = Next(args, ref index, "--repro");
        byte[] packet = File.ReadAllBytes(path);
        switch (target.ToUpperInvariant())
        {
            case "PARSER":
            {
                bool failed = FuzzEngine.ReproducesParser(packet);
                Console.WriteLine(failed ? "reproduced" : "did not reproduce");
                return failed ? 1 : 0;
            }

            case "DECODER":
            {
                int rate = index + 1 < args.Length ? int.Parse(args[++index], CultureInfo.InvariantCulture) : 48000;
                int channels = index + 1 < args.Length ? int.Parse(args[++index], CultureInfo.InvariantCulture) : 1;
                bool fec = index + 1 < args.Length && bool.Parse(args[++index]);
                bool useFloat = index + 1 >= args.Length || bool.Parse(args[++index]);
                return Report(() => DecoderTarget.Trace(packet, rate, channels, fec, useFloat));
            }

            case "SEQUENCE":
                return Report(() => DecoderTarget.TraceSequence(FuzzSequenceFile.Read(path)));

            default:
                throw new ArgumentException(FormattableString.Invariant($"Unknown repro target '{target}'; use parser, decoder or sequence."));
        }
    }

    private static int Report(Action action)
    {
        try
        {
            action();
            Console.WriteLine("did not reproduce");
            return 0;
        }
        catch (FuzzFailureException failure)
        {
            Console.WriteLine(failure);
            return 1;
        }
    }

    private static void AddCorpus(FuzzCorpus corpus, string path)
    {
        if (Directory.Exists(path))
        {
            corpus.LoadBitDirectory(path);
        }
        else if (File.Exists(path))
        {
            if (path.EndsWith(".bit", StringComparison.OrdinalIgnoreCase))
            {
                corpus.LoadBitFile(path);
            }
            else
            {
                corpus.LoadRawFile(path);
            }
        }
        else
        {
            throw new ArgumentException(FormattableString.Invariant($"Corpus path '{path}' does not exist."));
        }
    }

    private static string Next(string[] args, ref int index, string option)
    {
        index++;
        if (index >= args.Length)
        {
            throw new ArgumentException(FormattableString.Invariant($"{option} needs a value."));
        }

        return args[index];
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine(
            "Usage: Kiln.Audio.Fuzz --corpus <dir-or-file> [--seconds N] [--executions N] [--seed N]\n"
            + "       [--out DIR] [--no-encoder] [--no-alloc-check] [--no-replay]\n"
            + "       --repro parser <file>\n"
            + "       --repro decoder <file> [rate channels fec float]\n"
            + "       --repro sequence <file.seq>\n"
            + "\n"
            + "The fuzzer is single-threaded and deterministic: the same seed and corpus replay\n"
            + "the same sequence of mutations.");
    }
}
