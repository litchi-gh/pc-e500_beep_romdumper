namespace BeepDump.Core;

public enum BlockState { Good, Repaired, Failed }

public sealed record Block(int Address, BlockState State, byte[] Data);

public sealed record DecodeResult(List<Block> Blocks, double Scale, double Overhead, int ToneCount, List<string> Notes);

/// <summary>
/// BEEPDUMP.TXT が鳴らす音を読む。音の並びは 1 ブロック 259 音:
///   A/5000、データ 256 バイト、S の下位 8 bit、S/257（S = 番地の bit8-15 + データの和）
/// 周期の倍率は機械ごとに違うので、検査値の通り方から決める（校正用の音は鳴らさない）。
/// </summary>
public static class Decoder
{
    public const int Periods = 8, DataBytes = 256, FrameTones = DataBytes + 3;
    const double MinRunGap = 0.5;          // 秒。これより長い無音で別の実行とみなす
    const double BoundaryExcess = 3.5;     // ms。ブロック境界は処理が長いので音の間隔が伸びる
    const double IntervalWindow = 1.2;     // ms。間隔から周期の候補を絞る幅（短い音）
    const double IntervalSigma = 0.4;      // ms。周期の候補を、次の音までの間隔から離れるほど減点する幅
    const double LearnedSigma = 0.3;       // ms。同じく、値ごとの処理時間を覚えたあとの幅
    const double DurationSigma = 0.35;     // ms。音の長さとのずれで減点する幅
    const double DurationLimit = 1.3;      // ms。読んだ値と音の長さのずれの許容（実機の録音で、これ以下なら誤りなく読めた）
    const double KeyStretch = 2.7;         // ms。キーを押している間に間隔が伸びる量（実機で測定）
    const double IntervalLimit = 1.1;      // ms。読んだ値と次の音までの間隔のずれの許容（実機で標準偏差 0.25 ms）
    const int ShortValues = 46;            // これより小さい値は短い音で、周期を読み違えやすい
    const double WideWindow = 3.0;         // ms。同じく長い音
    const double ReliablePeriod = 250;     // これより長い周期（値 40 以上）は倍音が多く読み違えにくい
    const double KnownScaleWindow = 0.002;
    const double DurationWindow = 0.8;     // ms。音の長さから周期の候補を絞る幅

    /// <param name="knownScale">同じ機械の前の録音で分かった周期の倍率（なければ 0）。音の種類が少ない短い録り直しでも倍率を間違えない</param>
    public static DecodeResult Decode(float[] x, int rate, Action<string, double>? progress = null, Action<string>? trace = null, double knownScale = 0, double relative = 0.12, int lookahead = 3, Func<int, bool>? wanted = null)
    {
        if (FastDecoder.LooksFast(x, rate, relative)) return FastDecoder.Decode(x, rate, progress, relative, lookahead, wanted);
        var notes = new List<string>();
        var tones = Segmenter.Find(x, rate, shortest: 0.0024, relative: relative);
        var blocks = new List<Block>();
        double scale = 0, overhead = 0;
        int done = 0, from = 0;
        for (int i = 1; i <= tones.Count; i++)
        {
            if (i < tones.Count && (tones[i].Start - tones[i - 1].End) / (double)rate < MinRunGap) continue;
            int count = i - from;
            if (count >= FrameTones / 2)
            {
                var run = new Run(x, rate, tones.GetRange(from, count), notes, f => progress?.Invoke(Messages.Pick("音を読み取り中", "Reading tones"), (done + f * count) / tones.Count), trace, knownScale);
                blocks.AddRange(run.Decode());
                if (run.Scale > 0) { scale = run.Scale; overhead = run.Overhead; }
            }
            done += count;
            from = i;
        }
        if (blocks.Count == 0) notes.Add(tones.Count == 0 ? Messages.Pick("音が見つかりませんでした。", "No tones were found.") : Messages.Pick("BEEPDUMP の音の並びが見つかりませんでした。", "The BEEPDUMP tone sequence was not found."));
        return new(blocks, scale, overhead, tones.Count, notes);
    }

    /// <summary>周期を値に直すときの候補（読みの基準と 2 番目の候補）。</summary>
    readonly record struct Reading(int Value, int Second, double Margin);

