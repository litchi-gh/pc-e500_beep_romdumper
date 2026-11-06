namespace BeepDump.Core;

public enum ChannelMix { Both, Left, Right, Difference }

/// <summary>解析のしかた。録音機材によって音の録れ方が違うので、いくつかを試す。</summary>
public sealed record AnalysisPattern(string Name, string EnglishName, ChannelMix Channel = ChannelMix.Both, double Cutoff = 800, bool Level = false, double Relative = 0.12, int Lookahead = 3)
{
    public string Label => Messages.Pick(Name, EnglishName);
}

/// <summary>録音の音声データ。チャンネルごとに 16 bit で持つ。</summary>
public sealed record PcmAudio(short[][] Channels, int Rate)
{
    public int Length => Channels[0].Length;
    public double Seconds => (double)Length / Rate;
}

/// <summary>
/// 録音を複数のパターンで読み、検査の通ったブロックを集める。全部読めたらそこで終わる。
/// 1 つのパターンでは読めないブロックも、別のパターンで読めることがある。
/// </summary>
public static class Analyzer
{
    const int IdlePatterns = 3;
    const double StandardCutoff = 800, StandardRelative = 0.12;

    public static readonly AnalysisPattern[] Patterns =
    [
        new("標準", "standard"),
        new("音量のならし", "level smoothing", Level: true),
        new("左チャンネル", "left channel", ChannelMix.Left),
        new("右チャンネル", "right channel", ChannelMix.Right),
        new("左右の差（逆相の録音）", "left minus right (out-of-phase recording)", ChannelMix.Difference),
        new("低い音も使う", "keep low frequencies", Cutoff: 300),
        new("高い音だけ使う（低い雑音が多い録音）", "high frequencies only (low-frequency noise)", Cutoff: 2000),
        new("小さい音も拾う", "pick up quiet sound", Relative: 0.05),
        new("大きい音だけ拾う（響きが長い録音）", "loud sound only (long reverberation)", Relative: 0.3),
        new("先読みを増やす", "longer lookahead", Lookahead: 5),
        new("音量のならし + 先読みを増やす", "level smoothing + longer lookahead", Level: true, Lookahead: 5),
    ];

