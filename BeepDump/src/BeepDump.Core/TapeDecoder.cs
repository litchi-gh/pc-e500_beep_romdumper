using System.Numerics;

namespace BeepDump.Core;

/// <summary>
/// CSAVE M（カセットへの保存）がブザーから鳴らす音を読む。
/// ヘッダと本体の 2 区間があり、どちらも搬送音、長い周期の並び、短い周期の並び、長い周期 1 つ、バイトの並びの順に続く。
/// 1 周期が 1 ビットで、1 バイトは長い周期の開始ビットのあとにデータを上位ビットから送る（データの 1 が長い周期、0 が短い周期）。本体の最後に、データの 1 のビットの数（16 bit）が付く。
/// ブロックごとの検査値も番地もないので、1 回の録音では中身を確かめられない。読めたブロックは黄色にし、2 回の録音がそろって緑になる。
/// </summary>
public static class TapeDecoder
{
    // 搬送音の半周期に対する比。実機の録音で測った
    const double LongRatio = 2.4528, ShortRatio = 0.98183, GapRatio = 0.3377;
    // 区間の始まりの並びの周期（半周期に対する比）
    const double SyncLongRatio = 2.4156, SyncShortRatio = 0.9463;
    const int HeadCarrierCycles = 10000, BodyCarrierCycles = 11000;
    const int LeadInCycles = 16, NameEnd = 18, HeadBytes = 50, HeadSyncCycles = 40, DataSyncCycles = 20, TailBytes = 2;
    const int Phases = 8, Lookalikes = 6, RefitFrames = 300, RefitEvery = 1000, LearnFrames = 3000;
    const double LookGap = 120;
    const int FfFrames = 32;
    const double FfNoise = 0.35, FfContinue = 0.8, QuietFit = 0.95;
    const double LostResidual = 0.8, LostRatio = 1.6;
    const int LostFrames = 3, RecoverBack = 20, RecoverAhead = 30, BeamWidth = 64;
    const int RecheckAfter = 16, ResponseMilliseconds = 3, RefineMilliseconds = 10, RefineEvery = 8000, RecoverDrop = 6, DamageBefore = 8, ClusterGap = 64, StartCheck = 16, EndCheck = 12, PolishBehind = 8;
    const double DamagedLeft = 0.5, RecoverGain = 0.97, SameShape = 0.9, DeepRatio = 1.3, BurstLeft = 10, BurstShare = 0.25, QuietStart = 0.5;

    /// <summary>CSAVE M の音か。長い搬送音があれば CSAVE M とみなす（BASIC 版と機械語版にはない）。</summary>
    public static bool Looks(PcmAudio audio)
    {
        var carriers = FindCarriers(Loudest(audio), audio.Rate);
        for (int k = 0; k + 1 < carriers.Count; k++)
            if (Fits(carriers[k], HeadCarrierCycles, audio.Rate) && Fits(carriers[k + 1], BodyCarrierCycles, audio.Rate)) return true;
        return false;
    }

    static float[] Loudest(PcmAudio audio)
    {
        short[] best = audio.Channels[0];
        double top = -1;
        foreach (var channel in audio.Channels)
        {
            double sum = 0;
            for (int i = 0; i < channel.Length; i += 64) sum += (double)channel[i] * channel[i];
            if (sum > top) { top = sum; best = channel; }
        }
        return Flatten(best, 0, best.Length, audio.Rate);
    }

    /// <summary>ゆっくりした揺れを除く（約 2 ms の移動平均を引く）。</summary>
    static float[] Flatten(short[] s, int from, int to, int rate)
    {
        int half = Math.Max(1, rate / 990), n = to - from;
        var x = new float[n];
        double sum = 0;
        int count = 0, low = from, high = from;
        for (int i = from; i < to; i++)
        {
            while (high < Math.Min(s.Length, i + half + 1)) { sum += s[high++]; count++; }
            while (low < i - half) { sum -= s[low++]; count--; }
            x[i - from] = (float)(s[i] - sum / count);
        }
        return x;
    }

    readonly record struct Carrier(int Start, int End, double Half);

    /// <summary>
    /// 搬送音の区間を探す。同じ周期の繰り返しが 1.5 秒以上続く所。
    /// データの 00h の並びも短い周期が続くが、開始ビットで周期が乱れるので、繰り返しの強さで分けられる（実機の録音で搬送音 0.95 以上、00h の並び 0.61 以下）。
    /// </summary>
    static List<Carrier> FindCarriers(float[] x, int rate)
    {
        const int Misses = 2;     // 物音で一瞬乱れても、同じ区間とみなす窓の数
        var runs = new List<Carrier>();
        int hop = rate / 10, window = rate / 20, shortest = rate * 3 / 2;
        int minLag = rate / 4800, maxLag = rate / 2200 + 1;
        int start = -1, last = -1, missed = 0;
        var periods = new List<double>();
        void Finish()
        {
            if (start >= 0 && last - start >= shortest) runs.Add(new(start, last, periods.Order().ElementAt(periods.Count / 2) / 2));
            start = -1;
            periods.Clear();
        }
        for (int at = 0; at + window + maxLag + 2 < x.Length; at += hop)
        {
            double lag = RepeatLag(x, at, window, minLag, maxLag);
            if (lag > 0 && (start < 0 || Math.Abs(lag - periods[0]) < periods[0] * 0.03))
            {
                if (start < 0) start = at;
                last = at + window;
                missed = 0;
                periods.Add(lag);
                continue;
            }
            if (start >= 0 && ++missed <= Misses) continue;
            Finish();
            missed = 0;
            if (lag > 0) { start = at; last = at + window; periods.Add(lag); }
        }
        Finish();
        return runs;
    }

    /// <summary>窓の中の音が、とぎれずに同じ周期を繰り返していれば、その周期（サンプル）。違えば 0。</summary>
    static double RepeatLag(float[] x, int at, int window, int minLag, int maxLag)
    {
        const double Strong = 0.75;
        double power = 0;
        for (int i = at; i < at + window; i++) power += (double)x[i] * x[i];
        if (power / window < 16) return 0;
        Span<double> ac = stackalloc double[maxLag - minLag + 3];
        int bestLag = 0;
        double best = 0;
        for (int lag = minLag - 1; lag <= maxLag + 1; lag++)
        {
            double sum = 0;
            for (int i = at; i < at + window; i++) sum += (double)x[i] * x[i + lag];
            ac[lag - minLag + 1] = sum;
            if (lag >= minLag && lag <= maxLag && sum > best) { best = sum; bestLag = lag; }
        }
        if (bestLag == 0 || best / power < Strong) return 0;
        int part = window / 10;
        double loudest = 0, quietest = double.MaxValue;
        for (int k = 0; k < 10; k++)
        {
            double sum = 0;
            for (int i = at + k * part; i < at + (k + 1) * part; i++) sum += (double)x[i] * x[i];
            loudest = Math.Max(loudest, sum);
            quietest = Math.Min(quietest, sum);
        }
        if (quietest < loudest * 0.25) return 0;
        double a = ac[bestLag - minLag], b = ac[bestLag - minLag + 1], c = ac[bestLag - minLag + 2];
        return bestLag + Peak(a, b, c);
    }

    public static DecodeResult Decode(PcmAudio audio, Action<string, double>? progress = null)
    {
        var notes = new List<string>();
        var blocks = new List<Block>();
        int rate = audio.Rate, frames = 0, used = 0, runs = 0;
        var mono = Loudest(audio);
        var carriers = FindCarriers(mono, rate);
        for (int k = 0; k + 1 < carriers.Count; k++)
        {
            if (carriers[k].Start < used || !Fits(carriers[k], HeadCarrierCycles, rate) || !Fits(carriers[k + 1], BodyCarrierCycles, rate)) continue;
            var probe = ProbeHeader(audio, mono, carriers[k]);
            if (probe == null) continue;
            int part = k;
            var read = ReadBody(audio, mono, probe, carriers[k + 1], f => progress?.Invoke(Messages.Pick("CSAVE M の音を読み取り中", "Reading CSAVE M tones"), (part + f) / Math.Max(1, carriers.Count - 1)), notes);
            if (read == null) continue;
            runs++;
            blocks.AddRange(read.Blocks);
            frames += read.Frames;
            used = read.End;
            k++;
        }
        if (runs == 0) notes.Add(Messages.Pick("CSAVE M のヘッダが読めませんでした。録音の出だしが切れていないか確かめてください。", "Could not read the CSAVE M header. Check that the start of the sound was recorded."));
        blocks.Sort((a, b) => a.Address.CompareTo(b.Address));
        return new(blocks, 0, 0, frames, notes) { Tape = true };
    }

    /// <summary>搬送音の長さが想定どおりか。物音で少し短く切れることがある。</summary>
    static bool Fits(Carrier carrier, int cycles, int rate)
    {
        double measured = (carrier.End - carrier.Start) / (carrier.Half * 2);
        return measured > cycles * 0.75 && measured < cycles * 1.15;
    }

    /// <summary>ヘッダの位置と、最初の応答。応答はファイル名なしの形（01h、空白 16 個、0Dh）から学ぶ。名前があっても多くは空白なので、本体を読むきっかけには足りる。</summary>
    sealed record Probe(Reader Reader, double At, int From, double Half)
    {
        public int Rate => Reader.Rate;
    }

    static Probe? ProbeHeader(PcmAudio audio, float[] mono, Carrier carrier)
    {
        int rate = audio.Rate;
        double half = ExactHalf(mono, carrier);
        int stop = CarrierEnd(mono, carrier, half, rate);
        if (stop < 0) return null;
        int from = Math.Max(0, stop - rate / 4), to = Math.Min(audio.Length, stop + rate);
        var reader = new Reader(audio, from, to, rate, Reader.Timing.FromHalf(half));
        double syncLong = half * 2 * SyncLongRatio, syncShort = half * 2 * SyncShortRatio;
        double bestFit = -1, bestAt = 0;
        int range = rate / 150;
        for (int off = -range; off <= range; off += 2)
        {
            double fit = reader.FitSync(stop - from + off, half * 2, syncLong, syncShort, HeadSyncCycles);
            if (fit > bestFit) { bestFit = fit; bestAt = stop - from + off; }
        }
        double at = bestAt + HeadSyncCycles * (syncLong + syncShort) + syncLong;
        reader.Learn(at, [1, .. Enumerable.Repeat((byte)0x20, 16), 0x0D], cold: true);
        // 名前があれば読んだ値で学び直す。名前がなければ同じ結果になる
        var read = reader.ReadFrames(at, NameEnd);
        read[0] = 1;
        read[NameEnd - 1] = 0x0D;
        reader.Learn(at, read, cold: true);
        return new(reader, at, from, half);
    }