    sealed class Run
    {
        readonly float[] x;
        readonly int rate;
        readonly List<Tone> tones;
        readonly List<string> notes;
        readonly Action<double> progress;
        readonly Action<string>? trace;
        readonly double knownScale;
        readonly PeriodReader reader;
        readonly double[] intervals;       // ms。次の音までの間隔（次がなければ NaN）
        readonly double[] durations;       // ms
        readonly double[] bias = new double[256];   // 値ごとの読みの偏り（段）。検査の通ったブロックから学ぶ
        readonly double[] roleOverhead = new double[5];
        readonly double[] valueOverhead = new double[256];
        readonly double[] valueRing = new double[256];
        double shortToneRing = 2;          // ms。短い音の、音の長さ - 8 周期（響きの分）。検査の通ったブロックから学ぶ
        double[] periods = [];
        double[] frameScale = [];
        int[][] frames = [];               // 各ブロックの音の番号

        public double Scale { get; private set; }
        public double Overhead { get; private set; }

        public Run(float[] x, int rate, List<Tone> tones, List<string> notes, Action<double> progress, Action<string>? trace, double knownScale)
        {
            (this.x, this.rate, this.tones, this.notes, this.progress, this.trace, this.knownScale) = (x, rate, tones, notes, progress, trace, knownScale);
            reader = new(x, rate);
            intervals = new double[tones.Count];
            durations = new double[tones.Count];
            var onsets = new int[tones.Count];
            for (int i = 0; i < tones.Count; i++)
            {
                onsets[i] = Segmenter.Onset(x, rate, tones[i]);
                durations[i] = (tones[i].End - tones[i].Start) * 1000.0 / rate;
            }
            for (int i = 0; i < tones.Count; i++) intervals[i] = i + 1 < tones.Count ? (onsets[i + 1] - onsets[i]) * 1000.0 / rate : double.NaN;
        }

        public IEnumerable<Block> Decode()
        {
            // 1 回目: 音の長さだけで絞って読み、ブロックの区切りと処理時間を求める
            periods = ReadAll((i, _) => (Math.Max(0.15, durations[i] - 3.5) * 256 / Periods, (durations[i] + 1) * 256 / Periods, 1.0), 0, 0.4);
            var excess = intervals.Select((d, i) => d - Periods * periods[i] / 256).ToArray();
            Overhead = Median(excess.Where((e, i) => !double.IsNaN(e) && periods[i] >= 350));
            if (double.IsNaN(Overhead)) Overhead = Median(excess.Where(e => !double.IsNaN(e)));
            BuildFrames(excess);
            if (frames.Length == 0) { notes.Add(Messages.Pick($"{tones.Count} 音ありましたが、ブロックの形になっていません。", $"Found {tones.Count} tones, but they do not form blocks.")); return []; }

            // 2 回目: 次の音までの間隔で絞って読み直す。処理時間は時々揺れるので、倍音の多い長い音は広めに探し、
            // 短い音だけ狭く絞る（短い音は周期の 2 倍に読み違えやすいが、窓が 3 ms なら周期 125 以上は 2 倍が窓に入らない）
            FindRoleOverheads(excess);
            periods = ReadAll((i, role) => WindowFromInterval(i, role, WideWindow), 0.4, 0.7);
            var wide = periods;
            var narrow = ReadAll((i, role) => wide[i] < ReliablePeriod ? WindowFromInterval(i, role, IntervalWindow) : (wide[i], wide[i] + 0.5, 0.5), 0.7, 0.8, IntervalSigma);
            periods = wide.Select((u, i) => u < ReliablePeriod ? narrow[i] : u).ToArray();

            if (!FindScale()) { notes.Add(Messages.Pick("音の高さの倍率が決められませんでした。", "Could not determine the pitch scale.")); return []; }
            var readings = ReadFrames();
            var addresses = Chain(readings);

            // 検査の通ったブロックから倍率の変化と値ごとの偏りを学んで読み直す
            RefineScale(readings, addresses);
            readings = ReadFrames();
            addresses = Chain(readings);
            LearnBias(readings, addresses);
            // 読み違えやすい値は検査の通るブロックに入りにくいので、覚えては読み直すのを繰り返す
            for (int round = 0; round < 3; round++)
            {
                LearnOverheads(readings, addresses);
                readings = ReadByLattice(readings, addresses);
                addresses = Chain(readings);
            }

            // まだ通らないブロックは、音の長さを手がかりに読み直し、それでもだめなら怪しい音を入れ替えて試す
            var result = new List<Block>();
            for (int f = 0; f < frames.Length; f++)
            {
                progress(0.8 + 0.2 * f / frames.Length);
                int address = addresses[f];
                if (address < 0) { trace?.Invoke($"noaddr {frames[f].Length} 0 0 {frameScale[f]:F5} " + string.Join(' ', readings[f].Select((v, k) => $"{v}:{periods[frames[f][k]]:F2}:{durations[frames[f][k]]:F1}:{intervals[frames[f][k]]:F1}"))); continue; }
                if (frames[f].Length != FrameTones) { trace?.Invoke($"{address:X5} {frames[f].Length} count " + string.Join(' ', frames[f].Select(i => $"{durations[i]:F1}:{intervals[i]:F1}"))); result.Add(Failed(address)); continue; }
                var values = readings[f];
                trace?.Invoke($"{address:X5} {frames[f].Length} {(Passes(values, address) ? 1 : 0)} {(OnLattice(f, values) ? 1 : 0)} {frameScale[f]:F5} " + string.Join(' ', values.Select((v, k) => $"{v}:{periods[frames[f][k]]:F2}:{durations[frames[f][k]]:F1}:{intervals[frames[f][k]]:F1}")));
                if (Passes(values, address) && Plausible(f, values)) { result.Add(new(address, BlockState.Good, ToData(values))); continue; }
                var retried = RereadFrame(f);
                if (Passes(retried, address) && Plausible(f, retried)) { result.Add(new(address, BlockState.Repaired, ToData(retried))); continue; }
                var repaired = Repair(f, retried, address) ?? Repair(f, values, address);
                result.Add(repaired != null && Plausible(f, repaired) ? new(address, BlockState.Repaired, ToData(repaired)) : Failed(address));
            }
            return result;
        }

