using BeepDump.Core;

// ブザーの音を真似た合成音（矩形波の変化点ごとに減衰する共振）で、音の区切りからブロックの復元までを確かめる。
int failures = 0;
void Test(string name, Func<string?> body)
{
    if (args.Length > 0 && !name.Contains(args[0])) return;   // 名前の一部を指定すると、そのテストだけ動かす
    var watch = System.Diagnostics.Stopwatch.StartNew();
    string? error;
    try { error = body(); }
    catch (Exception e) { error = e.ToString(); }
    Console.WriteLine($"{(error == null ? "ok  " : "FAIL")} {name} ({watch.Elapsed.TotalSeconds:F1} s){(error == null ? "" : ": " + error)}");
    if (error != null) failures++;
}

byte[] Rom(int size, int seed)
{
    var random = new Random(seed);
    var rom = new byte[size];
    for (int i = 0; i < size; i++)
        rom[i] = (i / 256 % 7) switch { 0 => 0, 1 => 0xFF, 2 => (byte)random.Next(8), 3 => (byte)random.Next(16, 40), _ => (byte)random.Next(256) };
    return rom;
}

// 音の並びを作る。corrupt でデータの音を変えられる。
List<(int Value, double After)> Stream(byte[] rom, int address, Func<int, int, int, int>? corrupt = null)
{
    var list = new List<(int, double)>();
    for (int b = 0; b < rom.Length / 256; b++)
    {
        int a = address + b * 256, sum = (a >> 8) & 255;
        list.Add((a / 5000, 14));
        for (int i = 0; i < 256; i++)
        {
            int value = rom[b * 256 + i];
            sum += value;
            list.Add((corrupt?.Invoke(b, i, value) ?? value, i == 255 ? 13 : 15));
        }
        list.Add((sum & 255, 9));
        list.Add((sum / 257, 22));
    }
    return list;
}

float[] Render(List<(int Value, double After)> stream, double scale = 1.007, double drift = 0, double noise = 0.0004, int rate = 48000, int seed = 1, double gain = 1)
{
    var random = new Random(seed);
    double total = 1.5 + stream.Sum(t => 8 * (90 + 4 * t.Value) * scale / 256000 + t.After / 1000);
    var x = new float[(int)((total * (1 + Math.Abs(drift)) + 1) * rate)];
    var filter = new HighPass(rate);
    double time = 1.0;
    const double Ring = 5200, Decay = 900;
    int n = 0;
    foreach (var (value, after) in stream)
    {
        double period = (90 + 4 * value) * scale * (1 + drift * n / stream.Count) / 256000;
        for (int e = 0; e <= 16; e++)
        {
            double at = time + e * period / 2;
            int start = (int)(at * rate), stop = Math.Min(x.Length, start + (int)(0.006 * rate));
            double sign = e % 2 == 0 ? 1 : -1;
            for (int i = start; i < stop; i++)
            {
                double t = i / (double)rate - at;
                x[i] += (float)(gain * sign * 0.03 * Math.Exp(-Decay * t) * Math.Sin(2 * Math.PI * Ring * t));
            }
        }
        time += 8 * period + after / 1000;
        n++;
    }
    for (int i = 0; i < x.Length; i++) x[i] = filter.Next(x[i] + (float)(noise * (random.NextDouble() + random.NextDouble() - 1) * 1.7));
    return x;
}

string? Check(DecodeResult result, byte[] rom, int address, int failed = 0, int repaired = 0)
{
    int good = result.Blocks.Count(b => b.State == BlockState.Good);
    int f = result.Blocks.Count(b => b.State == BlockState.Failed), r = result.Blocks.Count(b => b.State == BlockState.Repaired);
    if (result.Blocks.Count != rom.Length / 256) return $"blocks {result.Blocks.Count} (good {good}, repaired {r}, failed {f})";
    foreach (var b in result.Blocks.Where(b => b.State != BlockState.Failed))
    {
        if (b.Address < address || b.Address + 256 > address + rom.Length) return $"block outside the range: {b.Address:X5} ({b.State}, sum {b.Data.Sum(v => v)}, first {b.Data[0]:X2} {b.Data[1]:X2} {b.Data[2]:X2}); all: " + string.Join(" ", result.Blocks.Select(k => $"{k.Address:X5}={k.State}"));
        if (!b.Data.SequenceEqual(rom.AsSpan(b.Address - address, 256).ToArray())) return $"wrong data at {b.Address:X5} ({b.State})";
    }
    if (f != failed || r != repaired) return $"good {good}, repaired {r} (want {repaired}), failed {f} (want {failed}): " + string.Join(" ", result.Blocks.Where(b => b.State != BlockState.Good).Take(6).Select(b => $"{b.Address:X5}={b.State}"));
    return null;
}

