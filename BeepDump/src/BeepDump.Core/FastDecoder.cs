namespace BeepDump.Core;

/// <summary>
/// 機械語版（fast/beepfast.asm）の音を読む。ビットの間隔が短く、前の切り替えの響きが次のビットに重なるので、
/// 最初の学習用の並びからブザーの応答（切り替え 1 回の波形）を覚え、響きを差し引きながら 1 ビットずつ決める。
/// </summary>
public static class FastDecoder
{
    const double BurstGap = 0.0035;     // 秒。ブロックの間の無音（実機で約 10 ms）を区切る
    const double LongBurst = 0.15;      // 秒。BASIC 版の音はこれより十分短い
    const double RunGap = 2.0;          // 秒。これより長い無音で別の実行とみなす（機械語を書き込むのに 2 秒より長くかかる）
    const double ResponseLength = 0.006; // 秒。学習用の並びは短いので、長い応答は求められない
    const double LongResponse = 0.020;   // 秒。読めたブロックから求める。筐体の響きが長い機種がある（PC-U6000 は 6 ms でも大きく残る）
    const int HeadBytes = 3, CheckBytes = 6, BlockBytes = HeadBytes + Decoder.DataBytes + CheckBytes;
    const byte LearnSync = 0x5A, BlockSync = 0xA5;
    const int RefitBlocks = 16, RefitRounds = 3, HeadChoices = 5, SampleBlocks = 32, NearbyBlocks = 4;
    const double ZeroBlockResidual = 0.5;
    const double JoinedBurst = 0.8;      // 秒。学習用の並びとブロック 1 つを合わせた長さの目安より短い
    const double LearnSeconds = 0.44;    // 秒。規定の速さ（待ち 130）での学習用の並びの長さ
    const int StrayDistance = 0x1000;    // S2 が見ていない番地のビットの最小の違い
    const double QuietBurst = 0.25;      // 典型的な塊に対する音量の比
    const int LearnRetries = 4;
    const int RepairBits = 48, RepairPairs = 10;   // 自信のないビットを 1 つずつ、上位どうしは 2 つ組で試す

    public static bool LooksFast(float[] x, int rate, double relative = 0.12)
    {
        var tones = Segmenter.Find(x, rate, BurstGap, relative: relative);
        var bursts = LongBursts(x, rate, tones);
        if (bursts.Count == 0) return false;
        // BASIC 版は短い音が大半で、長い塊があれば物音。機械語版は長い塊が大半
        var shorter = tones.Where(t => t.End - t.Start <= LongBurst * rate).ToArray();
        long shortTime = shorter.Sum(t => (long)(t.End - t.Start)), longTime = bursts.Sum(t => (long)(t.End - t.Start));
        if (longTime <= shortTime) return false;
        // 短い録り直しに長い物音が入ると時間では負ける。音の数がブロックの半分以上あれば BASIC 版
        if (shorter.Length >= Decoder.FrameTones / 2 && longTime < shortTime * 10) return false;
        // 響きの長い機種の短い録り直しでは、学習用の並びとブロックが 1 つの塊になる
        return bursts.Count >= 2 || (bursts[0].End - bursts[0].Start) / (double)rate > JoinedBurst;
    }

    /// <summary>長い音の塊。録音の前後の部屋の雑音も長い音に見えるので、本来の音より極端に小さいものは除く。</summary>
    static List<Tone> LongBursts(float[] x, int rate, List<Tone> tones)
    {
        var bursts = tones.Where(b => (b.End - b.Start) / (double)rate > LongBurst).ToList();
        if (bursts.Count == 0) return bursts;
        var levels = bursts.Select(b =>
        {
            double sum = 0;
            for (int i = b.Start; i < b.End; i++) sum += (double)x[i] * x[i];
            return Math.Sqrt(sum / (b.End - b.Start));
        }).ToArray();
        double typical = levels.Order().ElementAt(levels.Length / 2);   // 大きな物音に引きずられないように中央値で見る
        return bursts.Where((_, i) => levels[i] > QuietBurst * typical).ToList();
    }

    /// <param name="wanted">指定があれば、この番地のブロックだけ読む（学習に使うブロックは別に少し読む）</param>
    public static DecodeResult Decode(float[] x, int rate, Action<string, double>? progress = null, double relative = 0.12, int lookahead = 3, Func<int, bool>? wanted = null)
    {
        var notes = new List<string>();
        var bursts = LongBursts(x, rate, Segmenter.Find(x, rate, BurstGap, relative: relative));
        var blocks = new List<Block>();
        double speed = 0;
        Run? previous = null;
        int done = 0, from = 0;
        for (int i = 1; i <= bursts.Count; i++)
        {
            if (i < bursts.Count && (bursts[i].Start - bursts[i - 1].End) / (double)rate < RunGap) continue;
            int count = i - from;
            var run = new Run(x, rate, lookahead, wanted, bursts.GetRange(from, count), notes, f => progress?.Invoke(Messages.Pick("音を読み取り中", "Reading tones"), (done + f * count) / bursts.Count));
            blocks.AddRange(run.Decode(previous));
            if (run.Speed > 0 && run.ReadAny) { speed = run.Speed; previous = run; }
            done += count;
            from = i;
        }
        if (speed > 0) notes.Add(Messages.Pick($"機械語版、約 {speed:F0} bps", $"Machine-code version, about {speed:F0} bps"));
        if (blocks.Count == 0) notes.Add(Messages.Pick("機械語版の音の並びが読めませんでした。", "Could not read the machine-code tone sequence."));
        return new(blocks, 0, 0, bursts.Count, notes);
    }

