namespace BeepDump.Core;

public readonly record struct Tone(int Start, int End);

/// <summary>音を鳴っている区間ごとに区切る。音の高さの情報は使わない。</summary>
public static class Segmenter
{
    /// <param name="minGap">これより短い無音は 1 つの音の中とみなす（低い音は周期の途中で小さくなるため）</param>
    public static List<Tone> Find(float[] x, int rate, double minGap = 0.0045, double shortest = 0.0005, double relative = 0.12)
    {
        int step = Math.Max(1, rate / 12000);
        int half = Math.Max(1, (int)(0.0004 * rate));
        int n = x.Length / step;
        var tones = new List<Tone>();
        if (n < 8) return tones;

        var env = new float[n];
        double sum = 0;
        int lo = 0, hi = 0;
        for (int i = 0; i < n; i++)
        {
            int center = i * step, from = Math.Max(0, center - half), to = Math.Min(x.Length, center + half);
            while (hi < to) { sum += (double)x[hi] * x[hi]; hi++; }
            while (lo < from) { sum -= (double)x[lo] * x[lo]; lo++; }
            env[i] = (float)Math.Sqrt(Math.Max(0, sum) / Math.Max(1, to - from));
        }

        var top = RunningMean(RunningMax(env, (int)(0.1 * rate / step)), (int)(0.05 * rate / step));
        float floor = Math.Max(1e-4f, 4 * Percentile(env, 0.002));   // 機械語版は音がほぼ連続で、無音はごく一部しかない
        var raw = new List<Tone>();
        int start = -1;
        for (int i = 0; i < n; i++)
        {
            bool active = env[i] > Math.Max((float)relative * top[i], floor);
            if (active && start < 0) start = i;
            if (!active && start >= 0) { raw.Add(new(start * step, i * step)); start = -1; }
        }
        if (start >= 0) raw.Add(new(start * step, n * step));

        int gap = (int)(minGap * rate), least = (int)(shortest * rate);
        foreach (var t in raw)
        {
            if (tones.Count > 0 && t.Start - tones[^1].End < gap) tones[^1] = new(tones[^1].Start, t.End);
            else tones.Add(t);
        }
        tones.RemoveAll(t => t.End - t.Start <= least);
        return tones;
    }

    /// <summary>音の立ち上がり（区切りの近くで最大の 1/4 に達する点）。</summary>
    public static int Onset(float[] x, int rate, Tone t)
    {
        int from = Math.Max(0, t.Start - rate / 1000), to = Math.Min(x.Length, t.Start + rate / 500);
        float peak = 0;
        for (int i = from; i < to; i++) peak = Math.Max(peak, Math.Abs(x[i]));
        for (int i = from; i < to; i++) if (Math.Abs(x[i]) > 0.25f * peak) return i;
        return t.Start;
    }

    static float[] RunningMax(float[] x, int width)
    {
        width = Math.Max(1, width);
        var result = new float[x.Length];
        var queue = new int[x.Length];
        int head = 0, tail = 0, half = width / 2;
        for (int i = 0; i < x.Length; i++)
        {
            int edge = Math.Min(x.Length - 1, i + half);
            if (i == 0) for (int j = 0; j <= edge; j++) { while (tail > head && x[queue[tail - 1]] <= x[j]) tail--; queue[tail++] = j; }
            else if (i + half < x.Length) { while (tail > head && x[queue[tail - 1]] <= x[edge]) tail--; queue[tail++] = edge; }
            while (queue[head] < i - half) head++;
            result[i] = x[queue[head]];
        }
        return result;
    }

    static float[] RunningMean(float[] x, int width)
    {
        width = Math.Max(1, width);
        var result = new float[x.Length];
        double sum = 0;
        int half = width / 2, lo = 0, hi = 0;
        for (int i = 0; i < x.Length; i++)
        {
            int to = Math.Min(x.Length, i + half + 1), from = Math.Max(0, i - half);
            while (hi < to) sum += x[hi++];
            while (lo < from) sum -= x[lo++];
            result[i] = (float)(sum / (to - from));
        }
        return result;
    }

    static float Percentile(float[] x, double fraction)
    {
        var sample = new float[x.Length / 16 + 1];
        for (int i = 0; i < sample.Length; i++) sample[i] = x[Math.Min(x.Length - 1, i * 16)];
        Array.Sort(sample);
        return sample[(int)(fraction * (sample.Length - 1))];
    }
}

/// <summary>1 つの音の周期を倍音から求める。周期の単位は 1/256000 秒（公称で 90 + 4 × 値）。</summary>
public sealed class PeriodReader
{
    const double MaxFrequency = 16000;
    readonly float[] x;
    readonly int rate;
    double Top => Math.Min(MaxFrequency, 0.45 * rate);

    public PeriodReader(float[] samples, int rate) { x = samples; this.rate = rate; }

