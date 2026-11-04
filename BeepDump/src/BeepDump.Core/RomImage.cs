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
/// 検査の通ったブロックが録音によって食い違ったら、どちらも信用せず未確定にし、同じ内容が 2 回そろったものを採る。
/// </summary>
public sealed class RomImage
{
    const int BlockSize = Decoder.DataBytes;
    readonly Dictionary<int, Block> blocks = [];
    readonly Dictionary<int, List<Block>> disputed = [];
    readonly HashSet<int> failed = [];

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
                var agreed = versions.FirstOrDefault(v => v.Data.SequenceEqual(block.Data));
                if (agreed == null) { versions.Add(block); continue; }
                blocks[block.Address] = agreed.State == BlockState.Good ? agreed : block;
                disputed.Remove(block.Address);
                continue;
            }
            if (!blocks.TryGetValue(block.Address, out var old)) { blocks[block.Address] = block; continue; }
            if (old.Data.SequenceEqual(block.Data)) { if (block.State == BlockState.Good) blocks[block.Address] = block; continue; }
            // 修復したものより、そのまま検査の通ったものを信用する
            if (old.State == BlockState.Repaired && block.State == BlockState.Good) { blocks[block.Address] = block; continue; }
            if (old.State == BlockState.Good && block.State == BlockState.Repaired) continue;
            blocks.Remove(block.Address);
            disputed[block.Address] = [old, block];
            Conflicts.Add(block.Address);
        }
    }

    /// <summary>まだ読めていない範囲（撮り直しで INPUT に答える開始・終了番地）。</summary>
    public List<(int Start, int End)> Missing(RomModel model)
    {
        var result = new List<(int, int)>();
        foreach (var (start, end) in model.Ranges)
        {
            int from = -1;
            for (int a = start; a <= end; a += BlockSize)
            {
                if (!blocks.ContainsKey(a)) { if (from < 0) from = a; }
                else if (from >= 0) { result.Add((from, a - 1)); from = -1; }
            }
            if (from >= 0) result.Add((from, end));
        }
        return result;
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