    /// <summary>
    /// 時間の単位はサンプル。1 ビットは T、1 のビットは E だけ長い。ビットの中央の切り替えは M 後、データのバイトの切れ目で G 余分にかかる。
    /// ほかの切れ目は通る処理が違うので、機械語のサイクル数（データの切れ目を 36 として）の比で求める。
    /// </summary>
    readonly record struct Timing(double T, double M, double G, double E)
    {
        public double Longest => 8 * (T + E) + G * 48 / 36;

        /// <summary>index 番目のバイトのあとの切れ目で余分にかかる時間。</summary>
        public double After(int index) => G * Ratio(index);

        /// <summary>index 番目のバイトのあとの切れ目の、G に対する比。</summary>
        public static double Ratio(int index) => (index switch
        {
            0 => 12,
            1 => 7,
            2 => 48,
            < HeadBytes + Decoder.DataBytes - 1 => 36,
            HeadBytes + Decoder.DataBytes - 1 => 42,
            _ => 22,
        }) / 36.0;
    }

    readonly record struct Edge(double Time, bool Up);

    sealed class Response(int length)
    {
        public readonly double[] Up = new double[length], Down = new double[length];
        public readonly int Length = length;

        /// <summary>波形の傾き。</summary>
        public Response Slope()
        {
            var r = new Response(Length);
            for (int k = 1; k < Length - 1; k++)
            {
                r.Up[k] = (Up[k + 1] - Up[k - 1]) / 2;
                r.Down[k] = (Down[k + 1] - Down[k - 1]) / 2;
            }
            return r;
        }

        /// <summary>時刻 t（小数）に切り替えの波形を足す。</summary>
        public void Add(double[] y, double t, bool up, double gain)
        {
            var h = up ? Up : Down;
            int i = (int)Math.Floor(t);
            double f = t - i, a = gain * (1 - f), b = gain * f;
            int to = Math.Min(Length, y.Length - i - 1);
            for (int k = Math.Max(0, -i); k < to; k++)
            {
                y[i + k] += a * h[k];
                y[i + k + 1] += b * h[k];
            }
        }
    }

    sealed class Run(float[] x, int rate, int lookahead, Func<int, bool>? wanted, List<Tone> bursts, List<string> notes, Action<double> progress)
    {
        int length = (int)(ResponseLength * rate);
        Response response = new(0);
        Timing timing;
        (Tone Burst, byte[] Bytes, double Start) learned = (default, [], 0);

        public double Speed { get; private set; }

        /// <summary>検査の通るブロックがあったか。なければ学習結果を信用できない。</summary>
        public bool ReadAny { get; private set; }

        /// <summary>バイト列の切り替えを順に作る。戻り値は次のバイトの開始時刻。</summary>
        static double Walk(IEnumerable<byte> bytes, double start, Timing t, ref bool level, Action<Edge>? edge)
        {
            double cell = start;
            int index = 0;
            foreach (byte value in bytes)
            {
                for (int n = 0; n < 8; n++)
                {
                    int bit = value >> n & 1;
                    level = !level; edge?.Invoke(new(cell, level));
                    if (bit != 0) { level = !level; edge?.Invoke(new(cell + t.M, level)); }
                    cell += t.T + bit * t.E;
                }
                cell += t.After(index);
                index++;
            }
            return cell;
        }

        static List<Edge> EdgesOf(IEnumerable<byte> bytes, double start, Timing t)
        {
            var edges = new List<Edge>();
            bool level = false;
            Walk(bytes, start, t, ref level, edges.Add);
            return edges;
        }

        static byte[] LearnBytes() => [0, 0, LearnSync, .. FastCode.Learn];

        /// <summary>前の版の機械語の学習用の並び。ON キーを見る命令の 1 バイト（SSR の FFh）が ISR の FCh だった。その版で録音したものも読めるようにする。</summary>
        static byte[] OlderLearnBytes()
        {
            var bytes = LearnBytes();
            for (int i = HeadBytes; i + 3 < bytes.Length; i++)
                if (bytes[i] == 0x30 && bytes[i + 1] == 0x65 && bytes[i + 2] == 0xFF && bytes[i + 3] == 0x08) bytes[i + 2] = 0xFC;
            return bytes;
        }