        static Block Failed(int address) => new(address, BlockState.Failed, []);

        double[] ReadAll(Func<int, int, (double, double, double)> window, double progressFrom, double progressTo, double sigma = 0)
        {
            var roles = new int[tones.Count];
            Array.Fill(roles, 1);
            foreach (var frame in frames)
                if (frame.Length == FrameTones)
                    for (int k = 0; k < FrameTones; k++) roles[frame[k]] = k == 0 ? 0 : k == FrameTones - 3 ? 2 : k == FrameTones - 2 ? 3 : k == FrameTones - 1 ? 4 : 1;
            var result = new double[tones.Count];
            int finished = 0;
            Parallel.For(0, tones.Count, i =>
            {
                var (lo, hi, step) = window(i, roles[i]);
                result[i] = sigma > 0 ? reader.Find(tones[i], lo, hi, step, (lo + hi) / 2, sigma * 256 / Periods) : reader.Find(tones[i], lo, hi, step);
                int n = Interlocked.Increment(ref finished);
                if (n % 256 == 0) progress(progressFrom + (progressTo - progressFrom) * n / tones.Count);
            });
            return result;
        }

        (double, double, double) WindowFromInterval(int i, int role, double window)
        {
            double d = intervals[i];
            if (double.IsNaN(d) || d > 80) return (Math.Max(0.15, durations[i] - 3.5) * 256 / Periods, (durations[i] + 1) * 256 / Periods, 0.5);
            double tone = d - roleOverhead[role];
            return ((tone - window) * 256 / Periods, (tone + window) * 256 / Periods, 0.5);
        }