// BASIC 版は、読めないブロックが少し出ても録り直しで埋められる（実機の録音で 1 本あたり約 1 割）。誤ったデータを受け入れないことが大事。
string? Usable(DecodeResult result, byte[] rom, int address)
{
    foreach (var b in result.Blocks.Where(b => b.State != BlockState.Failed))
        if (!b.Data.SequenceEqual(rom.AsSpan(b.Address - address, 256).ToArray())) return $"wrong data at {b.Address:X5} ({b.State})";
    int read = result.Blocks.Count(b => b.State != BlockState.Failed), total = rom.Length / 256;
    return read * 4 >= total * 3 ? null : $"only {read} of {total} blocks";
}

const int Start = 0xC0000;
var rom = Rom(256 * 12, 1);

Test("clean stream", () => Usable(Decoder.Decode(Render(Stream(rom, Start)), 48000), rom, Start));
Test("clock 2% slow", () => { var r = Decoder.Decode(Render(Stream(rom, Start), scale: 1.02), 48000); return Math.Abs(r.Scale / 1.02 - 1) > 0.002 ? $"scale {r.Scale}" : Usable(r, rom, Start); });
Test("clock 1% fast", () => Usable(Decoder.Decode(Render(Stream(rom, Start), scale: 0.99), 48000), rom, Start));
Test("clock drifts 0.5% during the run", () =>
{
    var longRom = Rom(256 * 60, 4);
    return Usable(Decoder.Decode(Render(Stream(longRom, Start), drift: 0.005), 48000), longRom, Start);
});
foreach (double machine in new[] { 0.995, 1.002, 1.0065, 1.012, 1.02 })
    foreach (int count in new[] { 1, 6 })
        Test($"all-FF range is not read as all-FE (clock {machine}, {count} blocks)", () =>
        {
            var ff = new byte[256 * count];
            Array.Fill(ff, (byte)0xFF);
            // 1 ブロックだけだと番地を確かめられないので、BASIC 版は読めなかったことにする
            var result = Decoder.Decode(Render(Stream(ff, 0x20000), scale: machine), 48000);
            if (count == 1) return result.Blocks.All(b => b.State == BlockState.Failed) ? null : "an unconfirmed block was accepted";
            return Check(result, ff, 0x20000);
        });