        public IEnumerable<Block> Decode(Run? previous)
        {
            // 録音の終わりの物音など、音の塊が 1 つだけで読めないものは実行のまとまりとして扱わない
            if (previous != null && bursts.Count == 1 && (bursts[0].End - bursts[0].Start) / (double)rate < 0.3) return [];
            // 長い物音で途中から始まっていることがあるので、前の学習結果で読めれば、それはデータのブロックとして読む
            bool continued = false;
            if (previous != null)
            {
                (timing, response, length, learned) = (previous.timing, previous.response, previous.length, previous.learned);
                continued = AddressOf(ReadWithShifts(bursts[0], out _)) >= 0;
            }
            if (!continued)
            {
                length = (int)(ResponseLength * rate);   // 前の実行で長い応答に伸ばしていても、学習は短い応答からやり直す
                bursts = SplitLearn(bursts);
            }
            if (!continued && (bursts.Count < 2 || !Learn(bursts[0])))
            {
                notes.Add(Messages.Pick("学習用の並びが読めませんでした。", "Could not read the learning sequence."));
                if (previous == null) return [];
                (timing, response, length, learned) = (previous.timing, previous.response, previous.length, previous.learned);
                continued = true;
            }
            Speed = rate / (timing.T + timing.E / 2 + timing.G / 8);
            var blocks = Split(continued ? bursts : bursts.Skip(1)).ToArray();
            var read = new byte[blocks.Length][];
            var starts = new double[blocks.Length];
            var addresses = new int[blocks.Length];
            Array.Fill(addresses, -1);
            void Read(int i)
            {
                read[i] = ReadWithShifts(blocks[i], out starts[i]);
                addresses[i] = AddressOf(read[i]);
            }

            // 番地はブロックの順番から分かるので、読まなくてよいブロックを飛ばせる
            bool[] skip = new bool[blocks.Length];
            if (wanted != null)
            {
                int sample = Math.Max(1, blocks.Length / SampleBlocks);
                Parallel.For(0, blocks.Length, i => { if (i % sample == 0) Read(i); });
                // 物音で塊が割れると番号がずれるので、いちばん近い読めたブロックから番地を求める
                RejectStrayAddresses(addresses);
                int[] known = Enumerable.Range(0, blocks.Length).Where(i => addresses[i] >= 0).ToArray();
                if (known.Length > 0)
                    for (int i = 0; i < blocks.Length; i++)
                    {
                        int near = known.MinBy(n => Math.Abs(n - i));
                        int address = (addresses[near] + (i - near) * Decoder.DataBytes) & 0xFFFFF;
                        // 塊が割れると番号が 1 つずれることがあるので、前後の番地も見る
                        skip[i] = addresses[i] < 0 && !wanted(address) && !wanted((address + Decoder.DataBytes) & 0xFFFFF) && !wanted((address - Decoder.DataBytes) & 0xFFFFF);
                    }
            }
            int finished = 0;
            Parallel.For(0, blocks.Length, i =>
            {
                if (read[i] == null && !skip[i]) Read(i);
                int n = Interlocked.Increment(ref finished);
                if (n % 4 == 0) progress(0.2 + 0.7 * n / blocks.Length);
            });

            // 学習用の並びでない塊から学ぶと、切り方も読み方も狂って 1 つも読めない。学び直して全部切り直す
            void Restart(IEnumerable<Tone> tones)
            {
                Speed = rate / (timing.T + timing.E / 2 + timing.G / 8);
                blocks = Split(tones).ToArray();
                read = new byte[blocks.Length][];
                starts = new double[blocks.Length];
                addresses = new int[blocks.Length];
                skip = new bool[blocks.Length];
                Parallel.For(0, blocks.Length, Read);
            }
            if (!continued && addresses.All(a => a < 0))
            {
                if (previous != null)
                {
                    // 前の実行の学習結果で読む。先頭の塊もデータのブロックかもしれない
                    (timing, response, length, learned) = (previous.timing, previous.response, previous.length, previous.learned);
                    Restart(bursts);
                }
                else
                {
                    // 学習用の並びの前の物音から学んだのかもしれない。次の塊から学び直す
                    for (int k = 1; k <= LearnRetries && k + 1 < bursts.Count && addresses.All(a => a < 0); k++)
                        if (Learn(bursts[k])) Restart(bursts.Skip(k + 1));
                }
            }

            // 読めたブロックでブザーの応答とビットの時間を覚え直し、読めなかったブロックを読み直す。読めるものが増える間くり返す
            for (int round = 0; round < RefitRounds; round++)
            {
                RejectStrayAddresses(addresses);
                int[] good = Enumerable.Range(0, blocks.Length).Where(i => addresses[i] >= 0).ToArray();
                if (good.Length == 0 || Enumerable.Range(0, blocks.Length).All(i => addresses[i] >= 0 || skip[i])) break;
                // 同じ値ばかりのブロックは周期的な音で応答が決まらないので、内容がばらけたブロックを選ぶ
                var chosen = good.OrderByDescending(i => read[i].Distinct().Count()).Take(RefitBlocks).Select(i => (blocks[i], read[i], starts[i]));
                Refit(learned.Bytes.Length > 0 ? chosen.Append(learned).ToArray() : chosen.ToArray());
                int gained = 0;
                Parallel.For(0, blocks.Length, i =>
                {
                    if (addresses[i] >= 0 || skip[i]) return;
                    var again = ReadWithShifts(blocks[i], out double start);
                    int address = AddressOf(again);
                    read[i] = again;
                    if (address >= 0) { addresses[i] = address; starts[i] = start; Interlocked.Increment(ref gained); }
                });
                if (gained == 0) break;
            }

            RejectStrayAddresses(addresses);

            // 読めなかったブロックの番地は前後から求める。そこまで分かれば、1〜2 ビットの誤りは直せる
            var expected = new int[blocks.Length];
            for (int i = 0; i < blocks.Length; i++)
            {
                int near = Enumerable.Range(0, blocks.Length).Where(n => addresses[n] >= 0).OrderBy(n => Math.Abs(n - i)).DefaultIfEmpty(-1).First();
                expected[i] = near < 0 ? -1 : (addresses[near] + (i - near) * Decoder.DataBytes) & 0xFFFFF;
            }
            int[] failed = Enumerable.Range(0, blocks.Length).Where(i => addresses[i] < 0 && !skip[i] && expected[i] >= 0).ToArray();
            var repaired = new byte[blocks.Length][];
            if (failed.Length <= RepairLimit(blocks.Length))
                Parallel.ForEach(failed, i => repaired[i] = Repair(blocks[i], expected[i])!);
            progress(1);

            ReadAny = addresses.Any(a => a >= 0);
            // 比べる相手がいないと、番地の上位の読み違いを見つけられない。読めたブロックが 1 つだけなら緑にしない
            bool alone = addresses.Count(a => a >= 0) == 1;
            if (alone) notes.Add(Messages.Pick("読めたブロックが 1 つだけなので、番地を確かめられません。前後を含めて録音すると確かです。", "Only one block was read, so its address cannot be confirmed. Recording its neighbors too makes it certain."));
            var result = new List<Block>();
            for (int i = 0; i < blocks.Length; i++)
            {
                if (addresses[i] >= 0) result.Add(new(addresses[i], alone ? BlockState.Repaired : BlockState.Good, read[i][HeadBytes..(HeadBytes + Decoder.DataBytes)]));
                else if (skip[i]) continue;
                else if (repaired[i] != null) result.Add(new(expected[i], BlockState.Repaired, repaired[i][HeadBytes..(HeadBytes + Decoder.DataBytes)]));
                else if (expected[i] >= 0) result.Add(new(expected[i], BlockState.Failed, []));
            }
            return result;
        }