        /// <summary>境界（間隔が明らかに長い所）でブロックに分ける。音が足りない・多いブロックもそのまま残す。</summary>
        void BuildFrames(double[] excess)
        {
            // 周期の読み違いで境界に見えることがあるので、音の高さによらない実際のすき間でも確かめる
            var gaps = intervals.Select((d, i) => d - durations[i]).ToArray();
            double usualGap = Median(gaps.Where(g => !double.IsNaN(g)));
            var parts = new List<int[]>();
            var current = new List<int>();
            for (int i = 0; i < tones.Count; i++)
            {
                current.Add(i);
                if (i == tones.Count - 1 || (excess[i] - Overhead > BoundaryExcess && gaps[i] - usualGap > BoundaryExcess)) { parts.Add(current.ToArray()); current.Clear(); }
            }
            var list = new List<int[]>();
            for (int p = 0; p < parts.Count; p++)
            {
                // 割れたブロック: 続きと合わせてちょうど 259 音なら 1 つに戻す
                int end = p, sum = parts[p].Length;
                while (sum < FrameTones && end + 1 < parts.Count) sum += parts[++end].Length;
                if (end > p && sum == FrameTones) { list.Add(parts.Skip(p).Take(end - p + 1).SelectMany(q => q).ToArray()); p = end; continue; }
                // 長いまとまり: 境界を見落としたものとして 259 音ずつに分ける
                int blocks = parts[p].Length / FrameTones;
                if (blocks >= 2 && parts[p].Length == blocks * FrameTones)
                    for (int b = 0; b < blocks; b++) list.Add(parts[p][(b * FrameTones)..((b + 1) * FrameTones)]);
                else list.Add(parts[p]);
            }
            frames = list.Where(q => q.Length >= FrameTones / 2).ToArray();
        }

        void FindRoleOverheads(double[] excess)
        {
            for (int role = 0; role < 5; role++)
            {
                var values = new List<double>();
                foreach (var frame in frames.Where(f => f.Length == FrameTones))
                {
                    if (role == 1) for (int k = 1; k < FrameTones - 3; k++) values.Add(excess[frame[k]]);
                    else values.Add(excess[frame[role == 0 ? 0 : FrameTones - 5 + role]]);
                }
                double median = Median(values.Where(v => !double.IsNaN(v)));
                roleOverhead[role] = double.IsNaN(median) ? Overhead : median;
            }
        }

        /// <summary>周期 = 倍率 × (90 + 4 × 値)。格子に最もよく乗り、検査値が最も通る倍率を探す。</summary>
        bool FindScale()
        {
            // 倍率は録音中に少しずつ変わることがあるので、先頭の方だけで求める
            var sample = frames.Where(f => f.Length == FrameTones).SelectMany(f => f).Where(i => periods[i] > 180).Select(i => periods[i]).Take(1500).ToArray();
            if (sample.Length < 50) sample = frames.SelectMany(f => f).Select(i => periods[i]).Take(1500).ToArray();   // 全部 00h の範囲など
            const double Low = 0.70, High = 1.10, Step = 2e-5;
            int steps = (int)((High - Low) / Step);
            var fit = new double[steps];
            Parallel.For(0, steps, c =>
            {
                double s = Low + c * Step, sum = 0;
                foreach (double u in sample) sum += Math.Cos(Math.PI / 2 * (u / s - 90));
                fit[c] = sum;
            });
            // 音の種類が少ない範囲（全部 FFh など）は候補がいくつも残る。全部の音が 1 段ずれると検査値も一緒にずれて通るので、
            // 検査値の通り方が同じなら、値の違う音同士の周期の比がいちばん格子に合う候補を選ぶ。
            double top = fit.Max();
            var peaks = new List<int>();
            foreach (int c in Enumerable.Range(1, steps - 2).Where(c => fit[c] > fit[c - 1] && fit[c] >= fit[c + 1] && fit[c] > 0.3 * top).OrderByDescending(c => fit[c]))
            {
                // 同じ山のすそ野の小さな山は候補にしない（正しい候補が数の上限から漏れないように）
                if (peaks.Count < 400 && peaks.All(p => Math.Abs(p - c) * Step > 8e-4)) peaks.Add(c);
            }
            if (peaks.Count == 0) return false;
            var check = frames.Where(f => f.Length == FrameTones).Take(40).ToArray();
            (int Passes, double Residual) best = (-1, 0);
            // 前の録音の倍率が分かっていれば、その近くを優先する（1 段ずれた偽の候補は 0.36% 離れている）
            var nearKnown = peaks.Where(p => knownScale > 0 && Math.Abs(Low + p * Step - knownScale) < KnownScaleWindow).ToList();
            foreach (int peak in nearKnown.Count > 0 ? nearKnown : peaks)
            {
                double s = Low + peak * Step;
                var self = check.Select(f => SelfAddress(ReadFrame(f, s))).ToArray();
                int passes = self.Count(a => a >= 0) + Enumerable.Range(1, Math.Max(0, self.Length - 1)).Count(n => self[n] >= 0 && self[n] == self[n - 1] + DataBytes);
                if (passes < best.Passes) continue;
                double residual = ClassResidual(check, s);
                if (passes > best.Passes || residual < best.Residual) { best = (passes, residual); Scale = s; }
            }
            if (best.Passes == 0) { Scale = Low + peaks[0] * Step; notes.Add(Messages.Pick("検査の通るブロックが少なく、音の高さの倍率が不確かです。", "Few blocks passed the check, so the pitch scale is uncertain.")); }
            frameScale = new double[frames.Length];
            double tracked = Scale;
            for (int f = 0; f < frames.Length; f++)
            {
                frameScale[f] = tracked;
                var ratios = frames[f].Where(i => periods[i] > 340).Select(i => periods[i] / (90 + 4 * Nearest(periods[i], tracked).Value)).ToArray();
                if (ratios.Length >= 16) tracked += 0.25 * (Median(ratios) - tracked);
            }
            return true;
        }