    /// <summary>
    /// よく学んだ応答で、本体と同じ読み方でヘッダを読む。位置を少しずつ変えて読み、開始番地と長さを多数決で決める。検査値まで合う読みを優先する。
    /// 決まった欄がそろった読みがなければ null。
    /// </summary>
    static byte[]? ReadHeader(Probe probe, Reader learned)
    {
        probe.Reader.UseLearned(learned);
        // 始まりの並びから決めた位置は、応答の持つ時間のずれの分だけ狂うので広く試す。ビット 1 つ分ずれると決まった欄が合わなくなるので、中身の検査で決められる
        int range = probe.Rate / 400;
        var votes = new Dictionary<string, (int Count, byte[] Bytes)>();
        for (int off = -range; off <= range; off++)
        {
            probe.Reader.Read(probe.At + off, double.MaxValue, int.MaxValue, _ => { }, HeadBytes);
            var candidate = probe.Reader.Result.Bytes.ToArray();
            if (candidate.Length != HeadBytes) continue;
            candidate[0] = 1;       // 最初のバイトは直前の音の響きで読み違えやすいが、値は決まっている
            // 半周期ずれて読むと、決まった欄が偶然そろっても番地や長さはでたらめになる。ここで外せば、位置をずらしてやり直せる
            if (!HeaderFixed(candidate) || !HeaderInMemory(candidate)) continue;
            int weight = HeaderValid(candidate) ? 3 : 1;
            string key = Convert.ToHexString(candidate[18..22]) + Convert.ToHexString(candidate[29..31]);
            votes.TryGetValue(key, out var v);
            votes[key] = (v.Count + weight, v.Bytes ?? candidate);
        }
        return votes.Count == 0 ? null : votes.Values.MaxBy(v => v.Count).Bytes;
    }

    /// <summary>ヘッダの決まった欄がそろっているか。</summary>
    static bool HeaderFixed(byte[] b)
    {
        if (b[17] != 0x0D) return false;
        int wrong = 0;
        for (int i = 24; i < 29; i++) if (b[i] != 0) wrong++;
        for (int i = 32; i < 49; i++) if (b[i] != 0) wrong++;
        return wrong <= 1;
    }

    static Header HeaderOf(byte[] b) => new(b[20] | b[21] << 8 | b[30] << 16, b[18] | b[19] << 8 | b[29] << 16);

    /// <summary>ヘッダの範囲がメモリの中に収まるか。</summary>
    static bool HeaderInMemory(byte[] b)
    {
        var header = HeaderOf(b);
        return header.Length > 0 && header.Start + header.Length <= 1 << 20;
    }

    static bool HeaderValid(byte[] b)
    {
        int count = 0;
        for (int i = 0; i < 49; i++) count += BitOperations.PopCount(b[i]);
        return b[17] == 0x0D && b[49] == (byte)count;
    }

    /// <summary>3 点から山の頂上の位置を補間する（-0.5〜0.5）。</summary>
    static double Peak(double a, double b, double c)
    {
        double curve = 2 * (a - 2 * b + c);
        return curve < 0 ? Math.Clamp((a - c) / curve, -0.5, 0.5) : 0;
    }

    /// <summary>搬送音の半周期を、長い間隔で測って正確にする。</summary>
    static double ExactHalf(float[] x, Carrier carrier)
    {
        double period = carrier.Half * 2;
        int cycles = 50, at = carrier.Start + (carrier.End - carrier.Start) / 4, window = Math.Min((carrier.End - carrier.Start) / 2, (int)(period * 400));
        int center = (int)Math.Round(period * cycles), span = Math.Max(2, (int)(period * cycles * 0.01));
        Span<double> ac = stackalloc double[2 * span + 1];
        int best = 0;
        for (int d = -span; d <= span; d++)
        {
            double sum = 0;
            for (int i = at; i < at + window && i + center + d < x.Length; i++) sum += (double)x[i] * x[i + center + d];
            ac[d + span] = sum;
            if (sum > ac[best]) best = d + span;
        }
        if (best == 0 || best == 2 * span) return carrier.Half;
        double a = ac[best - 1], b = ac[best], c = ac[best + 1];
        return (center + best - span + Peak(a, b, c)) / cycles / 2;
    }

    /// <summary>搬送音が終わる所。ここから周期が変わり、搬送音に戻らない。</summary>
    static int CarrierEnd(float[] x, Carrier carrier, double half, int rate)
    {
        int cycles = (int)(rate / 100 / (half * 2)) + 1;
        int lag = (int)Math.Round(half * 2 * cycles), window = rate / 200, step = rate / 500;
        double Match(int at)
        {
            double xy = 0, xx = 0, yy = 0;
            for (int i = at; i < at + window && i < x.Length; i++) { xy += (double)x[i] * x[i - lag]; xx += (double)x[i] * x[i]; yy += (double)x[i - lag] * x[i - lag]; }
            return xy / Math.Max(1, Math.Sqrt(xx * yy));
        }
        int limit = Math.Min(x.Length - window - rate / 5, carrier.End + rate);
        for (int at = carrier.Start + rate; at < limit; at += step)
        {
            if (Match(at) >= 0.4) continue;
            double later = 0;
            int count = 0;
            for (int q = at + rate / 50; q < at + rate / 5; q += rate / 50) { later += Match(q); count++; }
            if (later / count < 0.5) return at + step / 2;
        }
        return -1;
    }

    sealed record Header(int Start, int Length);

    sealed record BodyResult(List<Block> Blocks, int Frames, int End);

    static BodyResult? ReadBody(PcmAudio audio, float[] mono, Probe probe, Carrier carrier, Action<double> progress, List<string> notes)
    {
        int rate = audio.Rate;
        double half = ExactHalf(mono, carrier);
        int stop = CarrierEnd(mono, carrier, half, rate);
        if (stop < 0) return null;
        int soundEnd = SoundEnd(mono, stop, rate);
        int from = Math.Max(0, stop - rate / 10), to = Math.Min(audio.Length, soundEnd + rate / 10);
        var reader = new Reader(audio, from, to, rate, Reader.Timing.FromHalf(half));
        reader.UseLearned(probe.Reader);
        double syncLong = half * 2 * SyncLongRatio, syncShort = half * 2 * SyncShortRatio;
        // 始まりの並びに合わせて位置を決める。この時点の応答は粗いので、バイトを読んで広く探すと外れる
        double bestFit = -1, bestAt = 0;
        for (int off = -rate / 150; off <= rate / 150; off += 2)
        {
            double fit = reader.FitSync(stop - from + off, half * 2, syncLong, syncShort, DataSyncCycles);
            if (fit > bestFit) { bestFit = fit; bestAt = stop - from + off; }
        }
        double first = reader.FindStart(bestAt + DataSyncCycles * (syncLong + syncShort) + syncLong, rate / 16000);

        // 1 回目は応答とビットの長さを学ぶだけ。値のばらけたデータがたまったら、やり直す。
        // 最初の読みが半周期ずれたまま安定すると、応答の立ち上がりと立ち下がりが入れ替わって、つじつまだけ合う。ヘッダは中身を確かめられるので、読めなければ位置をずらしてやり直す
        var fresh = reader.Snapshot();
        byte[]? bytes = null;
        double nominal = first;
        foreach (double shift in new[] { 0.0, 0.5, -0.5, 1, -1, 1.5, -1.5, 2, -2 })
        {
            reader.Restore(fresh);
            first = nominal + shift * 2 * half;
            reader.Read(first, soundEnd - from, LearnFrames, f => progress(f * 0.1));
            reader.LearnFromRead();
            bytes = ReadHeader(probe, reader);
            if (bytes != null) break;
        }
        if (bytes == null) return null;
        first = reader.AlignQuiet(reader.FindStart(first, rate / 400, separately: true));
        var header = HeaderOf(bytes);
        // 2 回目が本番
        reader.Read(first, soundEnd - from, int.MaxValue, f => progress(0.1 + f * 0.8));
        var read = reader.Result;

        // 音の終わりの響きを読んだ分は捨てる
        reader.Refine(f => progress(0.9 + f * 0.1));
        if (soundEnd < audio.Length - rate / 20 && read.Clusters.Count == 0) reader.TrimTo(header.Length + TailBytes);
        // 番地を決める。本体には番地がなく、ヘッダの開始番地から数える。乱れで数がずれたら、その後ろは末尾から数え直す
        bool cut = soundEnd >= audio.Length - rate / 20;      // 録音が音の途中で終わっている
        int total = read.Bytes.Count, payload = cut ? total : total - TailBytes;
        int extra = payload - header.Length;
        var data = new byte[header.Length];
        var known = new bool[header.Length];
        var damaged = read.Clusters;
        void Copy(int from, int to, int shift)
        {
            for (int i = from; i < to; i++)
            {
                int address = i - shift;
                if (address < 0 || address >= header.Length) continue;
                data[address] = read.Bytes[i];
                known[address] = true;
            }
        }
        if (damaged.Count == 0)
        {
            if (extra == 0 || cut && extra < 0 || extra >= -TailBytes && extra < 0) Copy(0, Math.Min(total, header.Length), 0);
            else notes.Add(Messages.Pick($"読めたバイト数がヘッダと {extra:+0;-0} 違います。番地を決められないので、読めなかったことにします。", $"The bytes read differ from the header by {extra:+0;-0}. The addresses cannot be determined, so the blocks count as not read."));
        }
        else
        {
            Copy(0, damaged[0].First, 0);
            if (!cut) Copy(damaged[^1].Last + 1, payload, extra);
            // 乱れの間の所は、全体の長さが合うときだけ番地が決まる
            if (extra == 0) foreach (var (a, b) in damaged.Zip(damaged.Skip(1))) Copy(a.Last + 1, b.First, 0);
            else if (damaged.Count > 1) notes.Add(Messages.Pick($"乱れが {damaged.Count} か所あり、その間の番地を決められないので、読めなかったことにします。", $"There are {damaged.Count} disturbed places and the addresses between them cannot be determined, so those blocks count as not read."));
        }

        // 物音が大きかったバイトは信用しない。音に情報が残っていないので、録り直すしかない（実機の録音で、誤読の方が正解より音に合っていた）
        bool noisy = false;
        foreach (int n in read.Bursts)
        {
            int address = damaged.Count > 0 && n > damaged[^1].Last ? n - extra : n;
            if (address >= 0 && address < header.Length) { known[address] = false; noisy = true; }
        }

        // ブロックにする。端数のブロックは作らない
        var blocks = new List<Block>();
        int firstBlock = (header.Start + Decoder.DataBytes - 1) / Decoder.DataBytes * Decoder.DataBytes;
        for (int a = firstBlock; a + Decoder.DataBytes <= header.Start + header.Length; a += Decoder.DataBytes)
        {
            int offset = a - header.Start;
            bool all = true;
            for (int i = offset; i < offset + Decoder.DataBytes; i++) all &= known[i];
            if (all) blocks.Add(new(a, BlockState.Repaired, data[offset..(offset + Decoder.DataBytes)]));
            else if (!cut || offset < payload) blocks.Add(new(a, BlockState.Failed, []));
        }

        // 全体の検査値
        long bits = 0;
        bool whole = known.All(k => k);
        for (int i = 0; i < data.Length; i++) bits += BitOperations.PopCount(data[i]);
        int tail = !cut && extra == 0 ? read.Bytes[total - 2] << 8 | read.Bytes[total - 1] : -1;
        string range = $"{header.Start:X5}-{header.Start + header.Length - 1:X5}";
        double seconds = Math.Max(1e-3, (read.EndTime - first) / rate);
        notes.Add(Messages.Pick($"CSAVE M {range}、約 {payload * 8 / seconds:F0} bps", $"CSAVE M {range}, about {payload * 8 / seconds:F0} bps"));
        if (cut) notes.Add(Messages.Pick("録音が音の途中で終わっています。読めた所までを使います。", "The recording ends before the sound does. What was read up to there is used."));
        else if (!whole) notes.Add(Messages.Pick(noisy ? "物音の入った所があり、そのブロックは読めませんでした。その範囲だけ録り直してください。" : "音の乱れた所があり、そのブロックは読めませんでした。", noisy ? "Noise covered some of the sound, and those blocks could not be read. Record just those ranges again." : "Some of the sound was disturbed, and those blocks could not be read."));
        else if ((bits & 0xFFFF) == tail || tail >= 0 && reader.TailFits((int)(bits & 0xFFFF))) notes.Add(Messages.Pick("全体の検査値は合いました。ただしビットの入れ替わりは検査値で分からないので、もう 1 回録音して確かめてください。", "The overall check value matched. It cannot catch swapped bits, so record it once more to confirm."));
        else notes.Add(Messages.Pick("全体の検査値が合いません。データか検査値のどちらかを読み違えています。もう 1 回録音して比べてください。", "The overall check value does not match: either the data or the check value itself was misread. Record it once more to compare."));
        int leftover = cut ? 0 : header.Length - blocks.Count * Decoder.DataBytes;
        if (leftover > 0) notes.Add(Messages.Pick($"256 バイトに満たない端の {leftover} バイトは使いません。", $"The {leftover} bytes at the ends that do not fill a 256-byte block are not used."));
        return new(blocks, total, soundEnd);
    }