        /// <summary>
        /// 前後の読めたブロックと番地がつながらないものを読めなかったことにする。S2 は番地の 256 倍を足す形なので
        /// 番地の上位 8 bit を見ておらず、S1 の上位のビットを読み違えると、番地だけ違うまま検査が通る。
        /// </summary>
        static void RejectStrayAddresses(int[] addresses)
        {
            var keep = (int[])addresses.Clone();
            int[] good = Enumerable.Range(0, keep.Length).Where(i => keep[i] >= 0).ToArray();
            for (int g = 0; g < good.Length; g++)
            {
                int i = good[g], agree = 0, differ = 0;
                for (int h = Math.Max(0, g - NearbyBlocks); h <= Math.Min(good.Length - 1, g + NearbyBlocks); h++)
                {
                    if (h == g) continue;
                    int j = good[h];
                    // 塊の割れや欠けで番号はずれることがあるので、ぴったりとは比べない。S1 の読み違いなら StrayDistance 以上離れる
                    int gap = (keep[j] - keep[i] - (j - i) * Decoder.DataBytes) & 0xFFFFF;
                    if (Math.Min(gap, 0x100000 - gap) < StrayDistance) agree++;
                    else differ++;
                }
                if (differ > agree) addresses[i] = -1;
            }
        }

        /// <summary>読めないブロックが多いときは録音そのものに問題があるので、時間をかけない。</summary>
        static int RepairLimit(int blocks) => Math.Max(8, blocks / 4);

        /// <summary>最もよく合う読み方を基に、自信のないビットを反転して読み直す。検査が通り、番地が前後と合えば採る。</summary>
        byte[]? Repair(Tone burst, int address)
        {
            Reading? best = null;
            (double Start, double Gain) bestHead = default;
            double bestShift = 0;
            foreach (var candidate in HeadCandidates(burst))
            {
                foreach (double shift in new[] { 0, 0.5, -0.5 })
                {
                    var reading = ReadBlock(burst, candidate, shift);
                    if (best == null || reading.Energy < best.Energy) (best, bestHead, bestShift) = (reading, candidate, shift);
                }
            }
            if (best == null) return null;
            int Flipped(int position) => 1 - (best.Bytes[HeadBytes + position / 8] >> position % 8 & 1);
            var weak = Enumerable.Range(0, best.Margins.Length).OrderBy(p => best.Margins[p]).Take(RepairBits).ToArray();
            foreach (int p in weak)
            {
                var again = ReadBlock(burst, bestHead, bestShift, p, Flipped(p));
                if (AddressOf(again.Bytes) == address) return again.Bytes;
            }
            for (int a = 0; a < RepairPairs; a++)
            {
                for (int b = a + 1; b < RepairPairs; b++)
                {
                    var again = ReadBlock(burst, bestHead, bestShift, weak[a], Flipped(weak[a]), weak[b], Flipped(weak[b]));
                    if (AddressOf(again.Bytes) == address) return again.Bytes;
                }
            }
            return null;
        }

        /// <summary>from から to の間で、音がいちばん小さい 2 ms の終わりの位置を返す。次の音はそこから始まる。</summary>
        int Quietest(int from, int to)
        {
            int quiet = (int)(0.002 * rate);
            double energy = 0, best = double.MaxValue;
            int cut = -1;
            for (int i = from; i < to; i++)
            {
                energy += (double)x[i] * x[i];
                if (i - from >= quiet) energy -= (double)x[i - quiet] * x[i - quiet];
                if (i - from >= quiet - 1 && energy < best) { best = energy; cut = i + 1; }
            }
            return cut;
        }

        /// <summary>学習用の並びと最初のブロックが 1 つの塊に見えていれば切る。ブロックの長さから学習用の並びの長さが分かる。</summary>
        List<Tone> SplitLearn(List<Tone> tones)
        {
            if (tones.Count == 0) return tones;
            // ほかに塊がなければブロックの長さが分からないので、規定の速さの長さを使う
            var lengths = tones.Skip(1).Select(t => t.End - t.Start).OrderBy(n => n).ToArray();
            double expected = lengths.Length > 0 ? (double)lengths[lengths.Length / 4] * LearnBytes().Length / BlockBytes : LearnSeconds * rate;
            var first = tones[0];
            if (first.End - first.Start < expected * 1.25) return tones;
            int cut = Quietest(first.Start + (int)(expected * 0.93), Math.Min(first.End, first.Start + (int)(expected * 1.1)));
            if (cut < 0) return tones;
            return [new(first.Start, cut), new(cut, first.End), .. tones.Skip(1)];
        }