        /// <summary>値ごとの読みの片寄りの平均。音の数の多い値に引きずられないよう、値ごとに同じ重みで見る。低すぎる値はもともと片寄るので除く。</summary>
        double ClassResidual(int[][] check, double scale)
        {
            var groups = check.SelectMany(f => f).Select(i => (position: (periods[i] / scale - 90) / 4, value: Nearest(periods[i], scale).Value))
                .Where(t => t.value >= 16).GroupBy(t => t.value).Select(g => Math.Abs(Median(g.Select(t => t.position - t.value)))).ToArray();
            return groups.Length == 0 ? 0 : groups.Average();
        }

        /// <summary>検査の通ったブロックの正しい値から各ブロックの倍率を求め、前後でならす。</summary>
        void RefineScale(int[][] readings, int[] addresses)
        {
            var measured = new double[frames.Length];
            for (int f = 0; f < frames.Length; f++)
            {
                measured[f] = double.NaN;
                if (addresses[f] < 0 || !Passes(readings[f], addresses[f])) continue;
                var ratios = frames[f].Select((i, k) => (u: periods[i], v: readings[f][k])).Where(t => t.v >= 64).Select(t => t.u / (90 + 4 * t.v)).ToArray();
                if (ratios.Length >= 16) measured[f] = Median(ratios);
            }
            for (int f = 0; f < frames.Length; f++)
            {
                var near = Enumerable.Range(Math.Max(0, f - 8), Math.Min(frames.Length, f + 9) - Math.Max(0, f - 8)).Select(n => measured[n]).Where(m => !double.IsNaN(m)).ToArray();
                if (near.Length >= 3) frameScale[f] = Median(near);
            }
        }

        /// <summary>ブロック全体で読みが格子点から片寄っていないか。全部の音が 1 段ずれると検査値も一緒にずれて通ってしまうので確かめる。</summary>
        bool OnLattice(int f, int[] values)
        {
            var offsets = frames[f].Select((i, k) => (u: periods[i], v: values[k])).Where(t => t.v >= 32)
                .Select(t => (t.u / frameScale[f] - 90) / 4 - t.v - bias[t.v]).ToArray();
            return offsets.Length < 16 || Math.Abs(Median(offsets)) < 0.2;
        }

        Reading Nearest(double period, double scale)
        {
            double position = (period / scale - 90) / 4;
            int center = (int)Math.Round(position);
            int best = -1, second = -1; double bestError = double.MaxValue, secondError = double.MaxValue;
            for (int v = Math.Max(0, center - 2); v <= Math.Min(255, center + 2); v++)
            {
                double error = Math.Abs(position - v - bias[v]);
                if (error < bestError) { second = best; secondError = bestError; best = v; bestError = error; }
                else if (error < secondError) { second = v; secondError = error; }
            }
            if (best < 0) best = second = Math.Clamp(center, 0, 255);
            return new(best, second < 0 ? best : second, secondError - bestError);
        }

        int[][] ReadFrames()
        {
            var result = new int[frames.Length][];
            for (int f = 0; f < frames.Length; f++)
                result[f] = ReadFrame(frames[f], frameScale[f]);
            return result;
        }

        /// <summary>1 ブロックを値に直す。検査値の下位の音は小さな値が多く、録音が短いと処理時間の基準がなくて周期の 2 倍に読むことがあるので、その場合も試す。</summary>
        int[] ReadFrame(int[] frame, double scale)
        {
            var values = frame.Select(i => Nearest(periods[i], scale).Value).ToArray();
            if (frame.Length != FrameTones || SelfAddress(values) >= 0) return values;
            var halved = (int[])values.Clone();
            halved[FrameTones - 2] = Nearest(periods[frame[FrameTones - 2]] / 2, scale).Value;
            return SelfAddress(halved) >= 0 ? halved : values;
        }

