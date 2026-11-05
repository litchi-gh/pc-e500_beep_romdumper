namespace BeepDump.Core;

public sealed record RomModel(string Name, (int Start, int End)[] Ranges)
{
    public string? EnglishName { get; init; }
    public string Label => Messages.English && EnglishName != null ? EnglishName : Name;
    public static readonly RomModel Main256 = new("PC-E500 / E500PJ / E550 / 1480U / 1490UII（本体 256KB）", [(0xC0000, 0xFFFFF)]) { EnglishName = "PC-E500 / E500PJ / E550 / 1480U / 1490UII (main 256KB)" };
    public static readonly RomModel Main384 = new("PC-E650 / PC-U6000（拡張 128KB + 本体 256KB）", [(0x20000, 0x3FFFF), (0xC0000, 0xFFFFF)]) { EnglishName = "PC-E650 / PC-U6000 (extension 128KB + main 256KB)" };
    public static readonly RomModel[] All = [Main256, Main384];
    public int Size => Ranges.Sum(r => r.End - r.Start + 1);
}

/// <summary>
/// 複数の録音から読んだブロックを集めて ROM にする。後から足した録音で、読めなかった所を埋められる。
/// 検査の通ったブロックが録音によって食い違ったら、どちらも信用せず未確定にする。新しい録音の内容が 2 回以上そろい、ほかのどの内容より多くなったら採る。
/// </summary>
public sealed class RomImage
{
    const int BlockSize = Decoder.DataBytes;
    const int RetakeLeadIn = 2, RetakeBlocks = 8;
    readonly Dictionary<int, Block> blocks = [];
    readonly Dictionary<int, List<Block>> disputed = [];
    readonly HashSet<int> failed = [];
    readonly Dictionary<int, int> reads = [];   // 今の内容がそろった回数

    /// <summary>新しく食い違いが見つかった番地（読んだら空にしてよい）。</summary>
    public List<int> Conflicts { get; } = [];

    public IEnumerable<Block> Blocks => blocks.Values;
    public BlockState? StateOf(int address) => blocks.TryGetValue(address, out var b) ? b.State : failed.Contains(address) || disputed.ContainsKey(address) ? BlockState.Failed : null;

    public void Add(IEnumerable<Block> found)
    {
        foreach (var block in found)
        {
            if (block.State == BlockState.Failed) { failed.Add(block.Address); continue; }
            if (disputed.TryGetValue(block.Address, out var versions))
            {
                versions.Add(block);
                int count = versions.Count(v => v.Data.SequenceEqual(block.Data));
                // 確認済みの内容より少ない回数の内容で上書きしないように、これまでの回数と比べる
                if (count < 2 || versions.Where(v => !v.Data.SequenceEqual(block.Data)).GroupBy(v => Convert.ToHexString(v.Data)).Any(g => g.Count() >= count)) { Conflicts.Add(block.Address); continue; }
                blocks[block.Address] = block with { State = BlockState.Good };   // 別の録音で同じ内容がそろったので確か
                reads[block.Address] = count;
                disputed.Remove(block.Address);
                continue;
            }
            if (!blocks.TryGetValue(block.Address, out var old)) { blocks[block.Address] = block; reads[block.Address] = 1; continue; }
            if (old.Data.SequenceEqual(block.Data))
            {
                // 別の録音で同じ内容になったので、どちらかが修復したものでも確かと言える
                blocks[block.Address] = block with { State = BlockState.Good };
                reads[block.Address]++;
                continue;
            }
            disputed[block.Address] = [.. Enumerable.Repeat(old, reads[block.Address]), block];
            blocks.Remove(block.Address);
            reads.Remove(block.Address);
            Conflicts.Add(block.Address);
        }
    }

    /// <summary>まだ読めていない範囲（撮り直しで INPUT に答える開始・終了番地）。</summary>
    public List<(int Start, int End)> Missing(RomModel model) => Runs(model, a => !blocks.ContainsKey(a));

    /// <summary>修復した、または確かめられなかったブロックの範囲。もう 1 回録音して同じ内容になれば確かになる。</summary>
    public List<(int Start, int End)> Unsure(RomModel model) => Runs(model, a => blocks.TryGetValue(a, out var b) && b.State == BlockState.Repaired);

    /// <summary>
    /// 撮り直しで答える範囲にする。BASIC 版の短い実行は番地を確かめられず、エミュレータの音では実行の最初のブロックを読めないことも多かったので、
    /// 2 ブロック前から、合わせて 8 ブロック以上にする。機械語版で少し長くなるのは 8 秒ほどで、困らない。
    /// </summary>
    public static List<(int Start, int End)> ForRetake(List<(int Start, int End)> ranges, RomModel model) => ranges.Select(r =>
    {
        var range = model.Ranges.First(m => r.Start >= m.Start && r.End <= m.End);
        int start = Math.Max(range.Start, r.Start - RetakeLeadIn * BlockSize);
        int end = Math.Min(range.End, Math.Max(r.End, start + RetakeBlocks * BlockSize - 1));
        return (Math.Max(range.Start, Math.Min(start, end + 1 - RetakeBlocks * BlockSize)), end);
    }).ToList();

    /// <summary>機種の範囲の外で読めたブロックの数。</summary>
    public int Outside(RomModel model) => blocks.Keys.Count(a => !model.Ranges.Any(r => a >= r.Start && a <= r.End));

    List<(int Start, int End)> Runs(RomModel model, Func<int, bool> wanted)
    {
        var result = new List<(int, int)>();
        foreach (var (start, end) in model.Ranges)
        {
            int from = -1;
            for (int a = start; a <= end; a += BlockSize)
            {
                if (wanted(a)) { if (from < 0) from = a; }
                else if (from >= 0) { result.Add((from, a - 1)); from = -1; }
            }
            if (from >= 0) result.Add((from, end));
        }
        return result;
    }

    /// <summary>ファイルに書く。途中で失敗しても、前のファイルは壊さない。</summary>
    public void Save(string path, RomModel model)
    {
        string temp = path + ".tmp";
        try
        {
            File.WriteAllBytes(temp, Build(model));
            File.Move(temp, path, overwrite: true);
        }
        finally { File.Delete(temp); }
    }

    /// <summary>エミュレータが読む形（拡張 ROM の後ろに本体 ROM）にする。読めていない所は FFh。</summary>
    public byte[] Build(RomModel model)
    {
        var image = new byte[model.Size];
        Array.Fill(image, (byte)0xFF);
        int offset = 0;
        foreach (var (start, end) in model.Ranges)
        {
            for (int a = start; a <= end; a += BlockSize)
                if (blocks.TryGetValue(a, out var block)) block.Data.CopyTo(image, offset + a - start);
            offset += end - start + 1;
        }
        return image;
    }
}