        /// <summary>
        /// 響きが長いとブロックの間の無音が埋まり、いくつものブロックが 1 つの塊に見える。
        /// ビットの時間からブロックの長さは分かるので、その前後でいちばん静かな所で切る。
        /// </summary>
        IEnumerable<Tone> Split(IEnumerable<Tone> tones)
        {
            double Shortest() { double t = 0; for (int b = 0; b < BlockBytes - 1; b++) t += 8 * timing.T + timing.After(b); return t + 8 * timing.T; }
            double shortest = Shortest(), longest = shortest + BlockBytes * 8 * timing.E;
            int quiet = (int)(0.002 * rate);
            foreach (var tone in tones)
            {
                int start = tone.Start;
                while (tone.End - start > longest * 1.25)
                {
                    int cut = Quietest(start + (int)(shortest - 0.003 * rate), Math.Min(tone.End, start + (int)(longest + 0.016 * rate)));
                    if (cut < 0) break;
                    yield return new(start, cut - quiet);
                    start = cut;
                }
                yield return new(start, tone.End);
            }
        }

        /// <summary>検査値が通れば番地を返す。S1 = 番地 + データの和、S2 = S1 の累積和（どちらも 20 bit）。</summary>
        static int AddressOf(byte[] bytes)
        {
            if (bytes[2] != BlockSync) return -1;
            int sum = 0;
            for (int k = 0; k < Decoder.DataBytes; k++) sum += bytes[HeadBytes + k];
            int c = HeadBytes + Decoder.DataBytes;
            if (bytes[c + 2] > 15 || bytes[c + 5] > 15) return -1;
            int s1 = bytes[c] | bytes[c + 1] << 8 | bytes[c + 2] << 16, s2 = bytes[c + 3] | bytes[c + 4] << 8 | bytes[c + 5] << 16;
            int address = (s1 - sum) & 0xFFFFF;
            if ((address & 255) != 0) return -1;
            int running = address, total = 0;
            for (int k = 0; k < Decoder.DataBytes; k++)
            {
                running = (running + bytes[HeadBytes + k]) & 0xFFFFF;
                total = (total + running) & 0xFFFFF;
            }
            return total == s2 ? address : -1;
        }

        /// <summary>学習用の並びから、ビットの時間とブザーの応答を求める。</summary>
        bool Learn(Tone burst)
        {
            bool ok = LearnFrom(burst, LearnBytes(), out double cost);
            if (!ok) return false;
            var (keepTiming, keepResponse, keepLearned) = (timing, response, learned);
            if (LearnFrom(burst, OlderLearnBytes(), out double olderCost) && olderCost < cost) return true;
            (timing, response, learned) = (keepTiming, keepResponse, keepLearned);
            return true;
        }

        bool LearnFrom(Tone burst, byte[] bytes, out double cost)
        {
            cost = double.MaxValue;
            double byteTime = (burst.End - burst.Start - 0.002 * rate) / (bytes.Length - 0.1);
            double cell = FindCell(burst, byteTime);
            if (cell <= 0) return false;
            // 時間のわずかな誤差は長く当てはめるほど積もって手がかりがなくなるので、先頭の短い範囲で合わせてから順に広げる
            timing = new Timing(cell, cell / 2, Math.Max(0, byteTime - 8 * cell), 0);
            double start = burst.Start;
            foreach (int count in new[] { 3, 4, 5, 6, 8, 10, 13, 16, 20, 25, 32, 40, 50, 64, 80, 100, bytes.Length })
            {
                var part = bytes[..count];
                var range = count == bytes.Length ? burst : new Tone(burst.Start, Math.Min(burst.End, burst.Start + (int)((count + 1) * timing.Longest)));
                for (int round = 0; round < 4; round++)
                {
                    (response, _) = Fit(range, part, start, timing, 25);
                    (timing, start) = Refine(range, part, start, timing);
                }
            }
            // 仕上げ: 全体で繰り返し合わせる
            for (int round = 0; round < 10; round++)
            {
                (response, _) = Fit(burst, bytes, start, timing, 30);
                (timing, start) = Refine(burst, bytes, start, timing);
            }
            (response, cost) = Fit(burst, bytes, start, timing, 40);
            learned = (burst, bytes, start);
            return true;
        }

        /// <summary>先頭の $00 は 1 ビットに 1 回の切り替えなので、2 ビット周期で波形が繰り返す。1 バイトの長さから範囲を絞って探す。</summary>
        double FindCell(Tone burst, double byteTime)
        {
            int shortest = (int)(byteTime / 4.6), longest = (int)Math.Ceiling(byteTime / 3.7);
            if (shortest < 2) return 0;
            var score = new double[longest + 3];
            for (int lag = shortest - 1; lag <= longest + 1; lag++)
            {
                int window = (longest + 1) * 7 - lag, s = burst.Start;
                if (s + window + lag >= x.Length) return 0;
                double xy = 0, xx = 0, yy = 0;
                for (int i = 0; i < window; i++) { xy += x[s + i] * x[s + i + lag]; xx += x[s + i] * x[s + i]; yy += x[s + i + lag] * x[s + i + lag]; }
                score[lag] = xy / Math.Sqrt(xx * yy + 1e-30);
            }
            int best = shortest;
            for (int lag = shortest; lag <= longest; lag++) if (score[lag] > score[best]) best = lag;
            double d = score[best - 1] - 2 * score[best] + score[best + 1];
            return (best + (d < 0 ? 0.5 * (score[best - 1] - score[best + 1]) / d : 0)) / 2;
        }