Test("a short all-FF retake uses the clock found in the earlier recording", () =>
{
    const double Machine = 1.012;
    var first = Decoder.Decode(Render(Stream(rom, Start), scale: Machine), 48000);
    var ff = new byte[512];
    Array.Fill(ff, (byte)0xFF);
    return Check(Decoder.Decode(Render(Stream(ff, 0x20000), scale: Machine), 48000, knownScale: first.Scale), ff, 0x20000);
});
Test("intervals stretched at the start of a run (key still held) do not lose the block", () =>
{
    var stream = Stream(rom, Start);
    foreach (int i in new[] { 1, 3, 4, 8, 12 }) stream[i] = (stream[i].Value, stream[i].After + 2.7);
    return Usable(Decoder.Decode(Render(stream), 48000), rom, Start);
});
Test("all-00 range", () =>
{
    var zero = new byte[256 * 3];
    return Usable(Decoder.Decode(Render(Stream(zero, 0xC0000)), 48000), zero, 0xC0000);
});
Test("44.1 kHz recording", () => Usable(Decoder.Decode(Render(Stream(rom, Start), rate: 44100), 44100), rom, Start));
Test("quiet recording", () => Usable(Decoder.Decode(Render(Stream(rom, Start), noise: 0.0001, gain: 0.1), 48000), rom, Start));
Test("near the top of memory", () =>
{
    var top = Rom(256 * 4, 2);
    return Usable(Decoder.Decode(Render(Stream(top, 0xFFC00)), 48000), top, 0xFFC00);
});
Test("extension ROM addresses", () =>
{
    var ext = Rom(256 * 4, 3);
    return Usable(Decoder.Decode(Render(Stream(ext, 0x20000)), 48000), ext, 0x20000);
});
Test("a wrong tone that cannot be repaired is reported, not accepted", () =>
{
    var r = Decoder.Decode(Render(Stream(rom, Start, (b, i, v) => b == 5 && i == 100 ? (v + 40) & 255 : v)), 48000);
    return r.Blocks.Any(b => b.Address == Start + 5 * 256 && b.State != BlockState.Failed) ? "block 5 accepted" : Usable(r, rom, Start);
});
Test("loud noise only loses the blocks it covers", () =>
{
    var x = Render(Stream(rom, Start));
    int center = x.Length / 2;
    var random = new Random(5);
    for (int i = center; i < center + 48000 * 3 / 2 && i < x.Length; i++) x[i] += (float)(0.2 * (random.NextDouble() - 0.5));
    var r = Decoder.Decode(x, 48000);
    var image = new RomImage();
    image.Add(r.Blocks);
    var missing = image.Missing(new RomModel("test", [(Start, Start + rom.Length - 1)]));
    if (missing.Count == 0 || missing.Count > 2) return $"missing ranges {missing.Count}";
    foreach (var b in r.Blocks.Where(b => b.State != BlockState.Failed))
        if (!b.Data.SequenceEqual(rom.AsSpan(b.Address - Start, 256).ToArray())) return $"wrong data at {b.Address:X5}";
    return null;
});
Test("a retake fills what was missing", () =>
{
    var image = new RomImage();
    var model = new RomModel("test", [(Start, Start + rom.Length - 1)]);
    image.Add(Decoder.Decode(Render(Stream(rom[..(256 * 7)], Start)), 48000).Blocks);
    int before = image.Blocks.Count();
    if (before < 5) return $"first recording read only {before}";
    image.Add(Decoder.Decode(Render(Stream(rom[(256 * 7)..], Start + 7 * 256), seed: 2), 48000).Blocks);
    if (image.Blocks.Count() <= before) return "retake added nothing";
    foreach (var b in image.Blocks) if (!b.Data.SequenceEqual(rom.AsSpan(b.Address - Start, 256).ToArray())) return $"wrong data at {b.Address:X5}";
    return image.Missing(model).All(m => m.End < Start + rom.Length) ? null : "bad range";
});
Test("two recordings fill each other's failed blocks", () =>
{
    float[] WithNoise(float[] x, double at, int seed)
    {
        var random = new Random(seed);
        for (int i = (int)(x.Length * at); i < (int)(x.Length * at) + 48000 && i < x.Length; i++) x[i] += (float)(0.2 * (random.NextDouble() - 0.5));
        return x;
    }
    var image = new RomImage();
    image.Add(Decoder.Decode(WithNoise(Render(Stream(rom, Start)), 0.3, 1), 48000).Blocks);
    int first = image.Blocks.Count();
    image.Add(Decoder.Decode(WithNoise(Render(Stream(rom, Start), seed: 2), 0.7, 2), 48000).Blocks);
    if (image.Blocks.Count() <= first) return $"second recording added nothing ({first})";
    foreach (var b in image.Blocks) if (!b.Data.SequenceEqual(rom.AsSpan(b.Address - Start, 256).ToArray())) return $"wrong data at {b.Address:X5}";
    return null;
});
Test("blocks that disagree stay undecided until two recordings agree", () =>
{
    var model = new RomModel("test", [(Start, Start + 255)]);
    byte[] a = new byte[256], b = new byte[256], c = new byte[256];
    a[0] = 1; b[0] = 2; c[0] = 3;
    var image = new RomImage();
    image.Add([new(Start, BlockState.Good, a)]);
    image.Add([new(Start, BlockState.Good, b)]);
    if (image.Conflicts.Count != 1 || image.Missing(model).Count != 1) return "conflict not reported";
    image.Add([new(Start, BlockState.Good, c)]);
    if (image.Missing(model).Count != 1) return "decided without agreement";
    image.Add([new(Start, BlockState.Good, b)]);
    return image.Missing(model).Count == 0 && image.Build(model)[0] == 2 ? null : "agreement not used";
});
Test("a repaired block read the same again becomes certain", () =>
{
    var image = new RomImage();
    var data = new byte[256];
    image.Add([new Block(Start, BlockState.Repaired, data)]);
    if (image.StateOf(Start) != BlockState.Repaired) return "first";
    image.Add([new Block(Start, BlockState.Repaired, data)]);
    return image.StateOf(Start) == BlockState.Good ? null : "not upgraded";
});
Test("confirmed content is not overtaken by a tie", () =>
{
    byte[] x = new byte[256], y = new byte[256];
    y[0] = 1;
    var image = new RomImage();
    foreach (var data in new[] { x, x, y, y }) image.Add([new(Start, BlockState.Good, data)]);
    if (image.StateOf(Start) != BlockState.Failed) return "a tie was decided";
    image.Add([new(Start, BlockState.Good, x)]);
    if (image.StateOf(Start) != BlockState.Good || image.Blocks.Single().Data[0] != 0) return "the leading content was not used";
    return null;
});
Test("a BASIC recording at a low sample rate does not turn green", () =>
{
    var r = Decoder.Decode(Render(Stream(rom, Start), rate: 22050), 22050);
    if (r.Blocks.Any(b => b.State == BlockState.Good)) return "a block is green";
    return r.Blocks.Any(b => b.State == BlockState.Repaired) ? null : "nothing was read";
});
Test("saving replaces a ROM file and leaves no temporary file", () =>
{
    string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    try
    {
        var model = new RomModel("test", [(Start, Start + 255)]);
        File.WriteAllBytes(path, [1, 2, 3]);
        var image = new RomImage();
        image.Add([new(Start, BlockState.Good, new byte[256])]);
        image.Save(path, model);
        if (new FileInfo(path).Length != 256) return "not replaced";
        return File.Exists(path + ".tmp") ? "temporary file left" : null;
    }
    finally { File.Delete(path); }
});
Test("a retake starts before the missing blocks and is long enough", () =>
{
    var model = new RomModel("test", [(Start, Start + 0x1FFF)]);
    var wide = RomImage.ForRetake([(Start + 0x500, Start + 0x5FF), (Start, Start + 0xFF), (Start + 0x1F00, Start + 0x1FFF), (Start + 0x400, Start + 0x13FF)], model);
    if (wide[0] != (Start + 0x300, Start + 0xAFF)) return $"middle {wide[0]}";
    if (wide[1] != (Start, Start + 0x7FF)) return $"first {wide[1]}";
    if (wide[2] != (Start + 0x1800, Start + 0x1FFF)) return $"last {wide[2]}";
    return wide[3] == (Start + 0x200, Start + 0x13FF) ? null : $"long {wide[3]}";
});
Test("BASIC: two blocks shifted the same way do not outvote a correct block", () =>
{
    var random = new Random(5);
    var data = new byte[256 * 3];
    for (int i = 0; i < data.Length; i++) data[i] = (byte)random.Next(64, 200);
    // 2 ブロック目と 3 ブロック目で長い音を 1 段高く読むと、和が 1 増えて番地が 1 ブロック手前になり、検査も通る
    var r = Decoder.Decode(Render(Stream(data, Start + 0x100, (b, i, v) => b >= 1 && i == 100 ? v + 1 : v)), 48000);
    var read = r.Blocks.Where(b => b.State != BlockState.Failed).ToArray();
    return read.Length == 0 ? null : "accepted: " + string.Join(" ", read.Select(b => $"{b.Address:X5}={b.State}"));
});
Test("a renamed copy of a recording has the same fingerprint", () =>
{
    string a = Path.GetTempFileName(), b = Path.GetTempFileName();
    try
    {
        File.WriteAllBytes(a, [1, 2, 3]);
        File.WriteAllBytes(b, [1, 2, 3]);
        if (AudioFile.Fingerprint(a) != AudioFile.Fingerprint(b)) return "copies differ";
        File.WriteAllBytes(b, [1, 2, 4]);
        return AudioFile.Fingerprint(a) != AudioFile.Fingerprint(b) ? null : "different content matched";
    }
    finally { File.Delete(a); File.Delete(b); }
});
Test("ranges to confirm follow the repaired blocks", () =>
{
    var model = new RomModel("test", [(Start, Start + 1023)]);
    var image = new RomImage();
    image.Add([new(Start, BlockState.Good, new byte[256]), new(Start + 256, BlockState.Repaired, new byte[256]), new(Start + 512, BlockState.Repaired, new byte[256])]);
    var unsure = image.Unsure(model);
    if (unsure.Count != 1 || unsure[0] != (Start + 256, Start + 767)) return "unsure ranges";
    return image.Missing(model).Single() == (Start + 768, Start + 1023) ? null : "missing ranges";
});
Test("a repaired block and a clean one that differ stay undecided", () =>
{
    byte[] repaired = new byte[256], good = new byte[256];
    repaired[0] = 1; good[0] = 2;
    var image = new RomImage();
    image.Add([new(Start, BlockState.Repaired, repaired)]);
    image.Add([new(Start, BlockState.Good, good)]);
    if (image.StateOf(Start) != BlockState.Failed || image.Conflicts.Count == 0) return "not held back";
    image.Add([new(Start, BlockState.Good, good)]);
    return image.StateOf(Start) == BlockState.Good && image.Blocks.Single().Data[0] == 2 ? null : "not decided by a third recording";
});
Test("WAV reader: 16-bit stereo, 24-bit and float", () =>
{
    static byte[] Wav(short format, short channels, short bits, byte[] data, bool unknownSize = false)
    {
        using var m = new MemoryStream();
        using var w = new BinaryWriter(m);
        w.Write("RIFF"u8); w.Write(unknownSize ? -1 : 36 + data.Length); w.Write("WAVEfmt "u8); w.Write(16); w.Write(format); w.Write(channels); w.Write(48000);
        w.Write(48000 * channels * bits / 8); w.Write((short)(channels * bits / 8)); w.Write(bits); w.Write("data"u8); w.Write(unknownSize ? -1 : data.Length); w.Write(data);
        return m.ToArray();
    }
    var stereo = WavReader.Read(new MemoryStream(Wav(1, 2, 16, [1, 0, 2, 0, 3, 0, 4, 0])));
    if (stereo == null || stereo.Channels.Length != 2 || stereo.Length != 2 || stereo.Channels[1][1] != 4 || stereo.Rate != 48000) return "16-bit";
    var piped = WavReader.Read(new MemoryStream(Wav(1, 1, 16, [5, 0, 6, 0], unknownSize: true)));
    if (piped == null || piped.Length != 2 || piped.Channels[0][1] != 6) return "unknown size (from a pipe)";
    var chunk = Wav(1, 1, 16, [7, 0, 8, 0]).Concat("LIST"u8.ToArray()).Concat(new byte[] { 4, 0, 0, 0, 1, 2, 3, 4 }).ToArray();
    var trailing = WavReader.Read(new MemoryStream(chunk));
    if (trailing == null || trailing.Length != 2) return "a chunk after data was read as samples";
    var deep = WavReader.Read(new MemoryStream(Wav(1, 1, 24, [0, 0x34, 0x12])));
    if (deep == null || deep.Channels[0][0] != 0x1234) return "24-bit";
    var real = WavReader.Read(new MemoryStream(Wav(3, 1, 32, BitConverter.GetBytes(0.5f))));
    if (real == null || Math.Abs(real.Channels[0][0] - 16383) > 1) return "float";
    return WavReader.Read(new MemoryStream(new byte[20])) == null ? null : "not a WAV";
});
Test("messages follow the language setting", () =>
{
    Messages.English = true;
    string english = RomModel.Main256.Label + Analyzer.Patterns[0].Label;
    Messages.English = false;
    string japanese = RomModel.Main256.Label + Analyzer.Patterns[0].Label;
    return english.Any(c => c > 0x2FF) ? "Japanese left in the English text" : japanese.Any(c => c > 0x2FF) ? null : "English in the Japanese text";
});
Test("silence decodes to nothing", () => Decoder.Decode(new float[48000 * 2], 48000).Blocks.Count == 0 ? null : "blocks found");