    public static DecodeResult Analyze(PcmAudio audio, Action<string, double>? progress = null, Action<string>? trace = null, double knownScale = 0)
    {
        // CSAVE M は音の形式が違い、パターンを変えて読む処理は当てはまらない
        if (TryTape(audio, progress) is { } tape) return tape;
        var patterns = Patterns.Where(p => audio.Channels.Length >= 2 || p.Channel == ChannelMix.Both).ToArray();
        var votes = new Dictionary<int, List<Block>>();
        var failed = new HashSet<int>();
        var notes = new List<string>();
        var used = new List<string>();
        var firstNotes = new List<string>();     // 1 つも読めなかったときは、最初のパターンの理由を伝える
        double scale = 0, overhead = 0;
        int tones = 0, number = 0, idle = 0;
        bool fast = false;
        (int From, int To)? sound = null;     // 機械語版で読めた音の範囲。後のパターンはこの外の雑音を読まない
        Exception? error = null;
        foreach (var pattern in patterns)
        {
            int index = number++;
            // 機械語版は先読みを増やす意味があるが、BASIC 版にはないので飛ばす
            if (pattern.Lookahead != 3 && !fast) continue;
            // BASIC 版の検査は和で弱く、受け入れの判定は標準の音に合わせてある。音の中身や区切り方を変えると、和の変わらない読み違いが判定をすり抜ける
            // （実機の録音とその加工で、左右の差、高域通過、検出のしきい値を変えたパターンで誤ったデータが通った）。何も読めていないときだけ使う
            if (votes.Count > 0 && !fast && (pattern.Cutoff != StandardCutoff || pattern.Channel == ChannelMix.Difference || pattern.Relative != StandardRelative)) continue;
            DecodeResult result;
            try
            {
                var samples = Prepare(audio, pattern);
                // 版の判別は最初に読めたパターンで決める。加工された音では、BASIC 版の音が別のパターンで機械語版に見えることがある
                if (votes.Count == 0) fast = FastDecoder.LooksFast(samples, audio.Rate, pattern.Relative);
                // 機械語版は、ほかのパターンですでに読めたブロックを読まない（BASIC 版は全部の音から時間を学ぶので、全部読む）
                result = Decoder.Decode(samples, audio.Rate, (text, f) => progress?.Invoke($"{text} ({index + 1}/{patterns.Length}: {pattern.Label})", (index + f) / patterns.Length),
                    index == 0 ? trace : null, knownScale, pattern.Relative, pattern.Lookahead, votes.Count > 0 ? address => !votes.ContainsKey(address) : null, fast, sound);
            }
            catch (Exception e)
            {
                // メモリ不足などで止まっても、読めたブロックは捨てない。何も読めていなければ、ほかのパターンを試す
                if (votes.Count == 0) { error ??= e; continue; }
                notes.Add(Messages.Pick($"「{pattern.Label}」の途中でエラーになったので、ここまでの結果を使います: {e.GetBaseException().Message}", $"An error stopped \"{pattern.Label}\", so the results so far are used: {e.GetBaseException().Message}"));
                break;
            }
            // 誤りが通りやすいパターンでしか読めなかったブロックは、検査が通っても緑にしない
            bool risky = !fast && (pattern.Cutoff != StandardCutoff || pattern.Channel == ChannelMix.Difference || pattern.Relative != StandardRelative);
            int before = votes.Count;
            foreach (var block in result.Blocks)
            {
                if (block.State == BlockState.Failed) { failed.Add(block.Address); continue; }
                if (!votes.TryGetValue(block.Address, out var list)) votes[block.Address] = list = [];
                list.Add(risky && block.State == BlockState.Good ? block with { State = BlockState.Repaired } : block);
            }
            if (votes.Count > before) { used.Add($"{pattern.Label} {votes.Count - before}"); sound ??= result.Sound; }
            if (risky && votes.Count > before) notes.Add(Messages.Pick("BASIC 版を標準以外のパターンで読みました。誤りが通りやすいので、もう 1 回録音して確かめてください。", "The BASIC version was read with a non-standard pattern, where errors pass more easily. Record it again to confirm."));
            // 検査の通らなかった倍率は不確かなので、後のパターンの基準にしない
            if (result.Scale > 0 && result.Blocks.Any(b => b.State == BlockState.Good)) { if (scale == 0) scale = result.Scale; if (knownScale == 0) knownScale = result.Scale; overhead = result.Overhead; }
            tones = Math.Max(tones, result.ToneCount);
            if (votes.Count > before) notes.AddRange(result.Notes.Where(n => !notes.Contains(n)));
            else if (index == 0) firstNotes = result.Notes;
            if (Complete(votes, failed)) break;
            // 読めない所が物音で潰れていれば、ほかのパターンでも読めない。長い録音で時間をかけすぎない
            idle = votes.Count > before ? 0 : idle + 1;
            if (votes.Count > 0 && idle >= IdlePatterns) break;
        }

        if (votes.Count == 0 && failed.Count == 0 && error != null) throw error;

        var blocks = new List<Block>();
        foreach (var (address, list) in votes.OrderBy(v => v.Key))
        {
            var groups = list.GroupBy(b => Convert.ToHexString(b.Data)).Select(g => g.OrderBy(b => b.State).First()).ToArray();
            // パターンは同じ音声を読むので、数が多くても確認にならない。食い違えばどれも信用しない
            if (groups.Length > 1)
            {
                notes.Add(Messages.Pick($"{address:X5} はパターンによって内容が違いました。", $"{address:X5} differed between patterns."));
                blocks.Add(new(address, BlockState.Failed, []));
                continue;
            }
            blocks.Add(groups[0]);
        }
        foreach (int address in failed.Where(a => !votes.ContainsKey(a)).OrderBy(a => a)) blocks.Add(new(address, BlockState.Failed, []));
        blocks.Sort((a, b) => a.Address.CompareTo(b.Address));
        if (votes.Count == 0) notes.AddRange(firstNotes);
        if (used.Count > 0) notes.Add(Messages.Pick("読めたパターン: ", "Patterns that read blocks: ") + string.Join(Messages.Pick("、", ", "), used));
        if (blocks.Count == 0) notes.Add(Messages.Pick("どのパターンでも読めませんでした。音量が小さいか、録音機材の雑音消し（ノイズ抑制）で音が消えているかもしれません。", "No pattern could read this recording. The level may be too low, or the recorder's noise suppression may have removed the tones."));
        return new(blocks, scale, overhead, tones, notes);
    }

