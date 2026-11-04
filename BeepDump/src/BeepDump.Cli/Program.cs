using BeepDump.Core;

Messages.English = true;
const string Usage = """
beepdump - rebuild a PC-E500 series ROM from a recording of its buzzer

Usage:
  beepdump decode [options] <recording>...   Read recordings and write the ROM
  beepdump listing [--basic]                 Print the program to type in
  beepdump --help | --version

Options for decode:
  -o, --output FILE   ROM file to write (default: beepdump.rom)
  -m, --model MODEL   e500: main ROM, 256KB (default; PC-E500, PC-E500PJ,
                            PC-E550, PC-1480U, PC-1490UII)
                      e650: extension + main ROM, 384KB (PC-E650, PC-U6000)
      --single        Use only the standard analysis pattern (faster)
      --trace FILE    Write the tone readings of the BASIC version to FILE
  -q, --quiet         Do not show progress

Options for listing:
      --basic         The BASIC version (default: the machine-code version)

Recordings: WAV is always readable. Other formats (MOV, MP4, M4A, ...) use Media
Foundation on Windows and need ffmpeg on the PATH on Linux and macOS.

Several recordings fill each other's gaps. Exit code: 0 = every block was read,
1 = some are missing (the ROM is still written, with FFh there), 2 = error.
""";

if (args.Length == 0 || args[0] is "-h" or "--help" or "help") { Console.WriteLine(Usage); return args.Length == 0 ? 2 : 0; }
if (args[0] == "--version") { Console.WriteLine($"beepdump {typeof(Decoder).Assembly.GetName().Version?.ToString(3)}"); return 0; }
try
{
    return args[0] switch
    {
        "decode" => Decode(args[1..]),
        "listing" => Listing(args[1..]),
        _ => Fail($"Unknown command: {args[0]}"),
    };
}
catch (Exception e) when (e is IOException or UnauthorizedAccessException or OutOfMemoryException)
{
    return Fail(e.Message);
}

static int Fail(string message)
{
    Console.Error.WriteLine($"beepdump: {message}");
    Console.Error.WriteLine("Run 'beepdump --help' for usage.");
    return 2;
}

static int Listing(string[] args)
{
    bool basic = false;
    foreach (string a in args)
    {
        if (a == "--basic") basic = true;
        else return Fail($"Unknown option: {a}");
    }
    Console.WriteLine((basic ? FastCode.BasicListing : FastCode.MachineListing).ReplaceLineEndings());
    return 0;
}

static int Decode(string[] args)
{
    var files = new List<string>();
    string output = "beepdump.rom";
    string? tracePath = null;
    var model = RomModel.Main256;
    bool single = false, quiet = false;
    for (int i = 0; i < args.Length; i++)
    {
        string a = args[i];
        string Next() => ++i < args.Length ? args[i] : throw new IOException($"{a} needs a value.");
        switch (a)
        {
            case "-o" or "--output": output = Next(); break;
            case "--trace": tracePath = Next(); break;
            case "--single": single = true; break;
            case "-q" or "--quiet": quiet = true; break;
            case "-m" or "--model":
                string name = Next().ToLowerInvariant();
                model = name switch { "e500" => RomModel.Main256, "e650" => RomModel.Main384, _ => throw new IOException($"Unknown model: {name} (use e500 or e650).") };
                break;
            default:
                if (a.StartsWith('-')) return Fail($"Unknown option: {a}");
                files.Add(a);
                break;
        }
    }
    if (files.Count == 0) return Fail("No recordings were given.");

    var image = new RomImage();
    double knownScale = 0;
    using var trace = tracePath == null ? null : new StreamWriter(tracePath);
    foreach (string file in files)
    {
        string lastText = "";
        void Show(string text, double fraction)
        {
            if (quiet || Console.IsErrorRedirected) return;
            string line = $"\r{Path.GetFileName(file)}: {text} {fraction * 100,3:F0}%";
            Console.Error.Write(line.PadRight(Math.Max(line.Length, lastText.Length)));
            lastText = line;
        }
        var audio = AudioFile.Load(file, f => Show("Loading audio", f));
        var result = single
            ? Decoder.Decode(Analyzer.Prepare(audio, Analyzer.Patterns[0]), audio.Rate, Show, trace == null ? null : trace.WriteLine, knownScale)
            : Analyzer.Analyze(audio, Show, trace == null ? null : trace.WriteLine, knownScale);
        if (lastText.Length > 0) Console.Error.Write("\r" + new string(' ', lastText.Length) + "\r");
        image.Add(result.Blocks);
        if (result.Blocks.Any(b => b.State == BlockState.Good)) knownScale = result.Scale;
        int repaired = result.Blocks.Count(b => b.State == BlockState.Repaired), failed = result.Blocks.Count(b => b.State == BlockState.Failed);
        Console.WriteLine($"{Path.GetFileName(file)}: {audio.Seconds:F1} s, {result.Blocks.Count - repaired - failed} blocks read, {repaired} repaired, {failed} failed");
        foreach (string note in result.Notes) Console.WriteLine("  " + note);
        foreach (var b in result.Blocks.Where(b => b.State == BlockState.Failed)) Console.WriteLine($"  failed: {b.Address:X5}");
    }
    foreach (int a in image.Conflicts) Console.WriteLine($"Recordings disagree at {a:X5}: record it once more to decide.");

    File.WriteAllBytes(output, image.Build(model));
    var missing = image.Missing(model);
    Console.WriteLine($"Wrote {output} ({model.Size} bytes).");
    if (missing.Count == 0) { Console.WriteLine("Every block was read."); return 0; }
    Console.WriteLine("Missing, filled with FFh. Record these ranges (answers to the two ? prompts):");
    foreach (var (start, end) in missing) Console.WriteLine($"  &H{start:X5}  &H{end:X5}");
    return 1;
}
