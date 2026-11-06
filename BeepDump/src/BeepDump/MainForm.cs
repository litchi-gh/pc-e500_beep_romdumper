using System.Globalization;
using BeepDump.Core;

namespace BeepDump;

/// <summary>録音ファイルをドロップすると ROM に戻す画面。足りない範囲は撮り直して足せる。日本語と英語を切り替えられる。</summary>
sealed class MainForm : Form
{
    static readonly string LanguageFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BeepDump", "language.txt");

    readonly ComboBox language = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90 };
    readonly ComboBox model = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 400 };
    readonly ComboBox method = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 450 };
    readonly Label modelLabel = new() { AutoSize = true, Margin = new(3, 8, 3, 3) };
    readonly Label methodLabel = new() { AutoSize = true, Margin = new(3, 8, 3, 3) };
    readonly Label firstStep = Heading();
    readonly Label secondStep = Heading();
    readonly Label thirdStep = Heading();
    readonly TextBox source = new() { ReadOnly = true, Multiline = true, Height = 92, Dock = DockStyle.Top, Font = new Font("Consolas", 10f), ScrollBars = ScrollBars.Vertical };
    readonly TextBox answers = new() { ReadOnly = true, Multiline = true, Height = 62, Dock = DockStyle.Top, ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 10f) };
    readonly Panel map = new() { Dock = DockStyle.Fill, BackColor = Color.White, BorderStyle = BorderStyle.FixedSingle };
    readonly TextBox log = new() { ReadOnly = true, Multiline = true, Dock = DockStyle.Bottom, Height = 110, ScrollBars = ScrollBars.Vertical };
    readonly ProgressBar bar = new() { Dock = DockStyle.Top, Height = 16, Maximum = 1000 };
    readonly Label status = new() { Dock = DockStyle.Top, Height = 26, TextAlign = ContentAlignment.MiddleLeft, UseMnemonic = false };
    readonly Button addButton = new() { AutoSize = true };
    readonly Button saveButton = new() { AutoSize = true, Enabled = false };
    readonly Button clearButton = new() { AutoSize = true };
    RomImage image = new();
    readonly HashSet<string> added = new(StringComparer.OrdinalIgnoreCase);     // 同じ録音を 2 回数えると、一致したことになってしまう
    double knownScale;
    bool running;
    bool updatingTexts;

    const int TapeMethod = 2, SourceLines = 6, AnswerLines = 4;
    bool UsesTape => method.SelectedIndex == TapeMethod;

    RomModel Model => RomModel.All[model.SelectedIndex];

    static string T(string japanese, string english) => Messages.Pick(japanese, english);

    public MainForm(string[] files)
    {
        Width = 920; Height = 900;
        Font = new Font("Yu Gothic UI", 9.5f);
        AllowDrop = true;

        Messages.English = LoadEnglishSetting();
        language.Items.AddRange(["日本語", "English"]);
        language.SelectedIndex = Messages.English ? 1 : 0;
        language.SelectedIndexChanged += (_, _) =>
        {
            Messages.English = language.SelectedIndex == 1;
            SaveEnglishSetting();
            UpdateTexts();
        };
        foreach (var _ in RomModel.All) model.Items.Add("");
        method.Items.AddRange(["", "", ""]);
        model.SelectedIndex = 0;
        method.SelectedIndex = 1;   // おすすめの機械語版

        method.SelectedIndexChanged += (_, _) => { if (!updatingTexts) ShowSource(); };
        model.SelectedIndexChanged += (_, _) => { if (!updatingTexts) { ShowSource(); UpdateState(); } };
        clearButton.Click += (_, _) => { image = new(); added.Clear(); knownScale = 0; log.Clear(); UpdateState(); };
        addButton.Click += (_, _) => Browse();
        saveButton.Click += (_, _) => Save();
        map.Paint += (_, e) => PaintMap(e.Graphics);
        map.Resize += (_, _) => map.Invalidate();

        // ボタンの方が背が高いので、欄を縦の中ほどにそろえる
        model.Margin = new(3, 6, 3, 3);
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true };
        top.Controls.AddRange([modelLabel, model, addButton, saveButton, clearButton, methodLabel, method, language]);
        top.SetFlowBreak(clearButton, true);

        // Dock.Top は後に足したものほど上に並ぶ
        Controls.Add(map);
        Controls.Add(log);
        Controls.Add(bar);
        Controls.Add(status);
        Controls.Add(thirdStep);
        Controls.Add(answers);
        Controls.Add(secondStep);
        Controls.Add(source);
        Controls.Add(firstStep);
        Controls.Add(top);

        DragEnter += (_, e) => { if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy; };
        DragDrop += (_, e) => { if (e.Data?.GetData(DataFormats.FileDrop) is string[] dropped) _ = Process(dropped); };
        UpdateTexts();
        FitToDisplay();
        Shown += (_, _) => { if (files.Length > 0) _ = Process(files); };
    }

    /// <summary>寸法は 96 dpi のときの値。文字は表示倍率に合わせて大きくなるので、欄も同じだけ広げる。画面からははみ出さないようにする。</summary>
    void FitToDisplay()
    {
        float factor = DeviceDpi / 96f;
        if (factor != 1) Scale(new SizeF(factor, factor));
        // 行の途中で切れないように、欄の高さは実際の行の高さから決める
        int line = source.GetPositionFromCharIndex(source.GetFirstCharIndexFromLine(1)).Y - source.GetPositionFromCharIndex(0).Y;
        int border = (int)(4 * factor);
        source.Height = line * SourceLines + border;
        answers.Height = line * AnswerLines + border;
        var area = Screen.PrimaryScreen?.WorkingArea ?? Rectangle.Empty;
        if (!area.IsEmpty) Size = new(Math.Min(Width, area.Width), Math.Min(Height, area.Height));
    }

    static Label Heading() => new() { UseMnemonic = false, Dock = DockStyle.Top, Height = 26, TextAlign = ContentAlignment.BottomLeft, AutoEllipsis = true };

    static bool LoadEnglishSetting()
    {
        try { if (File.Exists(LanguageFile)) return File.ReadAllText(LanguageFile).Trim() == "en"; }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName != "ja";
    }

    static void SaveEnglishSetting()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LanguageFile)!);
            File.WriteAllText(LanguageFile, Messages.English ? "en" : "ja");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>画面の文を今の言語にする。</summary>
    void UpdateTexts()
    {
        updatingTexts = true;
        Text = T("BeepDump - BEEP の音から ROM を復元", "BeepDump - rebuild a ROM from BEEP tones");
        modelLabel.Text = T("機種:", "Model:");
        methodLabel.Text = T("打ち込み:", "Program:");
        addButton.Text = T("録音を追加...", "Add recording...");
        saveButton.Text = T("ROM を保存...", "Save ROM...");
        clearButton.Text = T("最初からやり直す", "Start over");
        for (int i = 0; i < RomModel.All.Length; i++) model.Items[i] = RomModel.All[i].Label;
        method.Items[0] = T("BASIC 版（打ち込み少、本体 256KB 約 2 時間）", "BASIC (short to type, 256KB in about 2 hours)");
        method.Items[1] = T("機械語版（おすすめ。本体 256KB 約 15 分）", "Machine code (recommended, 256KB in about 15 min)");
        method.Items[2] = T("CSAVE M（打ち込みは 1 行。本体 256KB 約 22 分、2 回録音で確認）", "CSAVE M (one line to type, 256KB in about 22 min, record twice)");
        updatingTexts = false;
        ShowSource();
        UpdateState();
    }

    void ShowSource()
    {
        bool fast = method.SelectedIndex == 1;
        thirdStep.Text = UsesTape
            ? T("3. 読み取り結果（緑=2 回の録音が一致  黄=読めたが 1 回だけで未確認  赤=読めなかった  灰=未録音）",
                "3. Result (green = two recordings agree, yellow = read once, not confirmed, red = not read, gray = not recorded)")
            : T("3. 読み取り結果（緑=読めた  黄=修復して読めた  赤=読めなかった  灰=未録音）",
                "3. Result (green = read, yellow = repaired, red = not read, gray = not recorded)");
        secondStep.Text = UsesTape
            ? T("2. 次の命令を打ちます。ENTER の前に録音を始め、音が止まって > が出るまで待ちます（本体 256KB で約 22 分）。", "2. Type the command below. Start recording before pressing ENTER, and wait for the sound to stop and > to appear (about 22 min for 256KB).")
            : T("2. RUN すると ? が 2 回出ます。次の開始番地・終了番地を順に答えます（&H も打ちます）。ENTER の前に録音を始めます。",
                "2. RUN asks ? twice. Answer the start and end addresses below (type the &H too). Start recording before pressing ENTER.");
        UpdateAnswers();
        if (UsesTape)
        {
            source.Text = Model.TapeListing;
            firstStep.Text = T("1. プログラムは要りません。BASIC の RUN モードにします。読み違いに気づけるように、同じ範囲を 2 回録音します。",
                "1. No program is needed. Go to BASIC RUN mode. Record each range twice, so that misreads can be found.");
            return;
        }
        source.Text = fast ? FastCode.MachineListing : FastCode.BasicListing;
        firstStep.Text = fast
            ? T("1. 1 行目（行番号なし）を最初に 1 回だけ実行すると、メニューに戻ります。BASIC キーで PRO モードに入り、残りの行を打ち込みます。",
                "1. Run the first line (no line number) once; it returns to the menu. Press BASIC to reach PRO mode and type the other lines.")
            : T("1. ポケコンに打ち込みます（PRO モード）。録音中はキーに触らないでください。",
                "1. Type this in (PRO mode). Do not touch the keys while recording.");
    }

    void Browse()
    {
        using var dialog = new OpenFileDialog { Multiselect = true, Filter = T("録音|*.mov;*.mp4;*.m4a;*.wav;*.aac;*.mp3|すべて|*.*", "Recordings|*.mov;*.mp4;*.m4a;*.wav;*.aac;*.mp3|All files|*.*") };
        if (dialog.ShowDialog(this) == DialogResult.OK) _ = Process(dialog.FileNames);
    }

    async Task Process(string[] paths)
    {
        if (running) { Log(T("読み取りが終わってから追加してください。", "Add it after the current reading finishes.")); return; }
        running = true;
        addButton.Enabled = saveButton.Enabled = clearButton.Enabled = language.Enabled = false;
        try
        {
        foreach (var path in paths)
        {
            string name = Path.GetFileName(path);
            try
            {
                // 同じ録音を 2 回数えると、一致したことになってしまう。内容で比べるので、コピーも同じものと分かり、同じ名前の録り直しは別の録音になる
                status.Text = T($"{name}: 確認中", $"{name}: checking");
                string key = await Task.Run(() => AudioFile.Fingerprint(path));
                if (added.Contains(key)) { Log(T($"{name}: 同じ内容のファイルをすでに読み込んであります。", $"{name}: a file with the same content was already added.")); continue; }
                IProgress<(string Text, double Value)> report = new Progress<(string Text, double Value)>(p =>
                {
                    status.Text = $"{name}: {p.Text}";
                    bar.Value = (int)(Math.Clamp(p.Value, 0, 1) * 1000);
                });
                string loading = T("音声を読み込み中", "Loading audio");
                var result = await Task.Run(() =>
                {
                    var audio = AudioFile.Load(path, v => report.Report((loading, v)));
                    return Analyzer.Analyze(audio, (text, v) => report.Report((text, v)), null, knownScale);
                });
                if (result.Tape && !UsesTape) method.SelectedIndex = TapeMethod;   // CSAVE M の録音なら、足りない所も CSAVE M で録音できるように
                if (result.Blocks.Any(b => b.State != BlockState.Failed)) added.Add(key);     // 読めたものだけ覚える。読めなかったファイルはもう一度試せる
                image.Add(result.Blocks);
                if (result.Scale > 0 && result.Blocks.Any(b => b.State == BlockState.Good)) knownScale = result.Scale;
                int repaired = result.Blocks.Count(b => b.State == BlockState.Repaired), failed = result.Blocks.Count(b => b.State == BlockState.Failed), good = result.Blocks.Count - repaired - failed;
                Log(result.Tape
                    ? T($"{name}: {repaired} ブロック読めた（未確認）、失敗 {failed}", $"{name}: {repaired} blocks read (not confirmed), {failed} failed")
                    : T($"{name}: {good} ブロック読めた、修復 {repaired}、失敗 {failed}", $"{name}: {good} blocks read, {repaired} repaired, {failed} failed"));
                foreach (var note in result.Notes) Log("  " + note);
            }
            // 並列処理の例外は包まれて届く。1 本で失敗しても、読めたブロックを失わないように続ける
            catch (Exception e) { Log(T($"{name}: 読めませんでした: {e.GetBaseException().Message}", $"{name}: could not read it: {e.GetBaseException().Message}")); }
            UpdateState();
        }
        int outside = image.Outside(Model);
        if (outside > 0) Log(T($"注意: {outside} ブロックは、選んだ機種の ROM の範囲外です。拡張 ROM を読むときは、機種を PC-E650 / PC-U6000 にしてください。", $"Note: {outside} blocks are outside the selected model's ROM. To read an extension ROM, choose PC-E650 / PC-U6000."));
        }
        finally
        {
            running = false;
            addButton.Enabled = clearButton.Enabled = language.Enabled = true;
            bar.Value = 0;
            UpdateState();
        }
    }

    void Log(string text) => log.AppendText(text + Environment.NewLine);

    /// <summary>未録音の範囲と保存ボタンを、集めたブロックに合わせて更新する。</summary>
    void UpdateState()
    {
        var missing = image.Missing(Model);
        UpdateAnswers();
        saveButton.Enabled = !running && image.Blocks.Any(b => Model.Ranges.Any(r => b.Address >= r.Start && b.Address <= r.End));
        if (!running)
        {
            int have = Model.Ranges.Sum(r => image.Blocks.Count(b => b.Address >= r.Start && b.Address <= r.End));
            int total = Model.Size / Decoder.DataBytes;
            int yellow = Model.Ranges.Sum(r => image.Blocks.Count(b => b.State == BlockState.Repaired && b.Address >= r.Start && b.Address <= r.End));
            status.Text = !image.Blocks.Any()
                ? T("録音ファイル（MOV / MP4 / M4A / WAV）をここへドロップしてください。何本でも追加でき、読めなかった所を補い合います。",
                    "Drop recordings (MOV / MP4 / M4A / WAV) here. You can add several; they fill each other's gaps.")
                : missing.Count == 0
                    ? T($"完了: 全 {total} ブロックを読みました（黄 {yellow}）。「ROM を保存」で保存できます。", $"Done: all {total} blocks were read ({yellow} yellow). Use Save ROM to save it.")
                    : UsesTape
                        ? T($"{have} / {total} ブロックを読みました。残りを録音してドロップすると、そこだけ埋まります（命令は上の 2 に出ています）。",
                            $"{have} of {total} blocks were read. Record the rest and drop it to fill the gaps (the commands are in step 2).")
                        : T($"{have} / {total} ブロックを読みました。残りを録音してドロップすると、そこだけ埋まります（番地は上の 2 に出ています）。",
                            $"{have} of {total} blocks were read. Record the rest and drop it to fill the gaps (addresses are in step 2).");
        }
        foreach (int a in image.Conflicts)
            Log(T($"注意: {a:X5} のブロックは録音によって内容が違いました。もう 1 回録音すると、同じ内容がそろった方を採ります。",
                  $"Note: block {a:X5} differed between recordings. Record it once more; the content that agrees is used."));
        image.Conflicts.Clear();
        map.Invalidate();
    }

    /// <summary>まだ録音が要る範囲を、選んだ方式の書き方で出す。</summary>
    void UpdateAnswers()
    {
        var missing = image.Missing(Model);
        var unsure = image.Unsure(Model);
        string Lines(List<(int Start, int End)> ranges) => string.Join(Environment.NewLine, RomImage.ForRetake(ranges, Model).Select(m => UsesTape ? RomModel.TapeCommand(m.Start, m.End) : $"&H{m.Start:X5}   &H{m.End:X5}"));
        answers.Text = missing.Count > 0
            ? Lines(missing)
            : unsure.Count == 0
                ? T("すべて読めました。", "Every block was read.")
                : T("すべて読めました。黄色のマスを確かめるなら、次の範囲をもう 1 回録音します:", "Every block was read. To confirm the yellow cells, record these ranges once more:") + Environment.NewLine + Lines(unsure);
    }

    void Save()
    {
        if (image.Missing(Model).Count > 0 && MessageBox.Show(this, T("まだ読めていない範囲があります。その部分を FFh で埋めて保存しますか？", "Some ranges are not read yet. Save with FFh in those ranges?"), Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        using var dialog = new SaveFileDialog { Filter = T("ROM|*.rom|すべて|*.*", "ROM|*.rom|All files|*.*"), FileName = Model == RomModel.Main384 ? "PC-E650.rom" : "PC-E500.rom" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            image.Save(dialog.FileName, Model);
            Log(T($"保存しました: {dialog.FileName}", $"Saved: {dialog.FileName}"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // 集めたブロックを失わないように、エラーを出して続ける
            MessageBox.Show(this, T($"保存できませんでした: {e.Message}", $"Could not save: {e.Message}"), Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>ブロックを 1 つずつマス目で描く。マス目は欄に収まる最大の大きさにする。</summary>
    void PaintMap(Graphics g)
    {
        g.Clear(map.BackColor);
        float factor = DeviceDpi / 96f;
        int Margin = (int)(4 * factor), Spacing = (int)(6 * factor), LabelHeight = Font.Height + (int)(2 * factor);
        int width = map.ClientSize.Width - 2 * Margin, height = map.ClientSize.Height - 2 * Margin;
        int Blocks((int Start, int End) range) => (range.End - range.Start + 1) / Decoder.DataBytes;
        int cell = 2;
        for (int size = 3; size <= 40; size++)
        {
            int columns = Math.Max(1, width / size);
            int needed = Model.Ranges.Sum(r => LabelHeight + ((Blocks(r) - 1) / columns + 1) * size + Spacing);
            if (needed <= height) cell = size;
        }
        int perRow = Math.Max(1, width / cell), y = Margin;
        foreach (var range in Model.Ranges)
        {
            g.DrawString($"{range.Start:X5}-{range.End:X5}", Font, Brushes.Black, Margin, y);
            y += LabelHeight;
            for (int i = 0; i < Blocks(range); i++)
            {
                Brush brush = image.StateOf(range.Start + i * Decoder.DataBytes) switch
                {
                    BlockState.Good => Brushes.MediumSeaGreen,
                    BlockState.Repaired => Brushes.Gold,
                    BlockState.Failed => Brushes.IndianRed,
                    _ => Brushes.Gainsboro
                };
                g.FillRectangle(brush, Margin + i % perRow * cell, y + i / perRow * cell, cell - 1, cell - 1);
            }
            y += ((Blocks(range) - 1) / perRow + 1) * cell + Spacing;
        }
    }
}