    /// <summary>CSAVE M の音なら読む。ヘッダと本体の搬送音がそろうのは CSAVE M だけなので、読めなくてもほかの方式には回さない（長い録音では何十分もかかる）。</summary>
    public static DecodeResult? TryTape(PcmAudio audio, Action<string, double>? progress = null) =>
        TapeDecoder.Looks(audio) ? TapeDecoder.Decode(audio, progress) : null;

    /// <summary>読めたブロックが抜けなく並び、読めなかったブロックが残っていなければ終わり。</summary>
    static bool Complete(Dictionary<int, List<Block>> votes, HashSet<int> failed)
    {
        if (votes.Count == 0 || failed.Any(a => !votes.ContainsKey(a))) return false;
        var addresses = votes.Keys.OrderBy(a => a).ToArray();
        for (int i = 1; i < addresses.Length; i++) if (addresses[i] != addresses[i - 1] + Decoder.DataBytes) return false;
        return true;
    }

    /// <summary>パターンに合わせて、チャンネルの選択・高域通過・音量のならしをする。</summary>
    public static float[] Prepare(PcmAudio audio, AnalysisPattern pattern)
    {
        int length = audio.Length;
        var left = audio.Channels[0];
        var right = audio.Channels[Math.Min(1, audio.Channels.Length - 1)];
        int count = audio.Channels.Length;
        var filter = new HighPass(audio.Rate, pattern.Cutoff);
        var x = new float[length];
        for (int i = 0; i < length; i++)
        {
            float v;
            switch (pattern.Channel)
            {
                case ChannelMix.Left: v = left[i]; break;
                case ChannelMix.Right: v = right[i]; break;
                case ChannelMix.Difference: v = (left[i] - right[i]) / 2f; break;
                default:
                    float sum = 0;
                    for (int c = 0; c < count; c++) sum += audio.Channels[c][i];
                    v = sum / count;
                    break;
            }
            x[i] = filter.Next(v / 32768f);
        }
        if (pattern.Level) Level(x, audio.Rate);
        return x;
    }

    /// <summary>ゆっくりした音量の変化（録音機材の自動音量調整など）をならす。</summary>
    static void Level(float[] x, int rate)
    {
        int window = Math.Max(1, rate / 5);
        int count = x.Length / window + 1;
        var rms = new double[count];
        for (int k = 0; k < count; k++)
        {
            double sum = 0;
            int from = k * window, to = Math.Min(x.Length, from + window);
            for (int i = from; i < to; i++) sum += (double)x[i] * x[i];
            rms[k] = to > from ? Math.Sqrt(sum / (to - from)) : 0;
        }
        double peak = rms.Max();
        if (peak <= 0) return;
        double Gain(int k) => 1 / Math.Max(rms[Math.Clamp(k, 0, count - 1)], peak * 0.02);
        for (int i = 0; i < x.Length; i++)
        {
            double position = (double)i / window - 0.5;
            int k = (int)Math.Floor(position);
            double f = position - k;
            x[i] = (float)Math.Clamp(x[i] * ((1 - f) * Gain(k) + f * Gain(k + 1)) * 0.05, -4, 4);
        }
    }
}