        /// <summary>
        /// 切り替えの時刻はパラメータ（開始, T, M, G, E）の一次式なので、音の残りを波形の傾きで説明するように同時に解く（ガウス・ニュートン法）。
        /// </summary>
        (Timing, double) Refine(Tone burst, byte[] bytes, double start, Timing t)
        {
            const int Count = 5;
            int origin = burst.Start - length, size = burst.End - burst.Start + 2 * length;
            var slope = response.Slope();
            var model = new double[size];
            var columns = new double[Count][];
            for (int p = 0; p < Count; p++) columns[p] = new double[size];
            var c = new double[Count];
            double cell = start - origin;
            bool level = false;
            void Place(double time, bool up)
            {
                response.Add(model, time, up, 1);
                for (int p = 0; p < Count; p++) if (c[p] != 0) slope.Add(columns[p], time, up, -c[p]);
            }
            c[0] = 1;
            for (int b = 0; b < bytes.Length; b++)
            {
                for (int n = 0; n < 8; n++)
                {
                    int bit = bytes[b] >> n & 1;
                    level = !level; Place(cell, level);
                    if (bit != 0)
                    {
                        level = !level;
                        double keep = c[2];
                        c[2] = 1;
                        Place(cell + t.M, level);
                        c[2] = keep;
                    }
                    cell += t.T + bit * t.E;
                    c[1] += 1; c[4] += bit;
                }
                cell += t.After(b);
                c[3] += Timing.Ratio(b);
            }
            var residual = new double[size];
            for (int i = 0; i < size; i++) { int n = origin + i; residual[i] = (n >= 0 && n < x.Length ? x[n] : 0) - model[i]; }
            var matrix = new double[Count, Count + 1];
            for (int p = 0; p < Count; p++)
            {
                for (int q = p; q < Count; q++)
                {
                    double sum = 0;
                    for (int i = 0; i < size; i++) sum += columns[p][i] * columns[q][i];
                    matrix[p, q] = matrix[q, p] = sum;
                }
                double right = 0;
                for (int i = 0; i < size; i++) right += columns[p][i] * residual[i];
                matrix[p, Count] = right;
            }
            for (int p = 0; p < Count; p++) matrix[p, p] = matrix[p, p] * 1.001 + 1e-12;
            var delta = Solve(matrix, Count);
            // 1 回の更新で切り替えの時刻を動かしすぎない（T と G は打ち消し合うので、実際の時刻のずれで見る）
            double largest = 0, shift = delta[0];
            for (int b = 0; b < bytes.Length; b++)
            {
                for (int n = 0; n < 8; n++)
                {
                    int bit = bytes[b] >> n & 1;
                    largest = Math.Max(largest, Math.Abs(shift) + bit * Math.Abs(delta[2]));
                    shift += delta[1] + bit * delta[4];
                }
                shift += Timing.Ratio(b) * delta[3];
            }
            double scale = largest > 1 ? 1 / largest : 1;
            return (new Timing(t.T + scale * delta[1], t.M + scale * delta[2], t.G + scale * delta[3], t.E + scale * delta[4]), start + scale * delta[0]);
        }

        static double[] Solve(double[,] m, int n)
        {
            for (int k = 0; k < n; k++)
            {
                int pivot = k;
                for (int i = k + 1; i < n; i++) if (Math.Abs(m[i, k]) > Math.Abs(m[pivot, k])) pivot = i;
                if (pivot != k) for (int j = k; j <= n; j++) (m[k, j], m[pivot, j]) = (m[pivot, j], m[k, j]);
                if (Math.Abs(m[k, k]) < 1e-30) continue;
                for (int i = k + 1; i < n; i++)
                {
                    double f = m[i, k] / m[k, k];
                    for (int j = k; j <= n; j++) m[i, j] -= f * m[k, j];
                }
            }
            var result = new double[n];
            for (int i = n - 1; i >= 0; i--)
            {
                double v = m[i, n];
                for (int j = i + 1; j < n; j++) v -= m[i, j] * result[j];
                result[i] = Math.Abs(m[i, i]) < 1e-30 ? 0 : v / m[i, i];
            }
            return result;
        }

        /// <summary>切り替えの時刻が分かっている音から応答を最小二乗で求める（共役勾配法）。</summary>
        (Response, double) Fit(Tone burst, IEnumerable<byte> bytes, double start, Timing t, int iterations) =>
            FitEdges([(burst, EdgesOf(bytes, start - (burst.Start - length), t))], iterations);