// ---- 機械語版: ブザーの応答を真似た波形で、fast/beepfast.asm と同じ音の並びを作る
float[] RenderFast(byte[] data, int address, double bps = 2385, double noise = 0.0004, int seed = 1, int rate = 48000, double clock = 1, double gapSeconds = 0.0105, double decay = 1500, double tail = 0.005)
{
    double cell = rate / bps * clock, middle = cell * 0.48, extra = cell * 0.09, oneMore = extra / 9;
    var bursts = new List<byte[]>();
    bursts.Add([0, 0, 0x5A, .. FastCode.Learn]);
    for (int b = 0; b < data.Length / 256; b++)
    {
        int s1 = address + b * 256, s2 = 0;
        var bytes = new byte[265];
        bytes[2] = 0xA5;
        for (int i = 0; i < 256; i++) { bytes[3 + i] = data[b * 256 + i]; s1 = (s1 + bytes[3 + i]) & 0xFFFFF; s2 = (s2 + s1) & 0xFFFFF; }
        bytes[259] = (byte)s1; bytes[260] = (byte)(s1 >> 8); bytes[261] = (byte)(s1 >> 16); bytes[262] = (byte)s2; bytes[263] = (byte)(s2 >> 8); bytes[264] = (byte)(s2 >> 16);
        bursts.Add(bytes);
    }
    double total = 0.6 + bursts.Sum(b => b.Length * (8 * cell + 8 * oneMore + 2 * extra) / rate + gapSeconds) + 0.3;
    var x = new float[(int)(total * rate)];
    double t = 0.5 * rate;
    void Edge(double at, bool up)
    {
        double gain = up ? 0.03 : 0.008;
        int start = (int)at, stop = Math.Min(x.Length, start + (int)(tail * rate));
        for (int i = start; i < stop; i++)
        {
            double u = (i - at) / rate;
            x[i] += (float)(gain * Math.Exp(-decay * u) * (Math.Sin(2 * Math.PI * 4600 * u) + 0.5 * Math.Sin(2 * Math.PI * 2700 * u)));
        }
    }
    foreach (var bytes in bursts)
    {
        bool level = false;
        for (int b = 0; b < bytes.Length; b++)
        {
            for (int n = 0; n < 8; n++)
            {
                int bit = bytes[b] >> n & 1;
                level = !level; Edge(t, level);
                if (bit != 0) { level = !level; Edge(t + middle, level); }
                t += cell + bit * oneMore;
            }
            t += extra / 36 * (b == 0 ? 12 : b == 1 ? 7 : b == 2 ? 48 : b < 258 ? 36 : b == 258 ? 42 : 22);
        }
        t += gapSeconds * rate;
    }
    var filter = new HighPass(rate);
    var random = new Random(seed);
    for (int i = 0; i < x.Length; i++) x[i] = filter.Next(x[i] + (float)(noise * (random.NextDouble() + random.NextDouble() - 1) * 1.7));
    return x;
}