    /// <summary>与えた周期ごとの、倍音への合い方（倍音の位置の強さ − 倍音の間の強さ）を返す。</summary>
    public double[] Scores(Tone t, double[] periods)
    {
        int length = Math.Min(Math.Min(t.End - t.Start, rate / 10), Fft.MaxSize);
        var result = new double[periods.Length];
        if (length <= 0) return result;
        int size = 8192;
        while (size < 16 * length && size < Fft.MaxSize) size <<= 1;
        var re = new double[size]; var im = new double[size];
        for (int i = 0; i < length; i++) re[i] = x[t.Start + i];
        Fft.Forward(re, im);
        int bins = size / 2 + 1;
        double perBin = (double)size / rate;
        double Power(int i) { int n = Math.Min(bins - 1, i); return re[n] * re[n] + im[n] * im[n]; }
        for (int c = 0; c < periods.Length; c++)
        {
            double f0 = 256000 / periods[c], sum = 0;
            int harmonics = (int)(Top / f0);
            for (int k = 1; k <= harmonics; k++) sum += Power((int)(k * f0 * perBin + 0.5)) - Power((int)((k - 0.5) * f0 * perBin + 0.5));
            result[c] = sum;
        }
        return result;
    }

    /// <summary>候補の周期 lo〜hi の中で、倍音に最も合うものを返す（倍音の位置の強さ − 倍音の間の強さ）。</summary>
    public double Find(Tone t, double lo, double hi, double step = 0.5, double center = 0, double sigma = 0)
    {
        lo = Math.Clamp(lo, 55, 1290); hi = Math.Clamp(hi, lo + step, 1300);
        int length = Math.Min(Math.Min(t.End - t.Start, rate / 10), Fft.MaxSize);
        if (length <= 0) return lo;
        int size = 8192;
        while (size < 16 * length && size < Fft.MaxSize) size <<= 1;
        var re = new double[size]; var im = new double[size];
        for (int i = 0; i < length; i++) re[i] = x[t.Start + i];
        Fft.Forward(re, im);
        int bins = size / 2 + 1;
        var power = new double[bins];
        for (int i = 0; i < bins; i++) power[i] = re[i] * re[i] + im[i] * im[i];

        double perBin = (double)size / rate;
        int count = (int)((hi - lo) / step) + 1;
        double Score(double period)
        {
            double f0 = 256000 / period, sum = 0;
            int harmonics = (int)(Top / f0);
            for (int k = 1; k <= harmonics; k++)
            {
                sum += power[Math.Min(bins - 1, (int)(k * f0 * perBin + 0.5))];
                sum -= power[Math.Min(bins - 1, (int)((k - 0.5) * f0 * perBin + 0.5))];
            }
            return sum;
        }
        // 山の頂上を、減点の影響を受けないように求める
        double Peak(double from, int steps)
        {
            var score = new double[steps];
            for (int c = 0; c < steps; c++) score[c] = Score(from + c * step);
            int top = 0;
            for (int c = 1; c < steps; c++) if (score[c] > score[top]) top = c;
            double u = from + top * step;
            if (top > 0 && top < steps - 1)
            {
                double d = score[top - 1] - 2 * score[top] + score[top + 1];
                if (d < 0) u += step * 0.5 * (score[top - 1] - score[top + 1]) / d;
            }
            return u;
        }
        if (sigma <= 0) return Peak(lo, count);
        // 目安の周期から離れるほど減点して、どの山かを選ぶ（倍音が少ない短い音は、別の周期でも同じくらい合うことがある）
        double best = lo, bestScore = double.MinValue;
        for (int c = 0; c < count; c++)
        {
            double u = lo + c * step, raw = Score(u), z = (u - center) / sigma;
            double weighted = raw > 0 ? raw * Math.Exp(-z * z / 2) : raw;
            if (weighted > bestScore) { bestScore = weighted; best = u; }
        }
        return Peak(best - 6, (int)(12 / step) + 1);
    }
}

static class Fft
{
    const int TableSize = 1 << 18;

    /// <summary>これより大きいと回転因子の表が足りない。</summary>
    public const int MaxSize = TableSize;
    static readonly double[] Cos = new double[TableSize / 2], Sin = new double[TableSize / 2];

    static Fft()
    {
        for (int i = 0; i < TableSize / 2; i++) { Cos[i] = Math.Cos(2 * Math.PI * i / TableSize); Sin[i] = -Math.Sin(2 * Math.PI * i / TableSize); }
    }

    public static void Forward(double[] re, double[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            int half = len / 2, stride = TableSize / len;
            for (int i = 0; i < n; i += len)
                for (int j = 0, k = 0; j < half; j++, k += stride)
                {
                    double wr = Cos[k], wi = Sin[k];
                    double xr = re[i + j + half] * wr - im[i + j + half] * wi, xi = re[i + j + half] * wi + im[i + j + half] * wr;
                    re[i + j + half] = re[i + j] - xr; im[i + j + half] = im[i + j] - xi;
                    re[i + j] += xr; im[i + j] += xi;
                }
        }
    }
}