        (Response, double) FitEdges((Tone Burst, List<Edge> Edges)[] parts, int iterations)
        {
            var targets = parts.Select(p =>
            {
                int origin = p.Burst.Start - length;
                var y = new double[p.Burst.End - p.Burst.Start + 2 * length];
                for (int i = 0; i < y.Length; i++) { int n = origin + i; y[i] = n >= 0 && n < x.Length ? x[n] : 0; }
                return y;
            }).ToArray();
            int[] Offsets()
            {
                var o = new int[parts.Length];
                for (int p = 1; p < parts.Length; p++) o[p] = o[p - 1] + targets[p - 1].Length;
                return o;
            }
            Response ToResponse(double[] h)
            {
                var r = new Response(length);
                h.AsSpan(0, length).CopyTo(r.Up);
                h.AsSpan(length, length).CopyTo(r.Down);
                return r;
            }
            double[] Forward(double[] h)
            {
                var r = ToResponse(h);
                var all = new double[targets.Sum(y => y.Length)];
                var offsets = Offsets();
                Parallel.For(0, parts.Length, p =>
                {
                    var y = new double[targets[p].Length];
                    foreach (var e in parts[p].Edges) r.Add(y, e.Time, e.Up, 1);
                    y.CopyTo(all, offsets[p]);
                });
                return all;
            }
            double[] Adjoint(double[] v)
            {
                var offsets = Offsets();
                var each = new double[parts.Length][];
                Parallel.For(0, parts.Length, p =>
                {
                    var g = each[p] = new double[2 * length];
                    int size = targets[p].Length, offset = offsets[p];
                    foreach (var e in parts[p].Edges)
                    {
                        int i = (int)Math.Floor(e.Time), o = e.Up ? 0 : length;
                        double f = e.Time - i;
                        for (int k = 0; k < length; k++)
                        {
                            int n = i + k;
                            if (n >= 0 && n < size) g[o + k] += (1 - f) * v[offset + n];
                            if (n + 1 >= 0 && n + 1 < size) g[o + k] += f * v[offset + n + 1];
                        }
                    }
                });
                var sum = new double[2 * length];
                foreach (var g in each) for (int k = 0; k < sum.Length; k++) sum[k] += g[k];
                return sum;
            }
            var target = targets.SelectMany(y => y).ToArray();
            var h = new double[2 * length];
            var residual = (double[])target.Clone();
            var gradient = Adjoint(residual);
            var direction = (double[])gradient.Clone();
            double gg = gradient.Sum(v => v * v);
            for (int it = 0; it < iterations && gg > 1e-30; it++)
            {
                var ad = Forward(direction);
                double alpha = gg / Math.Max(1e-30, ad.Sum(v => v * v));
                for (int i = 0; i < h.Length; i++) h[i] += alpha * direction[i];
                for (int i = 0; i < residual.Length; i++) residual[i] -= alpha * ad[i];
                gradient = Adjoint(residual);
                double next = gradient.Sum(v => v * v), beta = next / gg;
                for (int i = 0; i < direction.Length; i++) direction[i] = gradient[i] + beta * direction[i];
                gg = next;
            }
            return (ToResponse(h), residual.Sum(v => v * v));
        }

        void Refit((Tone Burst, byte[] Bytes, double Start)[] good)
        {
            length = (int)(LongResponse * rate);
            var starts = good.Select(g => g.Start).ToArray();
            (Tone Burst, List<Edge> Edges)[] Parts() => good.Select((g, i) => (g.Burst, EdgesOf(g.Bytes, starts[i] - (g.Burst.Start - length), timing))).ToArray();
            (response, _) = FitEdges(Parts(), 60);
            for (int pass = 0; pass < 3; pass++)
            {
                var refined = new (Timing Timing, double Start)[good.Length];
                var current = timing;
                Parallel.For(0, good.Length, i => refined[i] = Refine(good[i].Burst, good[i].Bytes, starts[i], current));
                timing = new Timing(refined.Average(r => r.Timing.T), refined.Average(r => r.Timing.M), refined.Average(r => r.Timing.G), refined.Average(r => r.Timing.E));
                for (int i = 0; i < good.Length; i++) starts[i] = refined[i].Start;
                (response, _) = FitEdges(Parts(), 60);
            }
        }

        /// <summary>
        /// 開始位置の候補と、同期とデータの間の時間の誤差を順に試す。先頭の $00 は周期的な音なので、
        /// 合う開始位置がビット 2 つ分ごとに現れ、いちばん合う所が本当の開始とは限らない。検査の通るものを採る。
        /// </summary>
        byte[] ReadWithShifts(Tone burst, out double start)
        {
            var candidates = HeadCandidates(burst);
            if (candidates.Count == 0) { start = burst.Start; return new byte[BlockBytes]; }
            var first = ReadBlock(burst, candidates[0], 0).Bytes;
            start = candidates[0].Start;
            if (AddressOf(first) >= 0) return first;
            foreach (double shift in new[] { 0, 0.5, -0.5, 1, -1, 1.5, -1.5 })
            {
                foreach (var candidate in candidates)
                {
                    if (shift == 0 && candidate == candidates[0]) continue;
                    var again = ReadBlock(burst, candidate, shift).Bytes;
                    if (AddressOf(again) >= 0) { start = candidate.Start; return again; }
                }
            }
            return first;
        }

        /// <summary>分かっている先頭 3 バイトがよく合う開始位置を、よく合う順に返す。時刻は全体のサンプル番号。</summary>
        List<(double Start, double Gain)> HeadCandidates(Tone burst)
        {
            int origin = burst.Start - length;
            int before = (int)(0.003 * rate), after = (int)(0.007 * rate);
            int headEnd = (int)(length + after + 3 * timing.Longest);
            var y = new double[headEnd];
            for (int i = 0; i < headEnd; i++) { int n = origin + i; y[i] = n >= 0 && n < x.Length ? x[n] : 0; }
            byte[] head = [0, 0, BlockSync];
            int steps = (before + after) * 2 + 1;
            var explained = new double[steps];
            var gains = new double[steps];
            var m = new double[headEnd];
            for (int k = 0; k < steps; k++)
            {
                Array.Clear(m);
                foreach (var e in EdgesOf(head, length - before + k * 0.5, timing)) response.Add(m, e.Time, e.Up, 1);
                double xy = 0, mm = 0;
                for (int i = 0; i < headEnd; i++) { xy += m[i] * y[i]; mm += m[i] * m[i]; }
                gains[k] = xy / Math.Max(1e-30, mm);
                explained[k] = gains[k] > 0 ? xy * gains[k] : 0;
            }
            // 山の頂上を、大きい順に。近い山は 1 つとする
            var peaks = Enumerable.Range(0, steps).Where(k => explained[k] > 0 && (k == 0 || explained[k] >= explained[k - 1]) && (k == steps - 1 || explained[k] > explained[k + 1])).OrderByDescending(k => explained[k]);
            var chosen = new List<int>();
            foreach (int k in peaks)
            {
                if (chosen.Count == HeadChoices) break;
                if (chosen.All(c => Math.Abs(c - k) * 0.5 > timing.T / 2)) chosen.Add(k);
            }
            return chosen.Select(k => (origin + length - before + k * 0.5, gains[k])).ToList();
        }