    /// <summary>音が止まる所。</summary>
    static int SoundEnd(float[] x, int from, int rate)
    {
        int window = rate / 100;
        var level = new List<double>();
        for (int at = from; at + window < x.Length; at += window)
        {
            double sum = 0;
            for (int i = at; i < at + window; i++) sum += (double)x[i] * x[i];
            level.Add(Math.Sqrt(sum / window));
        }
        if (level.Count == 0) return x.Length;
        double typical = level.Where(v => v > 0).Order().ElementAt(level.Count(v => v > 0) * 2 / 3);
        for (int k = 0; k + 4 < level.Count; k++)
        {
            if (level[k] >= typical * 0.35) continue;
            if (level.Skip(k).Take(5).All(v => v < typical * 0.35)) return from + k * window;
        }
        return x.Length;
    }

    /// <summary>バイトの並びを、ブザーの応答を重ね合わせた音と比べて読む。</summary>
    sealed class Reader
    {
        public readonly record struct Timing(double Long, double Short, double Gap)
        {
            public static Timing FromHalf(double half) => new(half * LongRatio, half * ShortRatio, half * GapRatio);
        }

        public sealed record Cluster(int First, int Last);

        public sealed class ReadResult
        {
            public List<byte> Bytes = [];
            public List<double> Times = [];
            public List<Cluster> Clusters = [];
            public List<int> Bursts = [];
            public double EndTime;
        }

        static readonly bool[][] LongBits = Enumerable.Range(0, 256).Select(b => Enumerable.Range(0, 9).Select(j => j == 0 || (b >> 8 - j & 1) == 1).ToArray()).ToArray();

        readonly int channels, rate;
        int tap;
        public int Rate => rate;
        readonly float[][] x, r, swapBuffer;
        readonly double[] noise, local;
        readonly Dictionary<(int Channel, int Phase), float[]> templates = [];
        readonly double[] metric = new double[256], energy = new double[256];
        readonly List<(int Value, double Time)> recent = [];
        Timing timing;
        double[] span = new double[256];
        int[] lengths = new int[256];
        int widest;
        double learnedDrift;
        double[][]? anchorUp, anchorDown;
        public double[][] Up, Down;
        public ReadResult Result = new();

        public Reader(PcmAudio audio, int from, int to, int rate, Timing timing)
        {
            channels = audio.Channels.Length;
            this.rate = rate;
            tap = rate * ResponseMilliseconds / 1000;
            x = audio.Channels.Select(c => Flatten(c, from, to, rate)).ToArray();
            r = x.Select(c => (float[])c.Clone()).ToArray();
            noise = new double[channels];
            swapBuffer = Enumerable.Range(0, channels).Select(_ => new float[4096]).ToArray();
            local = new double[channels];
            Up = Enumerable.Range(0, channels).Select(_ => new double[tap]).ToArray();
            Down = Enumerable.Range(0, channels).Select(_ => new double[tap]).ToArray();
            SetTiming(timing);
        }

        /// <summary>今の応答とビットの長さを写し取る。</summary>
        public (double[][] Up, double[][] Down, Timing Timing, double Drift) Snapshot() => (Up.Select(h => (double[])h.Clone()).ToArray(), Down.Select(h => (double[])h.Clone()).ToArray(), timing, learnedDrift);

        /// <summary>写し取った状態に戻す。</summary>
        public void Restore((double[][] Up, double[][] Down, Timing Timing, double Drift) state)
        {
            for (int c = 0; c < channels; c++) { Up[c] = (double[])state.Up[c].Clone(); Down[c] = (double[])state.Down[c].Clone(); }
            SetTiming(state.Timing);
            learnedDrift = state.Drift;
            anchorUp = anchorDown = null;
            ResetNoise();
        }

        /// <summary>別の読み手が学んだ応答とビットの長さを使う。</summary>
        public void UseLearned(Reader other)
        {
            for (int c = 0; c < channels; c++) { Up[c] = (double[])other.Up[c].Clone(); Down[c] = (double[])other.Down[c].Clone(); }
            SetTiming(other.timing);
            learnedDrift = other.learnedDrift;
            ResetNoise();
        }

        void SetTiming(Timing t)
        {
            timing = t;
            for (int b = 0; b < 256; b++)
            {
                span[b] = t.Gap;
                foreach (bool on in LongBits[b]) span[b] += 2 * (on ? t.Long : t.Short);
                lengths[b] = (int)(span[b] + 2 * t.Long);
            }
            widest = (int)(18 * t.Long + t.Gap + 2 * t.Long) + 2;
            templates.Clear();
        }

        float[] Template(int channel, int phase)
        {
            if (templates.TryGetValue((channel, phase), out var v)) return v;
            v = new float[256 * widest];
            var line = new float[widest + tap + 4];
            for (int b = 0; b < 256; b++)
            {
                Array.Clear(line);
                double end = Put(line, channel, b, phase / (double)Phases, 1);
                PutEdge(line, channel, 0, end, 1);
                PutEdge(line, channel, 1, end + timing.Long, 1);
                Array.Copy(line, 0, v, b * widest, widest);
            }
            templates[(channel, phase)] = v;
            return v;
        }

        double Put(float[] line, int channel, int value, double t, float sign)
        {
            foreach (bool on in LongBits[value])
            {
                double half = on ? timing.Long : timing.Short;
                PutEdge(line, channel, 0, t, sign);
                PutEdge(line, channel, 1, t + half, sign);
                t += 2 * half;
            }
            return t + timing.Gap;
        }

        void PutEdge(float[] line, int channel, int kind, double t, float sign)
        {
            int i = (int)Math.Floor(t);
            if (i < 0 || i >= line.Length) return;
            float f = (float)(t - i), a = sign * (1 - f), b = sign * f;
            var h = kind == 0 ? Up[channel] : Down[channel];
            int n = Math.Min(tap, line.Length - i - 1);
            for (int k = 0; k < n; k++) { float v = (float)h[k]; line[i + k] += a * v; line[i + k + 1] += b * v; }
        }

        /// <summary>t から始まるバイトの 256 通りの当てはまり。小さいほど良い。</summary>
        void Score(double t, float[][]? segments = null, int shift = 0)
        {
            int i = (int)Math.Floor(t), phase = Math.Clamp((int)((t - i) * Phases), 0, Phases - 1);
            Array.Clear(metric);
            for (int c = 0; c < channels; c++)
            {
                var table = Template(c, phase);
                var source = segments == null ? r[c] : segments[c];
                int at = segments == null ? i : shift;
                double bestRaw = double.MaxValue;
                for (int b = 0; b < 256; b++)
                {
                    double e = Distance(source.AsSpan(at, lengths[b]), table.AsSpan(b * widest, lengths[b]));
                    energy[b] = e;
                    if (e / lengths[b] < bestRaw) bestRaw = e / lengths[b];
                }
                if (noise[c] <= 0) noise[c] = Math.Max(bestRaw, 1);
                local[c] = noise[c];
                for (int b = 0; b < 256; b++) metric[b] += energy[b] / noise[c] - lengths[b];
            }
        }

