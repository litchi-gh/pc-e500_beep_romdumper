using System.Runtime.InteropServices;

namespace BeepDump.Core;

/// <summary>
/// 録音を読む。WAV は自前で読む。ほかの形式（MOV / MP4 / M4A など）は、Windows では Media Foundation、
/// それ以外の OS では ffmpeg で読む。
/// </summary>
public static class AudioFile
{
    public static PcmAudio Load(string path, Action<double>? progress = null)
    {
        if (!File.Exists(path)) throw new IOException(Messages.Pick($"ファイルがありません: {path}", $"File not found: {path}"));
        if (Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase))
        {
            using var file = File.OpenRead(path);
            if (WavReader.Read(file) is { } wav) return wav;
        }
        if (!OperatingSystem.IsWindows()) return Ffmpeg(path);
        try { return MediaFoundation.Load(path, progress); }
        catch (IOException) when (FfmpegAvailable()) { return Ffmpeg(path); }
        catch (DllNotFoundException)
        {
            // Windows の N エディションには Media Foundation がない
            if (FfmpegAvailable()) return Ffmpeg(path);
            throw new IOException(Messages.Pick("この Windows には Media Foundation がありません。Media Feature Pack か ffmpeg を入れるか、WAV に変換してください。",
                "This Windows has no Media Foundation. Install the Media Feature Pack or ffmpeg, or convert the recording to WAV."));
        }
    }

    static bool FfmpegAvailable()
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ffmpeg", "-version") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
            process?.WaitForExit();
            return process?.ExitCode == 0;
        }
        catch (Exception) { return false; }
    }

    /// <summary>ffmpeg に WAV へ変換させて、パイプから読む。</summary>
    static PcmAudio Ffmpeg(string path)
    {
        if (!FfmpegAvailable())
            throw new IOException(Messages.Pick($"{Path.GetFileName(path)} を読むには ffmpeg が必要です。ffmpeg を入れるか、WAV に変換してください。",
                $"Reading {Path.GetFileName(path)} needs ffmpeg. Install ffmpeg, or convert the recording to WAV."));
        var info = new System.Diagnostics.ProcessStartInfo("ffmpeg") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string argument in new[] { "-v", "error", "-i", path, "-vn", "-map", "0:a:0", "-c:a", "pcm_s16le", "-f", "wav", "-" }) info.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(info) ?? throw new IOException("ffmpeg");
        var errors = process.StandardError.ReadToEndAsync();
        var audio = WavReader.Read(process.StandardOutput.BaseStream);
        process.WaitForExit();
        if (audio == null || audio.Length == 0)
            throw new IOException(Messages.Pick($"音声を読めませんでした: {errors.Result.Trim()}", $"Could not read the audio: {errors.Result.Trim()}"));
        return audio;
    }
}

/// <summary>Windows Media Foundation で音声を読む（MOV / MP4 / M4A / WAV など）。</summary>
static unsafe class MediaFoundation
{
    const uint AudioStream = 0xFFFFFFFD, AllStreams = 0xFFFFFFFE, MediaSource = 0xFFFFFFFF;
    const uint EndOfStream = 2, ReaderError = 1;
    static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f"), SubType = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    static readonly Guid Audio = new("73647561-0000-0010-8000-00aa00389b71"), Pcm = new("00000001-0000-0010-8000-00aa00389b71");
    static readonly Guid Bits = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669"), Channels = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
    static readonly Guid Rate = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba"), Duration = new("6c990d33-bb8e-477a-8598-0d5d96fcd88a");