var fastRom = Rom(256 * 7, 7);
Test("fast: reads every block at 2400 bps", () => { var x = RenderFast(fastRom, Start); if (!FastDecoder.LooksFast(x, 48000)) return "not detected"; return Check(Decoder.Decode(x, 48000), fastRom, Start); });
Test("fast: 1200 bps", () => Check(Decoder.Decode(RenderFast(fastRom, Start, bps: 1200), 48000), fastRom, Start));
Test("fast: 3200 bps", () => Check(Decoder.Decode(RenderFast(fastRom, Start, bps: 3200), 48000), fastRom, Start));
Test("fast: 44.1 kHz recording", () => Check(Decoder.Decode(RenderFast(fastRom, Start, rate: 44100), 44100), fastRom, Start));
Test("fast: last block of memory", () =>
{
    var top = Rom(256 * 2, 8);
    return Check(Decoder.Decode(RenderFast(top, 0xFFE00), 48000), top, 0xFFE00);
});
Test("fast: all-FF blocks of the extension ROM", () =>
{
    var ff = new byte[256 * 3];
    Array.Fill(ff, (byte)0xFF);
    return Check(Decoder.Decode(RenderFast(ff, 0x20000), 48000), ff, 0x20000);
});
Test("fast: noise over one block loses only that block, and a second recording fills it", () =>
{
    var model = new RomModel("test", [(Start, Start + fastRom.Length - 1)]);
    var x = RenderFast(fastRom, Start);
    var random = new Random(9);
    int from = (int)(48000 * 4.0);
    for (int i = from; i < from + 12000; i++) x[i] += (float)(0.3 * (random.NextDouble() - 0.5));
    var first = Decoder.Decode(x, 48000);
    foreach (var b in first.Blocks.Where(b => b.State != BlockState.Failed))
        if (!b.Data.SequenceEqual(fastRom.AsSpan(b.Address - Start, 256).ToArray())) return $"wrong data at {b.Address:X5}";
    var image = new RomImage();
    image.Add(first.Blocks);
    if (image.Missing(model).Count == 0) return "noise did not lose anything";
    image.Add(Decoder.Decode(RenderFast(fastRom, Start, seed: 2), 48000).Blocks);
    return image.Missing(model).Count == 0 && image.Build(model).SequenceEqual(fastRom) ? null : "still missing";
});
Test("fast: a long loud noise does not lose the blocks after it", () =>
{
    var x = RenderFast(fastRom, Start);
    var random = new Random(11);
    int from = (int)(48000 * 3.0);
    for (int i = from; i < from + 48000 / 4; i++) x[i] += (float)(0.6 * (random.NextDouble() - 0.5));
    var r = Decoder.Decode(x, 48000);
    foreach (var b in r.Blocks.Where(b => b.State != BlockState.Failed))
        if (!b.Data.SequenceEqual(fastRom.AsSpan(b.Address - Start, 256).ToArray())) return $"wrong data at {b.Address:X5}";
    return r.Blocks.Any(b => b.State == BlockState.Good && b.Address == Start + 6 * 256) ? null : "last block lost";
});
Test("multiple patterns: an out-of-phase stereo recording is still read", () =>
{
    var mono = RenderFast(fastRom, Start);
    var left = new short[mono.Length]; var right = new short[mono.Length];
    for (int i = 0; i < mono.Length; i++) { left[i] = (short)Math.Clamp(mono[i] * 32767f, -32768f, 32767f); right[i] = (short)-left[i]; }
    var r = Analyzer.Analyze(new PcmAudio([left, right], 48000));
    return Check(r, fastRom, Start);
});
Test("fast: blocks joined by sound in the gaps are separated, as on the PC-U6000", () =>
{
    var x = RenderFast(fastRom, Start);
    var bursts = Segmenter.Find(x, 48000, 0.0035).Where(t => t.End - t.Start > 0.15 * 48000).ToArray();
    var random = new Random(4);
    foreach (int k in new[] { 2, 4, 5 })
        for (int i = bursts[k].End - 100; i < bursts[k + 1].Start + 100; i++) x[i] += (float)(0.04 * (random.NextDouble() - 0.5));
    int seen = Segmenter.Find(x, 48000, 0.0035).Count(t => t.End - t.Start > 0.15 * 48000);
    if (seen != bursts.Length - 3) return $"expected the blocks to join: {seen} bursts";
    return Check(Decoder.Decode(x, 48000), fastRom, Start);
});
Test("fast: the learning sequence joined to the first block is separated", () =>
{
    var x = RenderFast(fastRom, Start);
    // 学習用の並びの後の無音を埋める
    var bursts = Segmenter.Find(x, 48000, 0.0035).Where(t => t.End - t.Start > 0.15 * 48000).ToArray();
    var random = new Random(3);
    for (int i = bursts[0].End - 100; i < bursts[1].Start + 100; i++) x[i] += (float)(0.04 * (random.NextDouble() - 0.5));
    if (Segmenter.Find(x, 48000, 0.0035).Count(t => t.End - t.Start > 0.15 * 48000) != bursts.Length - 1) return "still separate";
    return Check(Decoder.Decode(x, 48000), fastRom, Start);
});
Test("fast: room noise before the program starts is not taken for the learning sequence", () =>
{
    var tone = RenderFast(fastRom, Start);
    var random = new Random(8);
    var x = new float[48000 * 4 + tone.Length];
    for (int i = 0; i < 48000 * 4; i++) x[i] = (float)(0.008 * (random.NextDouble() - 0.5));
    tone.CopyTo(x, 48000 * 4);
    return Check(Decoder.Decode(x, 48000), fastRom, Start);
});
Test("fast: the level changing during a block (automatic gain control) is followed", () =>
{
    var x = RenderFast(fastRom, Start);
    for (int i = 0; i < x.Length; i++) x[i] *= (float)(0.25 + 0.75 * (0.5 + 0.5 * Math.Sin(2 * Math.PI * i / 48000 / 3)));
    return Check(Decoder.Decode(x, 48000), fastRom, Start);
});
Test("fast: a short retake with the learning sequence joined to its only block is read", () =>
{
    var one = fastRom[..256];
    var x = RenderFast(one, Start);
    var bursts = Segmenter.Find(x, 48000, 0.0035).Where(t => t.End - t.Start > 0.15 * 48000).ToArray();
    var random = new Random(3);
    for (int i = bursts[0].End - 100; i < bursts[1].Start + 100; i++) x[i] += (float)(0.04 * (random.NextDouble() - 0.5));
    if (Segmenter.Find(x, 48000, 0.0035).Count(t => t.End - t.Start > 0.15 * 48000) != 1) return "still separate";
    var r = Decoder.Decode(x, 48000);
    return r.Blocks.Count == 1 && r.Blocks[0].Address == Start && r.Blocks[0].State == BlockState.Repaired && r.Blocks[0].Data.SequenceEqual(one) ? null : "blocks: " + string.Join(" ", r.Blocks.Select(b => $"{b.Address:X5}={b.State}"));
});
Test("fast: a missing block does not make its neighbors stray", () =>
{
    var x = RenderFast(fastRom, Start);
    var bursts = Segmenter.Find(x, 48000, 0.0035).Where(t => t.End - t.Start > 0.15 * 48000).ToArray();
    Array.Clear(x, bursts[3].Start - 50, bursts[3].End - bursts[3].Start + 100);   // 3 番目のブロックの音を消す
    var r = Decoder.Decode(x, 48000);
    foreach (int k in new[] { 0, 1, 3, 4, 5, 6 })
    {
        int address = Start + k * 256;
        if (!r.Blocks.Any(b => b.Address == address && b.State == BlockState.Good)) return $"lost {address:X5}: " + string.Join(" ", r.Blocks.Select(b => $"{b.Address:X5}={b.State}"));
    }
    return null;
});
Test("fast: a loud noise just before the learning sequence does not lose the recording", () =>
{
    var tone = RenderFast(fastRom, Start);
    var random = new Random(7);
    var x = new float[48000 + tone.Length];
    for (int i = 12000; i < 12000 + 12000; i++) x[i] = (float)(0.08 * (random.NextDouble() - 0.5));     // 0.25 秒の物音
    tone.CopyTo(x, 48000 - (int)(0.5 * 48000) + 12000);                                                 // 2 秒以内に学習用の並び
    var r = Decoder.Decode(x, 48000);
    foreach (var b in r.Blocks.Where(b => b.State != BlockState.Failed))
        if (b.Address < Start || !b.Data.SequenceEqual(fastRom.AsSpan(b.Address - Start, 256).ToArray())) return $"wrong data at {b.Address:X5}";
    return r.Blocks.Count(b => b.State == BlockState.Good) >= 5 ? null : "read: " + string.Join(" ", r.Blocks.Select(b => $"{b.Address:X5}={b.State}"));
});
Test("fast: a misread upper address byte does not move a block", () =>
{
    // S2 は番地の上位 8 bit を見ていないので、S1 の上位バイトの読み違いは検査で分からない。ここを本物と同じ形で作る
    var x = RenderFast(fastRom, Start);
    var bursts = Segmenter.Find(x, 48000, 0.0035).Where(t => t.End - t.Start > 0.15 * 48000).ToArray();
    var other = RenderFast(fastRom, 0x90000);   // 番地の上位だけ違う音
    var near = Segmenter.Find(other, 48000, 0.0035).Where(t => t.End - t.Start > 0.15 * 48000).ToArray();
    // 4 ブロック目を、番地の違う音に置き換える
    int k = 4, length = Math.Min(bursts[k].End - bursts[k].Start, near[k].End - near[k].Start) + 200;
    Array.Copy(other, near[k].Start - 100, x, bursts[k].Start - 100, length);
    var r = Decoder.Decode(x, 48000);
    if (r.Blocks.Any(b => b.Address < Start && b.State != BlockState.Failed)) return "a block at a stray address was accepted: " + string.Join(" ", r.Blocks.Where(b => b.Address < Start).Select(b => $"{b.Address:X5}={b.State}"));
    return r.Blocks.Count(b => b.State == BlockState.Good) >= 6 ? null : "other blocks were lost";
});
Test("fast: noise that defeats the reading never gives wrong data", () =>
{
    // ブロックの音の中にだけ雑音を足す。読めなくなる大きさの前後を通る
    int failed = 0;
    foreach (double noise in new[] { 0.024, 0.026, 0.028, 0.03 })
    {
        var x = RenderFast(fastRom, Start);
        var random = new Random(6);
        foreach (var t in Segmenter.Find(x, 48000, 0.0035).Where(t => t.End - t.Start > 0.15 * 48000).Skip(1))
            for (int i = t.Start + 800; i < t.End - 100; i++) x[i] += (float)(noise * (random.NextDouble() + random.NextDouble() - 1));
        var r = Decoder.Decode(x, 48000);
        foreach (var b in r.Blocks.Where(b => b.State != BlockState.Failed))
            if (b.Address < Start || !b.Data.SequenceEqual(fastRom.AsSpan(b.Address - Start, 256).ToArray())) return $"wrong data at {b.Address:X5}";
        failed += r.Blocks.Count(b => b.State == BlockState.Failed);
    }
    return failed > 0 ? null : "the noise did not lose anything";
});
Test("fast: BASIC recordings are not mistaken for machine code", () => FastDecoder.LooksFast(Render(Stream(rom[..768], Start)), 48000) ? "detected as fast" : null);
Test("fast: a long noise in a BASIC recording does not make it look like machine code", () =>
{
    var x = Render(Stream(rom[..768], Start));
    var random = new Random(2);
    for (int i = x.Length / 3; i < x.Length / 3 + 48000; i++) x[i] += (float)(0.2 * (random.NextDouble() - 0.5));
    return FastDecoder.LooksFast(x, 48000) ? "detected as fast" : null;
});

Console.WriteLine(failures == 0 ? "All tests passed." : $"{failures} tests failed.");
return failures == 0 ? 0 : 1;