        static double Distance(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
        {
            int width = Vector<float>.Count, n = a.Length, i = 0;
            var total = Vector<float>.Zero;
            double sum = 0;
            int pending = 0;
            for (; i + width <= n; i += width)
            {
                var d = new Vector<float>(a.Slice(i, width)) - new Vector<float>(b.Slice(i, width));
                total += d * d;
                if (++pending == 64) { sum += Vector.Sum(total); total = Vector<float>.Zero; pending = 0; }
            }
            sum += Vector.Sum(total);
            for (; i < n; i++) { double d = a[i] - b[i]; sum += d * d; }
            return sum;
        }

        int Best()
        {
            int best = 0;
            for (int b = 1; b < 256; b++) if (metric[b] < metric[best]) best = b;
            return best;
        }

        void Subtract(float[][] target, int value, double t)
        {
            for (int c = 0; c < channels; c++) Add(target[c], c, value, t, -1);
        }

        void Add(float[] target, int channel, int value, double t, float sign)
        {
            foreach (bool on in LongBits[value])
            {
                double half = on ? timing.Long : timing.Short;
                AddEdge(target, channel, 0, t, sign);
                AddEdge(target, channel, 1, t + half, sign);
                t += 2 * half;
            }
        }

        void AddEdge(float[] target, int channel, int kind, double t, float sign)
        {
            int i = (int)Math.Floor(t);
            if (i < 0 || i + tap + 1 >= target.Length) return;
            float f = (float)(t - i), a = sign * (1 - f), b = sign * f;
            var h = kind == 0 ? Up[channel] : Down[channel];
            for (int k = 0; k < tap; k++) { float v = (float)h[k]; target[i + k] += a * v; target[i + k + 1] += b * v; }
        }

        double After(double t, int value) => t + span[value];

        public double After(double t, byte[] values)
        {
            foreach (byte v in values) t += span[v];
            return t;
        }

        // ---- 学習 ----

        /// <summary>切り替えの時刻の並びから、応答を最小二乗で求める（共役勾配法）。</summary>
        double Fit(int channel, List<(int Kind, double Time)> edges, int from, int to, bool cold, double weight, int rounds)
        {
            from = Math.Max(0, from);
            to = Math.Min(x[channel].Length - 1, to);
            int n = to - from, m = 2 * tap;
            var target = new double[n];
            for (int i = 0; i < n; i++) target[i] = x[channel][from + i];
            var solution = new double[m];
            if (!cold) { Up[channel].CopyTo(solution, 0); Down[channel].CopyTo(solution, tap); }
            var old = (double[])solution.Clone();
            var y = new double[n];

            void Forward(double[] h, double[] output)
            {
                Array.Clear(output);
                foreach (var (kind, time) in edges)
                {
                    int i = (int)Math.Floor(time) - from, col = kind * tap;
                    double f = time - Math.Floor(time);
                    for (int k = 0; k < tap; k++)
                    {
                        int at = i + k;
                        if (at >= 0 && at < n) output[at] += (1 - f) * h[col + k];
                        if (at + 1 >= 0 && at + 1 < n) output[at + 1] += f * h[col + k];
                    }
                }
            }
            void Backward(double[] v, double[] output)
            {
                Array.Clear(output);
                foreach (var (kind, time) in edges)
                {
                    int i = (int)Math.Floor(time) - from, col = kind * tap;
                    double f = time - Math.Floor(time);
                    for (int k = 0; k < tap; k++)
                    {
                        int at = i + k;
                        if (at >= 0 && at < n) output[col + k] += (1 - f) * v[at];
                        if (at + 1 >= 0 && at + 1 < n) output[col + k] += f * v[at + 1];
                    }
                }
            }
            double lambda = weight * edges.Count * 0.75 / 2;
            var work = new double[m];
            void Normal(double[] h, double[] output)
            {
                Forward(h, y);
                Backward(y, output);
                for (int k = 0; k < m; k++) output[k] += lambda * h[k];
            }
            var right = new double[m];
            Backward(target, right);
            for (int k = 0; k < m; k++) right[k] += lambda * old[k];
            Normal(solution, work);
            var residual = new double[m];
            var direction = new double[m];
            double rr = 0;
            for (int k = 0; k < m; k++) { residual[k] = right[k] - work[k]; direction[k] = residual[k]; rr += residual[k] * residual[k]; }
            for (int round = 0; round < rounds && rr > 1e-6; round++)
            {
                Normal(direction, work);
                double dq = 0;
                for (int k = 0; k < m; k++) dq += direction[k] * work[k];
                if (dq <= 0) break;
                double alpha = rr / dq, next = 0;
                for (int k = 0; k < m; k++) { solution[k] += alpha * direction[k]; residual[k] -= alpha * work[k]; next += residual[k] * residual[k]; }
                double beta = next / rr;
                rr = next;
                for (int k = 0; k < m; k++) direction[k] = residual[k] + beta * direction[k];
            }
            Forward(solution, y);
            double error = 0, power = 0;
            for (int i = 0; i < n; i++) { double d = target[i] - y[i]; error += d * d; power += target[i] * target[i]; }
            double fit = power > 0 ? 1 - error / power : 0;
            Array.Copy(solution, 0, Up[channel], 0, tap);
            Array.Copy(solution, tap, Down[channel], 0, tap);
            return fit;
        }

        /// <summary>区間の始まりの並びに合わせて応答を求め、当てはまりを返す。</summary>
        public double FitSync(double stop, double carrierCycle, double longCycle, double shortCycle, int cycles, bool keep = false)
        {
            var edges = new List<(int, double)>();
            double t = stop - 20 * carrierCycle;
            int from = (int)t + 1;
            for (int k = 0; k < 20; k++) { edges.Add((0, t)); edges.Add((1, t + carrierCycle / 2)); t += carrierCycle; }
            for (int k = 0; k < cycles; k++) { edges.Add((0, t)); edges.Add((1, t + longCycle / 2)); t += longCycle; }
            for (int k = 0; k < cycles; k++) { edges.Add((0, t)); edges.Add((1, t + shortCycle / 2)); t += shortCycle; }
            int to = (int)t - 1;
            double total = 0;
            var saveUp = Up.Select(h => (double[])h.Clone()).ToArray();
            var saveDown = Down.Select(h => (double[])h.Clone()).ToArray();
            int use = keep ? channels : 1;
            for (int c = 0; c < use; c++)
                total += Fit(c, edges, from, to, cold: true, 1e-3, keep ? 40 : 12);
            if (keep) { templates.Clear(); ResetNoise(); }
            else for (int c = 0; c < channels; c++) { Up[c] = saveUp[c]; Down[c] = saveDown[c]; }
            return total / use;
        }

        void ResetNoise() => Array.Clear(noise);

        /// <summary>値の分かったバイト列から応答を学ぶ。</summary>
        public void Learn(double t, byte[] values, bool cold)
        {
            var edges = new List<(int, double)>();
            double at = t;
            foreach (byte v in values)
            {
                foreach (bool on in LongBits[v])
                {
                    double half = on ? timing.Long : timing.Short;
                    edges.Add((0, at));
                    edges.Add((1, at + half));
                    at += 2 * half;
                }
                at += timing.Gap;
            }
            for (int c = 0; c < channels; c++) Fit(c, edges, (int)t + 2, (int)at - 2, cold, cold ? 1e-3 : 0.02, cold ? 40 : 15);
            templates.Clear();
            ResetNoise();
        }

        // ---- 読む ----

        void Reset()
        {
            for (int c = 0; c < channels; c++) Array.Copy(x[c], r[c], x[c].Length);
            ResetNoise();
            recent.Clear();
            Result = new();
        }

        /// <summary>最初のバイトの前の音（短い周期の並びと、長い周期 1 つ）の響きを引く。</summary>
        void SubtractLeadIn(float[][] target, double t, float sign = -1)
        {
            double at = t - 2 * timing.Long;
            for (int c = 0; c < channels; c++)
            {
                AddEdge(target[c], c, 0, at, sign);
                AddEdge(target[c], c, 1, at + timing.Long, sign);
                double q = at, half = timing.Short * SyncShortRatio / ShortRatio;
                for (int k = 0; k < LeadInCycles; k++)
                {
                    q -= 2 * half;
                    AddEdge(target[c], c, 0, q, sign);
                    AddEdge(target[c], c, 1, q + half, sign);
                }
            }
        }

        /// <summary>決まった数のバイトを読む（ヘッダ用）。最後のバイトの後ろには何もない。</summary>
        public byte[] ReadFrames(double t, int count)
        {
            ResetNoise();
            int length = (int)(count * span.Max()) + widest + tap + 8;
            int back = (int)(2 * timing.Long + 32 * timing.Short) + tap;
            int from = Math.Max(0, (int)t - back);
            if (from + back + length >= x[0].Length) length = x[0].Length - from - back - 1;
            length += back;
            var windows = x.Select(c => c.AsSpan(from, length).ToArray()).ToArray();
            var values = new byte[count];
            double at = t - from;
            SubtractLeadIn(windows, at);
            for (int n = 0; n < count; n++)
            {
                if ((int)at + widest >= length) break;
                ScoreIn(windows, at);
                int b = n == count - 1 ? BestLast(windows, at) : Best();
                values[n] = (byte)b;
                Subtract(windows, b, at);
                at = After(at, b) + learnedDrift;
            }
            return values;
        }

        /// <summary>次のバイトがないものとして、最も合う値を選ぶ。</summary>
        int BestLast(float[][] source, double t)
        {
            int from = (int)Math.Floor(t), best = 0;
            double top = double.MaxValue;
            for (int b = 0; b < 256; b++)
            {
                int length = (int)span[b] + tap / 2;
                if (from + length + tap + 4 >= source[0].Length) continue;
                double sum = 0;
                for (int c = 0; c < channels; c++)
                {
                    var line = new float[length + tap + 4];
                    Put(line, c, b, t - from, 1);
                    sum += Distance(source[c].AsSpan(from, length), line.AsSpan(0, length)) / Math.Max(noise[c], 1) - length;
                }
                if (sum < top) { top = sum; best = b; }
            }
            return best;
        }

        void ScoreIn(float[][] windows, double t)
        {
            int i = (int)Math.Floor(t);
            Score(t, windows, i);
        }

        /// <summary>
        /// 最初のバイトの位置を探す。読んだバイトを引いたあとに残る音が一番少ない所。
        /// バイトごとの当てはまりで比べると、同じ値が続くデータでは差が出ない。決まった長さの区間に残る音なら比べられる。
        /// </summary>
        public double FindStart(double guess, int range, bool separately = false)
        {
            const int Probe = 24;
            double best = double.MaxValue, at = guess;
            int back = (int)(2 * timing.Long + 32 * timing.Short) + tap;
            int measured = (int)(Probe * span.Min());
            ResetNoise();
            for (int off = -range; off <= range; off++)
            {
                // 雑音の見積もりは最初に比べた所で決まる。広く探すときは候補ごとにやり直す。持ち越すと、範囲の端の音で結果が変わる（実機の録音で、先頭が 0Fh のとき 74 サンプル遅れて 01h に読めた）
                if (separately) ResetNoise();
                double t = guess + off;
                int from = Math.Max(0, (int)t - back);
                int length = Math.Min(x[0].Length - from - 1, back + (int)(Probe * span.Max()) + widest + tap + 8);
                var windows = x.Select(c => c.AsSpan(from, length).ToArray()).ToArray();
                double local = t - from, begin = local;
                SubtractLeadIn(windows, local);
                for (int n = 0; n < Probe && (int)local + widest < length; n++)
                {
                    ScoreIn(windows, local);
                    int b = Best();
                    Subtract(windows, b, local);
                    local = After(local, b);
                }
                double left = 0;
                for (int c = 0; c < channels; c++)
                {
                    int s = (int)begin, e = Math.Min(length, s + measured);
                    for (int i = s; i < e; i++) left += (double)windows[c][i] * windows[c][i];
                }
                if (left < best) { best = left; at = t; }
            }
            return at;
        }

        /// <summary>
        /// データが FFh の並びで始まるとき、バイトの区切りの位置を決める。FFh は長いセルが続くので、区切りがセル単位でずれてもほとんど同じ音になり、次に来るデータを読み違える。
        /// 区切りごとに入る短い隙間だけが手がかりなので、たくさんのバイトの音で比べる。
        /// </summary>
        public double AlignQuiet(double start)
        {
            const int Bytes = 64;
            double cell = 2 * timing.Long, period = span[0xFF];
            int length = (int)(Bytes * period);
            ResetNoise();
            int from = (int)(start - 9 * cell) - tap - 4;
            // 学んだ応答の時間のずれの分だけ位置が狂っているので、1 サンプルずつ試す
            if (from < 0 || from + length + 2 * (int)(9 * cell) + tap + 8 >= x[0].Length) return start;
            double best = double.MaxValue, at = start;
            int reach = (int)(4 * cell);
            for (int k = -reach; k <= reach; k++)
            {
                double t = start + k;
                var windows = x.Select(c => c.AsSpan(from, length + 2 * (int)(9 * cell) + 2 * tap).ToArray()).ToArray();
                double local = t - from;
                for (int n = 0; n < Bytes; n++, local += period)
                    for (int c = 0; c < channels; c++) Add(windows[c], c, 0xFF, local, -1);
                double left = 0;
                // どの候補でもバイトが並んでいる区間で比べる
                int low = (int)(start - from + 9 * cell), high = low + length - (int)(4 * period);
                for (int c = 0; c < channels; c++) left += Energy(windows[c].AsSpan(low, high - low));
                double power = 0;
                for (int c = 0; c < channels; c++) power += Energy(x[c].AsSpan(from + low, high - low));
                double share = power > 0 ? left / power : double.MaxValue;
                if (share < best) { best = share; at = t; }
            }
            // FFh の並びでなければ、どの位置でも大きく残る。そのときは動かさない
            return best <= QuietStart ? at : start;
        }

        /// <summary>end（音の終わり）まで、または limit バイトまで読む。</summary>
        public void Read(double start, double end, int limit, Action<double> progress, int maxBytes = int.MaxValue)
        {
            Reset();
            SubtractLeadIn(r, start);
            var read = Result;
            double t = start, drift = learnedDrift;
            int bad = 0, sinceRefit = 0;
            bool quiet = true;   // FFh の並びの中か
            int same = maxBytes == int.MaxValue ? FfFrames : 0;
            var look = x.Select(_ => new float[2 * widest + tap + 16]).ToArray();
            var segments = x.Select(_ => new float[widest]).ToArray();
            int varied = 0;
            int committed = 0, polished = 0, recheck = -1;
            double typical = 0;
            for (int n = 0; varied < limit && read.Bytes.Count < maxBytes; n = read.Bytes.Count)
            {
                if (t >= end + rate / 50.0 || (int)t + 2 * widest + tap + 8 >= x[0].Length) break;
                if (n % 2048 == 0) progress(Math.Clamp((t - start) / Math.Max(1, end - start), 0, 1));
                read.Times.Add(t);
                int i = (int)Math.Floor(t), value;
                quiet = same >= FfFrames;
                bool wasQuiet = quiet;
                int skipped = quiet && !StaysQuiet(t) ? SkipNoise(t, drift) : 0;
                if (skipped > 0)
                {
                    // 物音の間も FFh の周期は変わらないので、時間で数えて FFh を置く
                    read.Times.RemoveAt(read.Times.Count - 1);
                    for (int k = 0; k < skipped; k++)
                    {
                        read.Times.Add(t);
                        read.Bytes.Add(0xFF);
                        Subtract(r, 0xFF, t);
                        t = After(t, 0xFF) + drift;
                    }
                    recent.Clear();
                    continue;
                }
                if (quiet && StaysQuiet(t)) value = 0xFF;
                else
                {
                    Score(t);
                    value = Best();
                    double fraction = Fraction(i, value);
                    // 音の終わりの無音を読まない
                    if (end - t < rate / 30.0 && fraction > 0.95 && Silent(i)) { read.Times.RemoveAt(read.Times.Count - 1); break; }
                    if (quiet && value != 0xFF && Continues(t)) value = 0xFF;
                    else
                    {
                        // 雑音の多い録音は普段から残りが多いので、その録音の普段の値と比べる
                        bool off = fraction > Math.Max(LostResidual, typical * LostRatio);
                        bad = off ? bad + 1 : 0;
                        if (!off) typical = typical <= 0 ? fraction : 0.995 * typical + 0.005 * fraction;
                        if (bad >= LostFrames && !quiet && maxBytes == int.MaxValue && end - t > rate / 100.0)
                        {
                            bad = 0;
                            read.Times.RemoveAt(read.Times.Count - 1);
                            int from = Math.Max(committed, read.Bytes.Count - RecoverBack);
                            var (next2, changed, damaged) = Recover(read, from, t, end, drift, typical);
                            if (changed)
                            {
                                if (damaged) Damage(read, read.Bytes.Count - 1);
                                committed = read.Bytes.Count;
                                recent.Clear();
                                t = next2;
                                same = 0;
                                continue;
                            }
                            committed = read.Bytes.Count;
                            read.Times.Add(t);
                            Score(t);
                            value = Best();
                        }
                        value = LookAhead(t, value, look, segments);
                    }
                }
                Subtract(r, value, t);
                double next = After(t, value);
                double step = Track(t, value);
                drift = Math.Clamp(drift + 0.01 * step, -0.4, 0.4);
                read.Bytes.Add((byte)value);
                if (value != 0xFF) varied++;
                recent.Add((value, t));
                same = value == 0xFF ? same + 1 : 0;
                t = next + 0.25 * step + drift;
                UpdateNoise(i, (int)next + tap);
                // 先頭の数バイトは直前の音の響きで読み違えやすいので、後ろが読めてから読み直す
                if (maxBytes == int.MaxValue && read.Bytes.Count == StartCheck) FixFirst(read);
                if (recheck >= 0 && read.Bytes.Count >= recheck + RecheckAfter)
                {
                    var (next5, changed5, _) = Recover(read, recheck, t, end, drift, typical);
                    if (changed5) { t = next5; recent.Clear(); }
                    committed = read.Bytes.Count;
                    recheck = -1;
                }
                if (wasQuiet && value != 0xFF && maxBytes == int.MaxValue) recheck = read.Bytes.Count - 1;
                if (++sinceRefit >= RefitEvery && value != 0xFF)
                {
                    sinceRefit = 0;
                    if (maxBytes == int.MaxValue) { Polish(read, polished, read.Bytes.Count - PolishBehind); polished = Math.Max(polished, read.Bytes.Count - PolishBehind); }
                    Refit();
                }
                if (recent.Count > RefitFrames * 2) recent.RemoveRange(0, RefitFrames);
            }
            read.EndTime = t;
            learnedDrift = drift;
            if (maxBytes == int.MaxValue)
            {
                // 音の終わりの近くは、読み直しの判定が働く前に終わるので、ここで読み直す
                if (read.Bytes.Count > EndCheck)
                {
                    // 音の後ろの無音を読まないように、バイトの数は変えない
                    var (next4, changed4, _) = Recover(read, read.Bytes.Count - EndCheck, t, t, drift, typical, EndCheck);
                    if (changed4) read.EndTime = next4;
                }
                Polish(read, polished, read.Bytes.Count);
                CountQuiet(read);
            }
            else Polish(read, 0, read.Bytes.Count);
            // 最後のバイトは次のバイトがないので、開始ビットのない形で比べ直す
            if (read.Bytes.Count > 0) FixLast(read);
        }

        /// <summary>
        /// 最終工程。読み終わったバイトで、長い応答を区間ごとに学び直して総点検する。
        /// 読む間は速さのために応答を短くしているが、筐体の響きは 10 ms ほど残る。長い応答で比べると、ビットの入れ替わりと正しい読みの差が大きく開く（実機の録音で 10 倍以上）。
        /// </summary>
        public void Refine(Action<double> progress)
        {
            var read = Result;
            int count = read.Bytes.Count;
            if (count < RefitFrames) return;
            int longTap = rate * RefineMilliseconds / 1000;
            if (longTap <= tap) return;
            for (int c = 0; c < channels; c++)
            {
                Array.Resize(ref Up[c], longTap);
                Array.Resize(ref Down[c], longTap);
            }
            tap = longTap;
            templates.Clear();
            for (int c = 0; c < channels; c++) Array.Copy(x[c], r[c], x[c].Length);
            SubtractLeadIn(r, read.Times[0]);
            int done = 0;
            for (int part = 0; part < count; part += RefineEvery)
            {
                progress((double)part / count);
                int stop = Math.Min(count, part + RefineEvery);
                RefitAround(read, part, stop);
                for (int c = 0; c < channels; c++)
                    for (int n = part; n < stop; n++) Add(r[c], c, read.Bytes[n], read.Times[n], -1);
                for (int c = 0; c < channels; c++)
                {
                    int from = (int)read.Times[part], to = Math.Min((int)read.Times[stop - 1], r[c].Length);
                    if (to > from) noise[c] = Math.Max(Energy(r[c].AsSpan(from, to - from)) / (to - from), 1);
                }
                int upto = stop == count ? count : stop - PolishBehind;
                Polish(read, done, upto);
                // 物音が大きくて読みを信用できないバイトを覚えておく
                for (int n = done; n < upto; n++)
                {
                    // FFh の並びの中は時間で数えているので、物音があっても問題ない
                    bool quietHere = read.Bytes[n] == 0xFF && (n == 0 || read.Bytes[n - 1] == 0xFF) && (n + 1 >= count || read.Bytes[n + 1] == 0xFF);
                    if (!quietHere && Noisy(read, n)) read.Bursts.Add(n);
                }
                done = upto;
            }
        }

        /// <summary>区間の中の、値のばらけた所で応答を学ぶ。音量や置き方が録音の途中で変わるので、区間ごとに学ぶ。</summary>
        void RefitAround(ReadResult read, int part, int stop)
        {
            const int Probe = 2000;
            for (int at = part; at + RefitFrames <= stop; at += Probe)
            {
                var values = read.Bytes.Skip(at).Take(RefitFrames).ToArray();
                if (values.Distinct().Count() < 40) continue;
                var edges = new List<(int, double)>();
                for (int k = 0; k < RefitFrames; k++)
                {
                    double t = read.Times[at + k];
                    foreach (bool on in LongBits[values[k]])
                    {
                        double half = on ? timing.Long : timing.Short;
                        edges.Add((0, t));
                        edges.Add((1, t + half));
                        t += 2 * half;
                    }
                }
                int from = (int)read.Times[at] + 2, to = (int)read.Times[at + RefitFrames - 1];
                for (int c = 0; c < channels; c++) Fit(c, edges, from, to, cold: false, 0.02, 25);
                return;
            }
        }

        /// <summary>
        /// 読み終わった所を仕上げる。隣り合うビットの入れ替わりは時間が変わらないので、読み進める間は気づけない。
        /// 後ろのバイトまで引いた今なら、入れ替えて残りの音が減るかを直接比べられる。バイトの切れ目をまたぐ入れ替わり（前のバイトの最下位と次のバイトの最上位）も試す。
        /// </summary>
        void Polish(ReadResult read, int from, int to)
        {
            from = Math.Max(0, from);
            to = Math.Min(to, read.Bytes.Count);
            for (int n = from; n < to; n++)
            {
                int a = read.Bytes[n];
                if (a == 0xFF && (n + 1 >= read.Bytes.Count || read.Bytes[n + 1] == 0xFF)) continue;
                for (int j = 0; j < 7; j++)
                {
                    if ((a >> j & 1) == (a >> j + 1 & 1)) continue;
                    int candidate = a ^ 3 << j;
                    if (TrySwap(read, n, a, candidate, -1, -1))
                    {
                        read.Bytes[n] = (byte)candidate;
                        a = candidate;
                    }
                }
                // 残りの多いバイトは、1 の数が同じ値をすべて試す。ビットが 2 つ以上動いた読み違いも直せる
                if (LeftAt(read, n) > DeepRatio)
                    foreach (int candidate in SameOnes[BitOperations.PopCount((uint)a)])
                    {
                        if (candidate == a) continue;
                        if (TrySwap(read, n, a, candidate, -1, -1))
                        {
                            read.Bytes[n] = (byte)candidate;
                            a = candidate;
                        }
                    }
                if (n + 1 < read.Bytes.Count)
                {
                    int b = read.Bytes[n + 1];
                    if ((a & 1) != b >> 7 && TrySwap(read, n, a, a ^ 1, b, b ^ 0x80))
                    {
                        read.Times[n + 1] += span[a ^ 1] - span[a];
                        read.Bytes[n] = (byte)(a ^ 1);
                        read.Bytes[n + 1] = (byte)(b ^ 0x80);
                    }
                }
            }
        }

        static readonly int[][] SameOnes = Enumerable.Range(0, 9).Select(k => Enumerable.Range(0, 256).Where(b => BitOperations.PopCount((uint)b) == k).ToArray()).ToArray();

        /// <summary>物音が大きくて読みを信用できないバイトか。普段の雑音より大きく、しかも音そのものに比べても大きいこと（雑音のほとんどない録音で誤って反応しないように）。</summary>
        bool Noisy(ReadResult read, int n)
        {
            if (LeftAt(read, n) <= BurstLeft) return false;
            int from = (int)read.Times[n], length = (int)span[read.Bytes[n]];
            double left = 0, power = 0;
            for (int c = 0; c < channels; c++)
            {
                left += Energy(r[c].AsSpan(from, length));
                power += Energy(x[c].AsSpan(from, length));
            }
            return power > 0 && left / power > BurstShare;
        }

        /// <summary>このバイトの区間に残った音の、普段の雑音に対する比。</summary>
        double LeftAt(ReadResult read, int n)
        {
            int from = (int)read.Times[n], length = (int)span[read.Bytes[n]];
            if (from + length >= r[0].Length) return 0;
            double left = 0;
            for (int c = 0; c < channels; c++) left += Energy(r[c].AsSpan(from, length)) / Math.Max(noise[c], 1);
            return left / (length * channels);
        }

        readonly List<(int Kind, double Time)> edgesOld = [], edgesNew = [];

        void Edges(List<(int Kind, double Time)> list, int value, double t)
        {
            foreach (bool on in LongBits[value])
            {
                double half = on ? timing.Long : timing.Short;
                list.Add((0, t));
                list.Add((1, t + half));
                t += 2 * half;
            }
        }

        /// <summary>置き換えると残りの音が減るなら、置き換えて true を返す。</summary>
        bool TrySwap(ReadResult read, int n, int oldA, int newA, int oldB, int newB)
        {
            edgesOld.Clear();
            edgesNew.Clear();
            double t = read.Times[n];
            Edges(edgesOld, oldA, t);
            Edges(edgesNew, newA, t);
            if (oldB >= 0)
            {
                double t2 = read.Times[n + 1];
                Edges(edgesOld, oldB, t2);
                Edges(edgesNew, newB, t2 + span[newA] - span[oldA]);
            }
            // 同じ時刻の切り替えは差がない
            int first = int.MaxValue, last = 0;
            for (int e = 0; e < edgesOld.Count; e++)
                if (Math.Abs(edgesOld[e].Time - edgesNew[e].Time) > 1e-6)
                {
                    first = Math.Min(first, (int)Math.Min(edgesOld[e].Time, edgesNew[e].Time));
                    last = Math.Max(last, (int)Math.Max(edgesOld[e].Time, edgesNew[e].Time));
                }
            if (first == int.MaxValue) return false;
            int begin = first - 1, length = last - first + tap + 4;
            if (begin < 0 || begin + length >= r[0].Length) return false;
            double change = 0;
            for (int c = 0; c < channels; c++)
            {
                var w = swapBuffer[c];
                if (w.Length < length) w = swapBuffer[c] = new float[length * 2];
                Array.Copy(r[c], begin, w, 0, length);
                Array.Clear(w, length, Math.Min(2, w.Length - length));
                for (int e = 0; e < edgesOld.Count; e++)
                {
                    if (Math.Abs(edgesOld[e].Time - edgesNew[e].Time) <= 1e-6) continue;
                    AddEdge(w, c, edgesOld[e].Kind, edgesOld[e].Time - begin, 1);
                    AddEdge(w, c, edgesNew[e].Kind, edgesNew[e].Time - begin, -1);
                }
                double sigma = Math.Max(noise[c], 1);
                change += (Energy(w.AsSpan(0, length)) - Energy(r[c].AsSpan(begin, length))) / sigma;
            }
            if (change >= 0) return false;
            for (int c = 0; c < channels; c++)
                for (int e = 0; e < edgesOld.Count; e++)
                {
                    if (Math.Abs(edgesOld[e].Time - edgesNew[e].Time) <= 1e-6) continue;
                    AddEdge(r[c], c, edgesOld[e].Kind, edgesOld[e].Time, 1);
                    AddEdge(r[c], c, edgesNew[e].Kind, edgesNew[e].Time, -1);
                }
            return true;
        }

        static double Energy(ReadOnlySpan<float> a)
        {
            int width = Vector<float>.Count, i = 0;
            double sum = 0;
            for (; i + width <= a.Length; i += width) { var v = new Vector<float>(a.Slice(i, width)); sum += Vector.Dot(v, v); }
            for (; i < a.Length; i++) sum += (double)a[i] * a[i];
            return sum;
        }

        /// <summary>
        /// FFh の並びの中の物音は、読むとバイト数がずれる。FFh は 1 バイトの時間が一定なので、前後の FFh の時刻の差からバイト数を決めて、FFh に置き換える。
        /// 本物のデータが混じっていれば、時間が FFh の整数倍にならないか、FFh の形に合わない。そのときは読んだままにする。
        /// </summary>
        void CountQuiet(ReadResult read)
        {
            const int Widest = 512;
            const double Whole = 0.02;
            int n = 0;
            while (n < read.Bytes.Count)
            {
                if (read.Bytes[n] == 0xFF) { n++; continue; }
                int first = n, last = n;
                // 近くの FFh でないバイトをまとめる
                for (int k = n + 1; k < read.Bytes.Count && k - last <= FfFrames && k - first <= Widest; k++) if (read.Bytes[k] != 0xFF) last = k;
                n = last + 1;
                int before = first - 1, after = last + 1;
                if (after + FfFrames > read.Bytes.Count) continue;
                bool flanked = first == 0 || before + 1 >= FfFrames;
                for (int k = 1; flanked && k <= FfFrames; k++) flanked = (first == 0 || read.Bytes[first - k] == 0xFF) && read.Bytes[last + k] == 0xFF;
                if (!flanked) continue;
                // 最初のバイトは前がないので、そのバイトの時刻から測る
                double period = (read.Times[after + FfFrames - 1] - read.Times[after]) / (FfFrames - 1);
                double from = first == 0 ? read.Times[0] - period : read.Times[before];
                double exact = (read.Times[after] - from) / period;
                int count = (int)Math.Round(exact) - 1;
                if (count < 0 || Math.Abs(exact - Math.Round(exact)) > Whole) continue;
                if (!FitsQuiet(from + period, count, period)) continue;
                read.Bytes.RemoveRange(first, last - first + 1);
                read.Times.RemoveRange(first, last - first + 1);
                read.Bytes.InsertRange(first, Enumerable.Repeat((byte)0xFF, count));
                read.Times.InsertRange(first, Enumerable.Range(0, count).Select(k => from + (k + 1) * period));
                int change = count - (last - first + 1);
                for (int c = read.Clusters.Count - 1; c >= 0; c--)
                {
                    var cluster = read.Clusters[c];
                    if (cluster.Last >= first - DamageBefore && cluster.First <= last) read.Clusters.RemoveAt(c);
                    else if (cluster.First > last) read.Clusters[c] = new(cluster.First + change, cluster.Last + change);
                }
                n = first + count;
            }
        }

        /// <summary>その時間の音が FFh の並びで説明がつくか。物音の入った少しのバイトは目をつぶる。</summary>
        bool FitsQuiet(double t, int count, double period)
        {
            if (count == 0) return true;
            var marks = new List<double>();
            for (int c = 0; c < channels; c++)
            {
                int from = (int)t - tap - 2, length = (int)(count * period) + 2 * tap + 8;
                if (from < 0 || from + length >= x[c].Length) return false;
                var copy = x[c].AsSpan(from, length).ToArray();
                for (int k = -tap / (int)period - 1; k <= count; k++) Add(copy, c, 0xFF, t + k * period - from, -1);
                for (int k = 0; k < count; k++)
                {
                    int at = (int)(t + k * period) - from;
                    double error = 0, power = 0;
                    for (int q = 0; q < (int)period && at + q < length; q++) { error += (double)copy[at + q] * copy[at + q]; double v = x[c][from + at + q]; power += v * v; }
                    marks.Add(power > 0 ? error / power : 1);
                }
            }
            marks.Sort();
            return marks[marks.Count / 2] < QuietFit;
        }

        /// <summary>
        /// 読み違いで時刻がずれたら、少し巻き戻して読み直す。候補の並びをいくつも並行して伸ばし、音に一番合う並びを選ぶ。
        /// 時刻を探し直すと途中のバイトの数が分からなくなるが、この読み方ならバイトの数と時刻がそろったまま戻れる（実機の録音で、乱れの前後の時間は正解と 1 サンプルも違わなかった）。
        /// </summary>
        (double Next, bool Changed, bool Damaged) Recover(ReadResult read, int from, double now, double end, double drift, double typical, int exactCount = 0)
        {
            var oldBytes = read.Bytes.Skip(from).ToArray();
            var oldTimes = read.Times.Skip(from).ToArray();
            double t0 = oldTimes.Length > 0 ? oldTimes[0] : now;
            int low = (int)t0, high = (int)now;
            double Left(int to)
            {
                double left = 0, power = 0;
                for (int c = 0; c < channels; c++)
                {
                    double sigma = Math.Max(noise[c], 1);
                    for (int i = low; i < to && i < r[c].Length; i++) { left += (double)r[c][i] * r[c][i] / sigma; power += (double)x[c][i] * x[c][i] / sigma; }
                }
                return power > 0 ? left / power : 1;
            }
            double before = Left(high);
            for (int k = oldBytes.Length - 1; k >= 0; k--)
                for (int c = 0; c < channels; c++) Add(r[c], c, oldBytes[k], oldTimes[k], 1);
            read.Bytes.RemoveRange(from, read.Bytes.Count - from);
            read.Times.RemoveRange(from, read.Times.Count - from);
            double horizon = Math.Max(t0 + widest, Math.Min(t0 + (RecoverBack + RecoverAhead) * span[0x55], end));
            var path = Beam(t0, horizon, drift, exactCount);
            // 最後の数バイトは後ろの音と照らせないまま決まっているので、通常の読みに任せる
            if (exactCount == 0 && path.Frames.Count > RecoverDrop)
            {
                double resume = path.Frames[^RecoverDrop].Time;
                path = (path.Frames.GetRange(0, path.Frames.Count - RecoverDrop), resume);
            }
            if (path.Frames.Count == 0)
            {
                for (int k = 0; k < oldBytes.Length; k++)
                {
                    read.Bytes.Add(oldBytes[k]);
                    read.Times.Add(oldTimes[k]);
                    for (int c = 0; c < channels; c++) Add(r[c], c, oldBytes[k], oldTimes[k], -1);
                }
                return (now, false, false);
            }
            foreach (var (value, time) in path.Frames)
                for (int c = 0; c < channels; c++) Add(r[c], c, value, time, -1);
            double after = high > low ? Left(high) : 0;
            if (high > low && after >= before * RecoverGain)
            {
                // 良くならなければ、元の読みのままにする
                foreach (var (value, time) in path.Frames)
                    for (int c = 0; c < channels; c++) Add(r[c], c, value, time, 1);
                for (int k = 0; k < oldBytes.Length; k++)
                {
                    read.Bytes.Add(oldBytes[k]);
                    read.Times.Add(oldTimes[k]);
                    for (int c = 0; c < channels; c++) Add(r[c], c, oldBytes[k], oldTimes[k], -1);
                }
                return (now, false, false);
            }
            foreach (var (value, time) in path.Frames)
            {
                read.Bytes.Add((byte)value);
                read.Times.Add(time);
            }
            double whole = Left((int)path.Next);
            return (path.Next, true, whole > Math.Max(DamagedLeft, typical * LostRatio));
        }

        sealed class Way
        {
            public required float[][] Window;     // Begin から先の、引き終わった音
            public required int Begin;
            public required double Time, Cost, Left;
            public required int Count, Ones;
            public required List<(int Value, double Time)> Frames;
        }

        /// <summary>
        /// 読み直しの本体。1 バイトの時間は 1 のビットの数だけで決まるので、「何バイト読んで、1 が何個あったか」が同じ並びは同じ時刻にいる。
        /// 同じ時刻の並びは一番合うものだけ残す（ビタビ法）。時刻の違う並びを無理に比べずに済み、時刻の誤った並びは後の音と合わなくなって落ちる。
        /// </summary>
        (List<(int Value, double Time)> Frames, double Next) Beam(double t0, double horizon, double drift, int exactCount = 0)
        {
            int size = widest + 2 * tap + 8, begin = (int)Math.Floor(t0);
            float[][] Slice(int at) => r.Select(c => { var w = new float[size]; int n = Math.Max(0, Math.Min(size, c.Length - at)); if (n > 0) Array.Copy(c, at, w, 0, n); return w; }).ToArray();
            var ways = new Dictionary<(int Count, int Ones), Way>
            {
                [(0, 0)] = new() { Window = Slice(begin), Begin = begin, Time = t0, Cost = 0, Left = 0, Count = 0, Ones = 0, Frames = [] }
            };
            var done = new List<Way>();
            var bestOfOnes = new int[9];
            int stop = (int)horizon;
            while (ways.Count > 0)
            {
                var way = ways.Values.MinBy(w => w.Time)!;
                ways.Remove((way.Count, way.Ones));
                if (way.Time >= horizon || (int)way.Time + 2 * widest + 2 * tap + 8 >= x[0].Length) { done.Add(way); continue; }
                Score(way.Time, way.Window, (int)Math.Floor(way.Time) - way.Begin);
                Array.Fill(bestOfOnes, -1);
                for (int b = 0; b < 256; b++)
                {
                    int ones = BitOperations.PopCount((uint)b);
                    if (bestOfOnes[ones] < 0 || metric[b] < metric[bestOfOnes[ones]]) bestOfOnes[ones] = b;
                }
                foreach (int value in bestOfOnes)
                {
                    int ones = way.Ones + BitOperations.PopCount((uint)value);
                    var key = (way.Count + 1, ones);
                    double cost = way.Cost + metric[value];
                    if (ways.TryGetValue(key, out var old) && old.Cost <= cost) continue;
                    double next = After(way.Time, value) + drift;
                    int nextBegin = (int)Math.Floor(next), move = nextBegin - way.Begin;
                    var copy = Slice(nextBegin);
                    double left = way.Left;
                    for (int c = 0; c < channels; c++)
                    {
                        // 親の窓の残りを引き継いで、このバイトを引く
                        var parent = new float[size + move];
                        Array.Copy(way.Window[c], parent, Math.Min(size, parent.Length));
                        Array.Copy(r[c], way.Begin + size, parent, size, Math.Max(0, Math.Min(move, r[c].Length - way.Begin - size)));
                        Add(parent, c, value, way.Time - way.Begin, -1);
                        double sigma = Math.Max(noise[c], 1);
                        int upto = Math.Min(move, stop - way.Begin);
                        for (int i = 0; i < upto; i++) left += (double)parent[i] * parent[i] / sigma;
                        Array.Copy(parent, move, copy[c], 0, size);
                    }
                    var frames = new List<(int, double)>(way.Frames) { (value, way.Time) };
                    ways[key] = new() { Window = copy, Begin = nextBegin, Time = next, Cost = cost, Left = left, Count = way.Count + 1, Ones = ones, Frames = frames };
                }
                if (ways.Count > BeamWidth)
                {
                    // 音の長さあたりの残りが多い並びから捨てる
                    foreach (var w in ways.Values.OrderBy(w => w.Cost / Math.Max(1, w.Time - t0)).Skip(BeamWidth).ToList()) ways.Remove((w.Count, w.Ones));
                }
            }
            // 最後は同じ区間に残った音で比べる。長さの違う並びも公平に比べられる
            if (exactCount > 0) done = done.Where(w => w.Count == exactCount).ToList();
            if (done.Count == 0) return ([], t0);
            var best = done.MinBy(w => w.Left)!;
            return (best.Frames, best.Time);
        }

        void Damage(ReadResult read, int n)
        {
            int first = Math.Max(0, n - DamageBefore);
            if (read.Clusters.Count > 0 && first - read.Clusters[^1].Last <= ClusterGap) read.Clusters[^1] = new(read.Clusters[^1].First, n);
            else read.Clusters.Add(new(first, n));
        }

        /// <summary>ここから先に音がないなら true。少し前の音量と比べる。</summary>
        bool Silent(int i)
        {
            int look = rate / 200, back = rate / 8;
            if (i < back || i + look >= x[0].Length) return true;
            double now = 0, before = 0;
            for (int c = 0; c < channels; c++)
            {
                now += Energy(x[c].AsSpan(i, look));
                before += Energy(x[c].AsSpan(i - back, look));
            }
            return now < before * 0.2;
        }

        /// <summary>データが FFh の並びで始まるとき、最初のバイトだけ違って読めたら FFh に直す。直前の音の響きで読み違えやすい。</summary>
        void FixFirst(ReadResult read)
        {
            if (read.Bytes[0] == 0xFF) return;
            for (int n = 1; n < StartCheck; n++) if (read.Bytes[n] != 0xFF) return;
            double start = read.Times[1] - span[0xFF];
            for (int c = 0; c < channels; c++)
            {
                Add(r[c], c, read.Bytes[0], read.Times[0], 1);
                Add(r[c], c, 0xFF, start, -1);
            }
            read.Bytes[0] = 0xFF;
            read.Times[0] = start;
        }

        /// <summary>今のバイトが FFh のままで説明がつくなら true。</summary>
        bool StaysQuiet(double t)
        {
            int i = (int)Math.Floor(t);
            int length = lengths[0xFF];
            int phase = Math.Clamp((int)((t - i) * Phases), 0, Phases - 1);
            double error = 0, power = 0;
            for (int c = 0; c < channels; c++)
            {
                var table = Template(c, phase);
                error += Distance(r[c].AsSpan(i, length), table.AsSpan(0xFF * widest, length));
                for (int k = 0; k < length; k++) power += (double)r[c][i + k] * r[c][i + k];
            }
            return power > 0 && error / power < FfNoise;
        }

        /// <summary>
        /// FFh の並びの中で音が合わなくなったとき、同じ周期のまま先へ進んで、また FFh に合うかを確かめる。合えばそこまでのバイトの数を返す。
        /// 本物のデータが来たのなら時間がずれるので、先で合うことはない。
        /// </summary>
        int SkipNoise(double t, double drift)
        {
            const int Reach = 256, Need = 6;
            double step = span[0xFF] + drift;
            int run = 0;
            for (int k = 1; k <= Reach; k++)
            {
                double at = t + k * step;
                int i = (int)Math.Floor(at);
                if (i + lengths[0xFF] + tap >= r[0].Length) return 0;
                double error = 0, power = 0;
                int phase = Math.Clamp((int)((at - i) * Phases), 0, Phases - 1);
                for (int c = 0; c < channels; c++)
                {
                    var table = Template(c, phase);
                    error += Distance(r[c].AsSpan(i, lengths[0xFF]), table.AsSpan(0xFF * widest, lengths[0xFF]));
                    for (int q = 0; q < lengths[0xFF]; q++) power += (double)r[c][i + q] * r[c][i + q];
                }
                run = power > 0 && error / power < FfContinue ? run + 1 : 0;
                if (run >= Need) return k - Need + 1;
            }
            return 0;
        }

        /// <summary>雑音で FFh に見えないだけで、この後も同じ間隔で FFh が続くなら true。本物のデータなら時間がずれて合わなくなる。</summary>
        bool Continues(double t)
        {
            const int Next = 6;
            double error = 0, power = 0;
            for (int c = 0; c < channels; c++)
            {
                double at = t + span[0xFF];
                for (int n = 0; n < Next; n++, at += span[0xFF])
                {
                    int i = (int)Math.Floor(at);
                    int phase = Math.Clamp((int)((at - i) * Phases), 0, Phases - 1);
                    if (i + lengths[0xFF] >= r[c].Length) return false;
                    var table = Template(c, phase);
                    error += Distance(r[c].AsSpan(i, lengths[0xFF]), table.AsSpan(0xFF * widest, lengths[0xFF]));
                    for (int k = 0; k < lengths[0xFF]; k++) power += (double)r[c][i + k] * r[c][i + k];
                }
            }
            return power > 0 && error / power < FfContinue;
        }

        double Fraction(int i, int value)
        {
            double power = 0;
            for (int c = 0; c < channels; c++)
            {
                double sigma = Math.Max(local[c], 1);
                for (int k = 0; k < lengths[value]; k++) power += (double)r[c][i + k] * r[c][i + k] / sigma;
            }
            double error = metric[value] + channels * lengths[value];
            return power > 0 ? error / power : 1;
        }

        /// <summary>怪しいときは、次のバイトまで含めて比べる。隣のビットとの入れ替わりは、次のバイトを見ないと分からない。</summary>
        int LookAhead(double t, int value, float[][] look, float[][] segments)
        {
            // 差の小さい候補だけ比べる（雑音の多い録音では、すべてのバイトで比べることになると遅い）
            double top = metric[value];
            var order = Enumerable.Range(0, 256).Where(b => metric[b] - top < LookGap).OrderBy(b => metric[b]).Take(Lookalikes).ToArray();
            if (order.Length < 2) return value;
            var scores = order.Select(b => metric[b]).ToArray();
            int i = (int)Math.Floor(t), best = value;
            double bestTotal = double.MaxValue;
            for (int q = 0; q < order.Length; q++)
            {
                int b = order[q];
                double next = After(t, b);
                int j = (int)Math.Floor(next);
                int length = j - i + widest + tap + 4;
                for (int c = 0; c < channels; c++)
                {
                    if (i + length >= r[c].Length) return value;
                    Array.Copy(r[c], i, look[c], 0, length);
                    Add(look[c], c, b, t - i, -1);
                }
                Score(next, look, j - i);
                int k = Best();
                double total = scores[q] + metric[k];
                if (total < bestTotal) { bestTotal = total; best = b; }
            }
            return best;
        }

        /// <summary>時刻のずれを測る。読んだバイトの切り替えを少しずらしたときの残りの大きさから求める。</summary>
        double Track(double t, int value)
        {
            int from = (int)Math.Floor(t) - 4;
            int length = (int)span[value] + tap + 8;
            if (from < 0 || from + length + 4 >= r[0].Length) return 0;
            double minus = 0, zero = 0, plus = 0;
            var buffer = new float[length + 8];
            for (int c = 0; c < channels; c++)
            {
                double sigma = Math.Max(noise[c], 1);
                double Measure(double shift)
                {
                    Array.Copy(r[c], from, buffer, 0, length);
                    Array.Clear(buffer, length, 8);
                    Add(buffer, c, value, t - from, 1);
                    Add(buffer, c, value, t - from + shift, -1);
                    double sum = 0;
                    for (int k = 0; k < length; k++) sum += (double)buffer[k] * buffer[k];
                    return sum / sigma;
                }
                minus += Measure(-0.5);
                zero += Measure(0);
                plus += Measure(0.5);
            }
            double curve = minus + plus - 2 * zero;
            return curve > 0 ? Math.Clamp(0.25 * (minus - plus) / curve, -1, 1) : 0;
        }

        void UpdateNoise(int from, int to)
        {
            for (int c = 0; c < channels; c++)
            {
                to = Math.Min(to, r[c].Length);
                double sum = 0;
                for (int i = Math.Max(0, from); i < to; i++) sum += (double)r[c][i] * r[c][i];
                if (to > from) noise[c] = noise[c] <= 0 ? sum / (to - from) : 0.999 * noise[c] + 0.001 * sum / (to - from);
            }
        }

        /// <summary>読めたデータで応答を学び直す。同じ値が続く所では応答が決まらないので使わない。</summary>
        void Refit()
        {
            if (recent.Count < RefitFrames) return;
            var part = recent.Skip(recent.Count - RefitFrames).ToArray();
            if (part.Select(p => p.Value).Distinct().Count() < 40) return;
            var edges = new List<(int, double)>();
            foreach (var (value, t) in part)
            {
                double at = t;
                foreach (bool on in LongBits[value])
                {
                    double half = on ? timing.Long : timing.Short;
                    edges.Add((0, at));
                    edges.Add((1, at + half));
                    at += 2 * half;
                }
            }
            int from = (int)part[0].Time, to = (int)part[^1].Time + 30;
            var oldUp = Up.Select(h => (double[])h.Clone()).ToArray();
            var oldDown = Down.Select(h => (double[])h.Clone()).ToArray();
            for (int c = 0; c < channels; c++) Fit(c, edges, from, to, cold: false, 0.02, 15);
            // 読み違えたまま学ぶと、誤った読みと誤った応答でつじつまが合ってしまう（実機の録音で、以後のデータがすべて誤った）。音量が変わっても形は変わらないので、形が変わったら捨てる
            bool same = true;
            for (int c = 0; same && c < channels; c++) same = Similarity(Up[c], oldUp[c]) >= SameShape && Similarity(Down[c], oldDown[c]) >= SameShape;
            if (!same && anchorUp != null)
            {
                Up = oldUp;
                Down = oldDown;
            }
            templates.Clear();
        }

        static double Similarity(double[] a, double[] b)
        {
            double ab = 0, aa = 0, bb = 0;
            for (int k = 0; k < a.Length; k++) { ab += a[k] * b[k]; aa += a[k] * a[k]; bb += b[k] * b[k]; }
            return aa > 0 && bb > 0 ? ab / Math.Sqrt(aa * bb) : 0;
        }

        /// <summary>1 回目に読んだ所から、ビットの長さと応答を求める。</summary>
        public void LearnFromRead()
        {
            var read = Result;
            int count = read.Bytes.Count - 1;
            double[,] a = new double[3, 3];
            double[] b = new double[3];
            int used = 0;
            var damaged = new bool[count + 1];
            foreach (var c in read.Clusters) for (int i = c.First; i <= Math.Min(c.Last + DamageBefore, count); i++) damaged[i] = true;
            for (int n = 0; n < count; n++)
            {
                if (damaged[n] || read.Bytes[n] == 0xFF || read.Times[n + 1] <= read.Times[n]) continue;
                int longs = LongBits[read.Bytes[n]].Count(v => v), shorts = 9 - longs;
                double[] row = [longs, shorts, 1];
                double duration = read.Times[n + 1] - read.Times[n];
                for (int p = 0; p < 3; p++) { b[p] += row[p] * duration; for (int q = 0; q < 3; q++) a[p, q] += row[p] * row[q]; }
                used++;
            }
            if (used >= 500 && Solve(a, b) is { } s)
            {
                var measured = new Timing(s[0] / 2, s[1] / 2, s[2]);
                if (Math.Abs(measured.Long / timing.Long - 1) < 0.03 && Math.Abs(measured.Short / timing.Short - 1) < 0.03 && measured.Gap > 0 && measured.Gap < timing.Long) SetTiming(measured);
            }
            // 値のばらけた所で応答を学ぶ。時刻はバイトごとに追従した値を使う
            int size = Math.Min(read.Bytes.Count, RefitFrames * 2);
            for (int n = read.Bytes.Count - size; n >= 0 && size > 0; n -= size)
            {
                var part = read.Bytes.Skip(n).Take(size).ToArray();
                if (part.Distinct().Count() < 40) continue;
                var edges = new List<(int, double)>();
                for (int i = 0; i < size; i++)
                {
                    double at = read.Times[n + i];
                    foreach (bool on in LongBits[part[i]])
                    {
                        double half = on ? timing.Long : timing.Short;
                        edges.Add((0, at));
                        edges.Add((1, at + half));
                        at += 2 * half;
                    }
                }
                for (int c = 0; c < channels; c++) { Fit(c, edges, (int)read.Times[n] + 2, (int)read.Times[n + size - 1], cold: false, 0.02, 40); AlignResponse(c); }
                anchorUp = Up.Select(h => (double[])h.Clone()).ToArray();
                anchorDown = Down.Select(h => (double[])h.Clone()).ToArray();
                templates.Clear();
                ResetNoise();
                break;
            }
        }

        /// <summary>
        /// 応答の立ち上がりを先頭にそろえる。読み違いで時刻がずれたまま学ぶと、応答がそのずれを吸収して、正しい時刻で読めなくなる。
        /// 切り替えの時刻は音の立ち上がりと同じなので、そろえれば時刻の基準が 1 つに決まる。
        /// </summary>
        void AlignResponse(int channel)
        {
            const int Lead = 2;
            double peak = 0;
            foreach (double v in Up[channel]) peak = Math.Max(peak, Math.Abs(v));
            int onset = 0;
            while (onset < tap && Math.Abs(Up[channel][onset]) < peak * 0.25) onset++;
            int move = onset - Lead;
            if (onset >= tap || move == 0) return;
            foreach (var h in new[] { Up[channel], Down[channel] })
            {
                var old = (double[])h.Clone();
                for (int k = 0; k < tap; k++) { int from = k + move; h[k] = from >= 0 && from < tap ? old[from] : 0; }
            }
        }

        static double[]? Solve(double[,] a, double[] b)
        {
            int n = b.Length;
            var m = new double[n, n + 1];
            for (int i = 0; i < n; i++) { for (int j = 0; j < n; j++) m[i, j] = a[i, j]; m[i, n] = b[i]; }
            for (int i = 0; i < n; i++)
            {
                int pivot = i;
                for (int k = i + 1; k < n; k++) if (Math.Abs(m[k, i]) > Math.Abs(m[pivot, i])) pivot = k;
                if (Math.Abs(m[pivot, i]) < 1e-9) return null;
                for (int j = 0; j <= n; j++) (m[i, j], m[pivot, j]) = (m[pivot, j], m[i, j]);
                for (int k = 0; k < n; k++)
                {
                    if (k == i) continue;
                    double f = m[k, i] / m[i, i];
                    for (int j = i; j <= n; j++) m[k, j] -= f * m[i, j];
                }
            }
            var s = new double[n];
            for (int i = 0; i < n; i++) s[i] = m[i, n] / m[i, i];
            return s;
        }

        /// <summary>読みすぎた分を捨てて、最後のバイトを読み直す。</summary>
        public void TrimTo(int count)
        {
            var read = Result;
            if (read.Bytes.Count <= count || read.Bytes.Count > count + 3) return;
            // FixLast は読みの最後のバイトを引かずに残しているので、それを戻してから捨てる
            for (int k = read.Bytes.Count - 2; k >= count - 1; k--)
                for (int c = 0; c < channels; c++) Add(r[c], c, read.Bytes[k], read.Times[k], 1);
            read.Bytes.RemoveRange(count, read.Bytes.Count - count);
            read.Times.RemoveRange(count, read.Times.Count - count);
            read.Bytes[count - 1] = (byte)BestLast(r, read.Times[count - 1]);
        }

        /// <summary>
        /// 最後の 2 バイトが value でも、読んだ値と同じくらい録音に合うか。検査値の読み違いでデータが誤りとされるのを防ぐ。
        /// </summary>
        public bool TailFits(int value)
        {
            var read = Result;
            int n = read.Bytes.Count;
            if (n < 2) return false;
            double t = read.Times[n - 2];
            int high = read.Bytes[n - 2], low = read.Bytes[n - 1];
            int from = (int)Math.Floor(t);
            double Residual(int a, int b)
            {
                double sum = 0;
                for (int c = 0; c < channels; c++)
                {
                    int length = (int)(span[a] + span[b]) + tap / 2;
                    if (from + length + tap + 4 >= r[c].Length) return double.MaxValue;
                    var line = r[c].AsSpan(from, length + tap + 4).ToArray();
                    Add(line, c, high, t - from, 1);    // 読んだ時に引いた分を戻す
                    double second = After(t - from, a);
                    Add(line, c, a, t - from, -1);
                    Add(line, c, b, second, -1);
                    for (int k = 0; k < length; k++) sum += (double)line[k] * line[k] / Math.Max(noise[c], 1);
                }
                return sum;
            }
            double read2 = Residual(high, low), wanted = Residual(value >> 8, value & 255);
            return wanted <= read2 * 1.03;
        }

        /// <summary>最後のバイトを、次のバイトがない形で読み直す。全体の検査値を正しく読むために要る。</summary>
        void FixLast(ReadResult read)
        {
            int last = read.Bytes.Count - 1;
            for (int c = 0; c < channels; c++) Add(r[c], c, read.Bytes[last], read.Times[last], 1);
            read.Bytes[last] = (byte)BestLast(r, read.Times[last]);
        }
    }
}
