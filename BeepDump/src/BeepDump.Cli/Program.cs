using BeepDump.Core;

Messages.English = true;
const string Usage = """
beepdump - rebuild a PC-E500 series ROM from a recording of its buzzer

Usage:
  beepdump decode [options] <recording>...   Read recordings and write the ROM
  beepdump listing [--basic | --csave] [-m MODEL]
                                             Print what to type in
  beepdump --help | --version

Options for decode:
  -o, --output FILE   ROM file to write (default: beepdump.rom)
      --force         Overwrite the ROM file if it already exists
  -m, --model MODEL   e500: main ROM, 256KB (default; PC-E500, PC-E500PJ,
                            PC-E550, PC-1480U, PC-1490UII)
                      e650: extension + main ROM, 384KB (PC-E650, PC-U6000)
      --single        Use only the standard analysis pattern (faster)
      --trace FILE    Write the tone readings of the BASIC version to FILE
  -q, --quiet         Do not show progress

Options for listing:
      --basic         The BASIC version (default: the machine-code version)
      --csave         The CSAVE M commands: one line each, no program to type
  -m, --model MODEL   e500 or e650, for --csave

Recordings: WAV is always readable. Other formats (MOV, MP4, M4A, ...) use Media
Foundation on Windows and need ffmpeg on the PATH on Linux and macOS.

CSAVE M has no check per block, so a single recording cannot be confirmed: its
blocks count as repaired until a second recording agrees.

Several recordings fill each other's gaps. Exit code: 0 = every block was read,
1 = some are missing (the ROM is still written, with FFh there), 2 = error
(a recording could not be read, or nothing was read).
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
catch (Exception e)
{
    return Fail(e.GetBaseException().Message);
}

static int Fail(string message)
{
    Console.Error.WriteLine($"beepdump: {message}");
    Console.Error.WriteLine("Run 'beepdump --help' for usage.");
    return 2;
}

static int Listing(string[] args)
{
    bool basic = false, csave = false;
    var model = RomModel.Main256;
    for (int i = 0; i < args.Length; i++)
    {
        string a = args[i];
        if (a == "--basic") basic = true;
        else if (a == "--csave") csave = true;
        else if (a is "-m" or "--model")
        {
            if (++i >= args.Length) return Fail($"{a} needs a value.");
            string name = args[i].ToLowerInvariant();
            if (name is not ("e500" or "e650")) return Fail($"Unknown model: {name} (use e500 or e650).");
            model = name == "e650" ? RomModel.Main384 : RomModel.Main256;
        }
        else return Fail($"Unknown option: {a}");
    }
    if (basic && csave) return Fail("Choose either --basic or --csave.");
    Console.WriteLine((csave ? model.TapeListing : basic ? FastCode.BasicListing : FastCode.MachineListing).ReplaceLineEndings());
    return 0;
}

static int Decode(string[] args)
{
    var files = new List<string>();
    string output = "beepdump.rom";
    bool force = false, trouble = false, tape = false;
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
            case "--force": force = true; break;
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
    // 録り直しの読み込みで、前に書いた ROM を FFh だらけで上書きしないように
    if (!force && File.Exists(output)) return Fail($"{output} already exists. To add a retake, give all the recordings together, and use --force to overwrite it.");

    // 長い解析の後で書けないと分かることがないように、先に確かめる
    bool existed = File.Exists(output);
    using (new FileStream(output, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read)) { }
    if (!existed) File.Delete(output);

    var image = new RomImage();
    double knownScale = 0;
    var seen = new HashSet<string>();
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
        PcmAudio audio;
        DecodeResult result;
        try
        {
            // 同じ録音を 2 回数えると、一致したことになってしまう
            if (!seen.Add(AudioFile.Fingerprint(file)))
            {
                Console.WriteLine($"{Path.GetFileName(file)}: the same content as an earlier recording, so it was skipped.");
                continue;
            }
            audio = AudioFile.Load(file, f => Show("Loading audio", f));
            result = Analyzer.TryTape(audio, Show)
                ?? (single
                    ? Decoder.Decode(Analyzer.Prepare(audio, Analyzer.Patterns[0]), audio.Rate, Show, trace == null ? null : trace.WriteLine, knownScale)
                    : Analyzer.Analyze(audio, Show, trace == null ? null : trace.WriteLine, knownScale));
            tape |= result.Tape;
        }
        catch (Exception e)
        {
            // 1 本読めなくても、ほかの録音の結果は残す
            if (lastText.Length > 0) Console.Error.WriteLine();
            Console.Error.WriteLine($"beepdump: {Path.GetFileName(file)}: {e.GetBaseException().Message}");
            trouble = true;
            continue;
        }
        if (lastText.Length > 0) Console.Error.Write("\r" + new string(' ', lastText.Length) + "\r");
        image.Add(result.Blocks);
        if (result.Scale > 0 && result.Blocks.Any(b => b.State == BlockState.Good)) knownScale = result.Scale;
        int repaired = result.Blocks.Count(b => b.State == BlockState.Repaired), failed = result.Blocks.Count(b => b.State == BlockState.Failed);
        Console.WriteLine(result.Tape
            ? $"{Path.GetFileName(file)}: {audio.Seconds:F1} s, {repaired} blocks read (not confirmed), {failed} failed"
            : $"{Path.GetFileName(file)}: {audio.Seconds:F1} s, {result.Blocks.Count - repaired - failed} blocks read, {repaired} repaired, {failed} failed");
        foreach (string note in result.Notes) Console.WriteLine("  " + note);
        foreach (var b in result.Blocks.Where(b => b.State == BlockState.Failed)) Console.WriteLine($"  failed: {b.Address:X5}");
    }
    // 後の録音で解決した食い違いは出さない
    foreach (int a in image.Conflicts.Distinct().Where(a => image.StateOf(a) == BlockState.Failed)) Console.WriteLine($"Recordings disagree at {a:X5}: record it once more to decide.");

    bool InModel(Block b) => model.Ranges.Any(r => b.Address >= r.Start && b.Address <= r.End);
    int outside = image.Outside(model);
    if (outside > 0) Console.WriteLine($"{outside} blocks are outside this model's ROM and were not written." + (model == RomModel.Main256 ? " For the extension ROM of a PC-E650 or PC-U6000, use -m e650." : ""));
    if (!image.Blocks.Any(InModel))
    {
        Console.Error.WriteLine($"beepdump: no blocks for this model were read, so {output} was not written.");
        return 2;
    }
    // 読めなかった録音の分だけ足りない ROM で、前の ROM を置き換えない
    if (trouble && existed)
    {
        Console.Error.WriteLine($"beepdump: a recording could not be read, so the existing {output} was not overwritten.");
        return 2;
    }
    image.Save(output, model);
    var missing = image.Missing(model);
    Console.WriteLine($"Wrote {output} ({model.Size} bytes).");
    var unsure = image.Unsure(model);
    string Line(int start, int end) => tape ? "  " + RomModel.TapeCommand(start, end) : $"  &H{start:X5}  &H{end:X5}";
    if (unsure.Count > 0)
    {
        Console.WriteLine("These ranges were repaired or could not be fully confirmed. Record them once more to be certain:");
        foreach (var (start, end) in RomImage.ForRetake(unsure, model)) Console.WriteLine(Line(start, end));
    }
    if (missing.Count == 0)
    {
        Console.WriteLine("Every block was read.");
        return trouble ? 2 : 0;
    }
    Console.WriteLine(tape ? "Missing, filled with FFh. Record these ranges:" : "Missing, filled with FFh. Record these ranges (answers to the two ? prompts):");
    foreach (var (start, end) in RomImage.ForRetake(missing, model)) Console.WriteLine(Line(start, end));
    return trouble ? 2 : 1;
}