        /// <summary>番地: 検査値と A/5000 から求める。求まらなければ -1。</summary>
        static int SelfAddress(int[] v)
        {
            if (v.Length != FrameTones) return -1;
            int sum = 0;
            for (int k = 1; k <= DataBytes; k++) sum += v[k];
            int r = (v[FrameTones - 2] - sum) & 255;
            if ((r + sum) / 257 != v[FrameTones - 1]) return -1;
            int found = -1;
            for (int high = 0; high < 16; high++)
            {
                int address = high << 16 | r << 8;
                if (address / 5000 != v[0]) continue;
                if (found >= 0) return -1;
                found = address;
            }
            return found;
        }

        /// <summary>
        /// ブロックに番地を割り当てる。番地を検査値から逆算すると、音を 1 つ読み違えただけで番地もずれて、検査も通ることがある
        /// （値 6 を 1 と読むと、和が 5 減って 5 ブロック先の番地になる）。そこで前後のブロックの多数決で決める。
        /// </summary>
        int[] Chain(int[][] readings)
        {
            const int Reach = 16;
            var self = readings.Select(SelfAddress).ToArray();
            var result = new int[frames.Length];
            Array.Fill(result, -1);
            // 物音で 2 ブロックが 1 つにつながったときも番地を数え違えないように、音の数からブロックの位置を求める
            var position = new int[frames.Length];
            for (int f = 1; f < frames.Length; f++) position[f] = position[f - 1] + Math.Max(1, (int)Math.Round((double)frames[f - 1].Length / FrameTones));
            for (int f = 0; f < frames.Length; f++)
            {
                var votes = new Dictionary<int, int>();
                for (int g = Math.Max(0, f - Reach); g <= Math.Min(frames.Length - 1, f + Reach); g++)
                {
                    if (self[g] < 0) continue;
                    int address = self[g] - (position[g] - position[f]) * DataBytes;
                    votes[address] = votes.GetValueOrDefault(address) + 1;
                }
                if (votes.Count == 0) continue;
                var top = votes.OrderByDescending(v => v.Value).First();
                if (top.Key >= 0 && top.Key < 1 << 20) result[f] = top.Key;
            }
            for (int pass = 0; pass < 2; pass++)
            {
                for (int f = 1; f < frames.Length; f++) if (result[f] < 0 && result[f - 1] >= 0) result[f] = result[f - 1] + (position[f] - position[f - 1]) * DataBytes;
                for (int f = frames.Length - 2; f >= 0; f--) if (result[f] < 0 && result[f + 1] >= DataBytes) result[f] = result[f + 1] - (position[f + 1] - position[f]) * DataBytes;
            }
            for (int f = 0; f < frames.Length; f++) if (result[f] >= 1 << 20) result[f] = -1;
            return result;
        }

        static bool Passes(int[] v, int address)
        {
            if (v.Length != FrameTones) return false;
            int sum = (address >> 8) & 255;
            for (int k = 1; k <= DataBytes; k++) sum += v[k];
            return (sum & 255) == v[FrameTones - 2] && sum / 257 == v[FrameTones - 1];
        }

        void LearnBias(int[][] readings, int[] addresses)
        {
            var offsets = new List<double>[256];
            for (int v = 0; v < 256; v++) offsets[v] = [];
            var rings = new List<double>();
            for (int f = 0; f < frames.Length; f++)
            {
                if (addresses[f] < 0 || !Passes(readings[f], addresses[f])) continue;
                for (int k = 0; k < FrameTones; k++)
                {
                    int value = readings[f][k];
                    double period = periods[frames[f][k]];
                    offsets[value].Add((period / frameScale[f] - 90) / 4 - value);
                    if (period < ReliablePeriod) rings.Add(durations[frames[f][k]] - Periods * period / 256);
                }
            }
            if (rings.Count >= 16) shortToneRing = Median(rings);
            var known = new double[256];
            for (int v = 0; v < 256; v++) known[v] = offsets[v].Count >= 3 ? Median(offsets[v]) : double.NaN;
            for (int v = 0; v < 256; v++)
            {
                int lo = v, hi = v;
                while (lo >= 0 && double.IsNaN(known[lo])) lo--;
                while (hi < 256 && double.IsNaN(known[hi])) hi++;
                bias[v] = lo < 0 && hi > 255 ? 0 : lo < 0 ? known[hi] : hi > 255 ? known[lo] : lo == hi ? known[lo] : known[lo] + (known[hi] - known[lo]) * (v - lo) / (hi - lo);
            }
        }