    [DllImport("ole32.dll")] static extern int CoInitializeEx(nint reserved, uint mode);
    [DllImport("mfplat.dll")] static extern int MFStartup(uint version, uint flags);
    [DllImport("mfplat.dll")] static extern int MFCreateMediaType(out nint type);
    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)] static extern int MFCreateSourceReaderFromURL(string url, nint attributes, out nint reader);

    static nint Slot(nint obj, int slot) => (*(nint**)obj)[slot];
    static void Release(nint obj) { if (obj != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(obj, 2))(obj); }
    static void Check(int hr, string what) { if (hr < 0) throw new IOException(Messages.Pick($"音声を読めませんでした（{what}、HRESULT {hr:X8}）", $"Could not read the audio ({what}, HRESULT {hr:X8})")); }

    /// <param name="progress">読み終えた割合（0〜1）</param>
    public static PcmAudio Load(string path, Action<double>? progress = null)
    {
        CoInitializeEx(0, 0);
        Check(MFStartup(0x00020070, 0), "starting Media Foundation");
        nint reader = 0, type = 0, current = 0;
        try
        {
            Check(MFCreateSourceReaderFromURL(Path.GetFullPath(path), 0, out reader), "opening the file");
            var setSelection = (delegate* unmanaged[Stdcall]<nint, uint, int, int>)Slot(reader, 4);
            Check(setSelection(reader, AllStreams, 0), "selecting audio");
            Check(setSelection(reader, AudioStream, 1), "selecting audio; the file may have none");
            Check(MFCreateMediaType(out type), "preparing the format");
            Guid major = MajorType, sub = SubType, audio = Audio, pcm = Pcm, bits = Bits, channels = Channels, rate = Rate;
            var setGuid = (delegate* unmanaged[Stdcall]<nint, Guid*, Guid*, int>)Slot(type, 24);
            Check(setGuid(type, &major, &audio), "preparing the format");
            Check(setGuid(type, &sub, &pcm), "preparing the format");
            Check(((delegate* unmanaged[Stdcall]<nint, Guid*, uint, int>)Slot(type, 21))(type, &bits, 16), "preparing the format");
            Check(((delegate* unmanaged[Stdcall]<nint, uint, nint, nint, int>)Slot(reader, 7))(reader, AudioStream, 0, type), "converting audio");
            Check(((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(reader, 6))(reader, AudioStream, &current), "getting the format");
            var getU32 = (delegate* unmanaged[Stdcall]<nint, Guid*, uint*, int>)Slot(current, 7);
            uint channelCount, sampleRate;
            Check(getU32(current, &channels, &channelCount), "getting the channels");
            Check(getU32(current, &rate, &sampleRate), "getting the sample rate");

            double seconds = 0;
            long* variant = stackalloc long[3];
            variant[0] = variant[1] = variant[2] = 0;
            Guid duration = Duration;
            if (((delegate* unmanaged[Stdcall]<nint, uint, Guid*, long*, int>)Slot(reader, 12))(reader, MediaSource, &duration, variant) >= 0)
                seconds = variant[1] / 1e7;

            int frameCapacity = (int)Math.Min(Array.MaxLength, Math.Max(1 << 20, (long)((seconds + 2) * sampleRate)));
            var channelData = new short[channelCount][];
            for (int c = 0; c < channelCount; c++) channelData[c] = new short[frameCapacity];
            int count = 0;
            var read = (delegate* unmanaged[Stdcall]<nint, uint, uint, uint*, uint*, long*, nint*, int>)Slot(reader, 9);
            while (true)
            {
                uint index, flags; long time; nint sample = 0;
                Check(read(reader, AudioStream, 0, &index, &flags, &time, &sample), "reading audio");
                if (flags != 0 && (flags & ReaderError) != 0) throw new IOException(Messages.Pick("音声を読めませんでした", "Could not read the audio"));
                if (sample != 0)
                {
                    nint buffer = 0;
                    Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(sample, 41))(sample, &buffer), "getting a buffer");
                    byte* data; uint capacity, length;
                    Check(((delegate* unmanaged[Stdcall]<nint, byte**, uint*, uint*, int>)Slot(buffer, 3))(buffer, &data, &capacity, &length), "getting a buffer");
                    var pcmData = (short*)data;
                    int frames = (int)(length / (2 * channelCount));
                    if ((long)count + frames > channelData[0].Length)
                        for (int c = 0; c < channelCount; c++) Array.Resize(ref channelData[c], (int)Math.Min(Array.MaxLength, channelData[c].Length * 2L));
                    for (int i = 0; i < frames; i++)
                    {
                        for (int c = 0; c < channelCount; c++) channelData[c][count] = pcmData[i * (int)channelCount + c];
                        count++;
                    }
                    ((delegate* unmanaged[Stdcall]<nint, int>)Slot(buffer, 4))(buffer);
                    Release(buffer); Release(sample);
                    if (seconds > 0) progress?.Invoke(Math.Min(1, (double)count / sampleRate / seconds));
                }
                if ((flags & EndOfStream) != 0) break;
            }
            for (int c = 0; c < channelCount; c++) Array.Resize(ref channelData[c], count);
            return new(channelData, (int)sampleRate);
        }
        finally { Release(current); Release(type); Release(reader); }
    }
}

/// <summary>4 次バターワースの高域通過（800 Hz）。低いブーンという音と本体を叩く音を落とす。</summary>
public sealed class HighPass
{
    readonly double[,] c = new double[2, 5];
    readonly double[,] z = new double[2, 2];

    public HighPass(double rate, double cutoff = 800)
    {
        double k = Math.Tan(Math.PI * cutoff / rate);
        double[] q = [0.54119610, 1.30656296];
        for (int s = 0; s < 2; s++)
        {
            double n = 1 / (1 + k / q[s] + k * k);
            c[s, 0] = n; c[s, 1] = -2 * n; c[s, 2] = n;
            c[s, 3] = 2 * (k * k - 1) * n; c[s, 4] = (1 - k / q[s] + k * k) * n;
        }
    }

    public float Next(float input)
    {
        double x = input;
        for (int s = 0; s < 2; s++)
        {
            double y = c[s, 0] * x + z[s, 0];
            z[s, 0] = c[s, 1] * x - c[s, 3] * y + z[s, 1];
            z[s, 1] = c[s, 2] * x - c[s, 4] * y;
            x = y;
        }
        return (float)x;
    }
}