        /// <summary>読み取りの結果。Margins はビットごとの自信（0 と 1 の誤差の差）、Energy は説明できなかった音の大きさ。</summary>
        sealed record Reading(byte[] Bytes, double[] Margins, double Energy);

        /// <summary>1 ブロックを読む。分かっている先頭 3 バイトのあとは 1 ビットずつ決める。forced のビットは指定の値にする。</summary>
        Reading ReadBlock(Tone burst, (double Start, double Gain) head3, double shift, int forcedA = -1, int valueA = 0, int forcedB = -1, int valueB = 0)
        {
            int origin = burst.Start - length;
            int size = (int)(BlockBytes * timing.Longest) + 3 * length + (int)(0.007 * rate);
            var y = new double[size];
            for (int i = 0; i < size; i++) { int n = origin + i; y[i] = n >= 0 && n < x.Length ? x[n] : 0; }
            byte[] head = [0, 0, BlockSync];
            double start = head3.Start - origin, gain = head3.Gain;

            var output = new byte[BlockBytes];
            head.CopyTo(output, 0);
            var residual = (double[])y.Clone();
            bool level = false;
            double decodeGain = gain;
            double cell = Walk(head, start, timing, ref level, e => response.Add(residual, e.Time, e.Up, -decodeGain)) + shift;
            int window = (int)Math.Ceiling((timing.T + timing.E) * lookahead + timing.After(2)) + 8;
            var model = new double[window];   // 比べる範囲だけ作ればよい
            var recent = new List<Edge>();
            var margins = new double[(BlockBytes - HeadBytes) * 8];
            int from = (int)start;
            for (int b = HeadBytes; b < BlockBytes; b++)
            {
                int value = 0;
                for (int n = 0; n < 8; n++)
                {
                    int s = (int)cell, position = (b - HeadBytes) * 8 + n;
                    double error0 = double.MaxValue, error1 = double.MaxValue;
                    // ブロックの最後では、先読みする先のビットがない
                    int span = Math.Min(lookahead, (BlockBytes - b) * 8 - n);
                    for (int combo = 0; combo < 1 << span; combo++)
                    {
                        Array.Clear(model);
                        bool lv = level;
                        double at = cell - s;
                        for (int k = 0; k < span; k++)
                        {
                            int bit = combo >> k & 1;
                            lv = !lv; response.Add(model, at, lv, gain);
                            if (bit != 0) { lv = !lv; response.Add(model, at + timing.M, lv, gain); }
                            at += timing.T + bit * timing.E;
                            if ((n + k) % 8 == 7) at += timing.After(b + (n + k) / 8);
                        }
                        double error = 0;
                        for (int i = 0; i < window && s + i < size; i++) { double d = residual[s + i] - model[i]; error += d * d; }
                        if ((combo & 1) == 0) error0 = Math.Min(error0, error);
                        else error1 = Math.Min(error1, error);
                    }
                    margins[position] = Math.Abs(error0 - error1);
                    int bestBit = position == forcedA ? valueA : position == forcedB ? valueB : error1 < error0 ? 1 : 0;
                    value |= bestBit << n;
                    level = !level; response.Add(residual, cell, level, -gain); recent.Add(new(cell, level));
                    if (bestBit != 0) { level = !level; response.Add(residual, cell + timing.M, level, -gain); recent.Add(new(cell + timing.M, level)); }
                    cell += timing.T + bestBit * timing.E;
                }
                cell += timing.After(b);
                output[b] = (byte)value;
                if ((b - HeadBytes) % 4 == 3 || b - HeadBytes < 4) { cell += Track(residual, recent, cell, ref gain); recent.Clear(); }
            }
            double energy = 0, total = 0;
            for (int i = Math.Max(0, from); i < Math.Min(size, (int)cell); i++) { energy += residual[i] * residual[i]; total += y[i] * y[i]; }
            // データが全部 0 だと S2 が 0 になり、音を読めていなくても S1 の上位が立つだけで検査が通ってしまう。音を説明できていなければ採らない
            if (energy > ZeroBlockResidual * total && output.AsSpan(HeadBytes, Decoder.DataBytes).IndexOfAnyExcept((byte)0) < 0) output[2] = 0;
            return new(output, margins, energy);
        }

        /// <summary>
        /// 時計のわずかなずれと音量の変化を追う。読み終えた範囲の残りのうち、波形の傾きで説明できる分が時間のずれ、
        /// 波形そのもので説明できる分が音量の変化（録音機材の自動音量調整）。
        /// </summary>
        double Track(double[] residual, List<Edge> edges, double now, ref double gain)
        {
            if (edges.Count == 0) return 0;
            int from = (int)edges[0].Time, to = Math.Min(residual.Length, (int)now);
            if (to - from < 16) return 0;
            var m = new double[to - from];
            foreach (var e in edges) response.Add(m, e.Time - from, e.Up, gain);
            double dot = 0, energy = 0, same = 0, power = 0;
            for (int i = 1; i < m.Length - 1; i++)
            {
                double slope = (m[i + 1] - m[i - 1]) / 2;
                dot += residual[from + i] * slope;
                energy += slope * slope;
                same += residual[from + i] * m[i];
                power += m[i] * m[i];
            }
            if (power > 0) gain *= 1 + Math.Clamp(0.5 * same / power, -0.1, 0.1);
            return energy > 0 ? Math.Clamp(-0.7 * dot / energy, -0.4, 0.4) : 0;
        }
    }
}