        /// <summary>
        /// 検査の通ったブロックから、値ごとの処理時間（次の音までの間隔 − 8 周期）を覚える。
        /// BASIC の処理時間は値で変わる（実機の PC-E500 で、値 13 は他より約 0.9 ms 短かった）。
        /// </summary>
        void LearnOverheads(int[][] readings, int[] addresses)
        {
            var samples = new List<double>[256];
            var lengths = new List<double>[256];
            for (int v = 0; v < 256; v++) { samples[v] = []; lengths[v] = []; }
            var all = new List<double>();
            for (int f = 0; f < frames.Length; f++)
            {
                if (addresses[f] < 0 || !Passes(readings[f], addresses[f])) continue;
                for (int k = 1; k < FrameTones - 3; k++)
                {
                    int value = readings[f][k], i = frames[f][k];
                    if (double.IsNaN(intervals[i])) continue;
                    double tone = Periods * frameScale[f] * (90 + 4 * value + 4 * bias[value]) / 256;
                    double excess = intervals[i] - tone;
                    all.Add(excess);
                    samples[value].Add(excess);
                    lengths[value].Add(durations[i] - tone);
                }
            }
            double usual = all.Count > 0 ? Median(all) : roleOverhead[1];
            for (int v = 0; v < 256; v++) valueOverhead[v] = samples[v].Count >= 3 ? Median(samples[v]) : usual;
            // 音の長さ - 8 周期（響きの分）。値の近い音はほぼ同じなので、出てこない値は近い値から補う
            var ring = new double[256];
            for (int v = 0; v < 256; v++) ring[v] = lengths[v].Count >= 3 ? Median(lengths[v]) : double.NaN;
            for (int v = 0; v < 256; v++)
            {
                int lo = v, hi = v;
                while (lo >= 0 && double.IsNaN(ring[lo])) lo--;
                while (hi < 256 && double.IsNaN(ring[hi])) hi++;
                valueRing[v] = lo < 0 && hi > 255 ? shortToneRing : lo < 0 ? ring[hi] : hi > 255 ? ring[lo] : lo == hi ? ring[lo] : ring[lo] + (ring[hi] - ring[lo]) * (v - lo) / (hi - lo);
            }
        }

        /// <summary>
        /// 検査値は和なので、+11 と -11 のような読み違いの組は通ってしまう。読んだ値と次の音までの間隔が矛盾する音があれば、そのブロックは信用しない。
        /// </summary>
        bool Plausible(int f, int[] values)
        {
            if (!OnLattice(f, values)) return false;
            for (int k = 1; k < FrameTones - 3; k++)
            {
                int i = frames[f][k], value = values[k];
                if (value >= ShortValues) continue;     // 長い音は倍音が多く、周期を読み違えにくい
                double tone = Periods * frameScale[f] * (90 + 4 * value + 4 * bias[value]) / 256;
                if (Math.Abs(durations[i] - tone - valueRing[value]) > DurationLimit) return false;
                if (double.IsNaN(intervals[i])) continue;
                double late = intervals[i] - tone - valueOverhead[value];
                if (Math.Abs(late) > IntervalLimit && Math.Abs(late - KeyStretch) > IntervalLimit / 2) return false;   // キーを押している間は間隔だけ伸びる
            }
            return true;
        }

        /// <summary>
        /// 短い音を「どの値に最も合うか」で読む。各値の周期の倍音への合い方を、その値で予想される次の音までの間隔とのずれで減点して比べる。
        /// 長い音は周期の読みがそのまま使える。
        /// </summary>
        int[][] ReadByLattice(int[][] previous, int[] addresses)
        {
            var result = new int[frames.Length][];
            int finished = 0;
            Parallel.For(0, frames.Length, f =>
            {
                var frame = frames[f];
                if (addresses[f] >= 0 && Passes(previous[f], addresses[f]) && Plausible(f, previous[f])) { result[f] = previous[f]; return; }
                var values = new int[frame.Length];
                var periodsOfValues = new double[ShortValues];
                for (int v = 0; v < ShortValues; v++) periodsOfValues[v] = frameScale[f] * (90 + 4 * v + 4 * bias[v]);
                for (int k = 0; k < frame.Length; k++)
                {
                    int i = frame[k];
                    values[k] = Nearest(periods[i], frameScale[f]).Value;
                    bool dataTone = frame.Length != FrameTones || (k >= 1 && k < FrameTones - 3);
                    if (periods[i] >= ReliablePeriod || !dataTone || double.IsNaN(intervals[i]) || intervals[i] > 80) continue;
                    var scores = reader.Scores(tones[i], periodsOfValues);
                    double best = double.MinValue;
                    int chosen = values[k];
                    for (int v = 0; v < ShortValues; v++)
                    {
                        double tone = Periods * periodsOfValues[v] / 256;
                        double late = intervals[i] - tone - valueOverhead[v];
                        double z = Math.Min(Math.Abs(late), Math.Abs(late - KeyStretch)) / LearnedSigma, y = (durations[i] - tone - valueRing[v]) / DurationSigma;
                        double weighted = scores[v] > 0 ? scores[v] * Math.Exp(-(z * z + y * y) / 2) : scores[v];
                        if (weighted > best) { best = weighted; chosen = v; }
                    }
                    // 選ぶのは山だけ。値はその山の頂上の周期から決める（値ごとの比較だと隣の値と差がつきにくい）
                    double near = periodsOfValues[chosen];
                    values[k] = Nearest(reader.Find(tones[i], near - 6, near + 6), frameScale[f]).Value;
                }
                result[f] = values;
                int n = Interlocked.Increment(ref finished);
                if (n % 16 == 0) progress(0.8 + 0.15 * n / frames.Length);
            });
            return result;
        }

        /// <summary>
        /// 検査の通らなかったブロックの短い音を、音の長さから絞って読み直す。音の間隔はキーを押している間や実行の最初に伸びることがあり、
        /// そのとき間隔で絞ると正しい周期が窓から外れるが、音の長さは変わらない。
        /// </summary>
        int[] RereadFrame(int f)
        {
            var result = new int[frames[f].Length];
            for (int k = 0; k < result.Length; k++)
            {
                int i = frames[f][k];
                double period = periods[i];
                if (period < ReliablePeriod)
                {
                    double tone = durations[i] - shortToneRing;     // 値がまだ分からないので、短い音に共通の響きの分で見積もる
                    period = reader.Find(tones[i], (tone - DurationWindow) * 256 / Periods, (tone + DurationWindow) * 256 / Periods);
                }
                result[k] = Nearest(period, frameScale[f]).Value;
            }
            return result;
        }

        /// <summary>検査値に合うように、読みの怪しい音を 2 番目の候補へ入れ替えて試す。候補がただ 1 つのときだけ採用する。</summary>
        int[]? Repair(int f, int[] values, int address)
        {
            var candidates = Enumerable.Range(0, FrameTones)
                .Select(k => (k, reading: Nearest(periods[frames[f][k]], frameScale[f])))
                .OrderBy(c => c.reading.Margin).Take(10).ToArray();
            int[]? found = null;
            int hits = 0;
            void Try(params (int k, Reading reading)[] flips)
            {
                var trial = (int[])values.Clone();
                foreach (var (k, reading) in flips) trial[k] = reading.Value == values[k] ? reading.Second : reading.Value;
                if (Passes(trial, address)) { found = trial; hits++; }
            }
            // 2 音以上を入れ替えると、偶然検査値に合う誤った組み合わせを受け入れてしまう（実機の録音で確かめた）
            foreach (var c in candidates) Try(c);
            return hits == 1 ? found : null;
        }

        static byte[] ToData(int[] v)
        {
            var data = new byte[DataBytes];
            for (int k = 0; k < DataBytes; k++) data[k] = (byte)v[k + 1];
            return data;
        }

        static double Median(IEnumerable<double> values)
        {
            var sorted = values.OrderBy(v => v).ToArray();
            return sorted.Length == 0 ? double.NaN : sorted[sorted.Length / 2];
        }
    }
}
