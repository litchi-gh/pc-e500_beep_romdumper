using System.Buffers.Binary;

namespace BeepDump.Core;

/// <summary>WAV を読む（8/16/24/32 bit の PCM と 32 bit 浮動小数点）。どの OS でも動く。パイプからも読めるよう、前から順に読むだけにする。</summary>
public static class WavReader
{
    const int FormatPcm = 1, FormatFloat = 3, FormatExtensible = 0xFFFE;

    /// <summary>対応していない形式なら null を返す。</summary>
    public static PcmAudio? Read(Stream stream)
    {
        var header = new byte[12];
        if (!ReadAll(stream, header) || header[0] != 'R' || header[1] != 'I' || header[2] != 'F' || header[3] != 'F' || header[8] != 'W') return null;
        int format = 0, channels = 0, rate = 0, bits = 0;
        var chunk = new byte[8];
        while (ReadAll(stream, chunk))
        {
            string id = System.Text.Encoding.ASCII.GetString(chunk, 0, 4);
            long size = BinaryPrimitives.ReadUInt32LittleEndian(chunk.AsSpan(4));
            if (id == "fmt ")
            {
                var body = new byte[size + (size & 1)];
                if (size < 16 || !ReadAll(stream, body)) return null;
                format = BinaryPrimitives.ReadUInt16LittleEndian(body);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(2));
                rate = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(4));
                bits = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(14));
                if (format == FormatExtensible && size >= 26) format = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(24));
                continue;
            }
            if (id == "data")
            {
                if (channels < 1 || rate < 1) return null;
                return ReadSamples(stream, format, channels, rate, bits);
            }
            Skip(stream, size + (size & 1));
        }
        return null;
    }

    static PcmAudio? ReadSamples(Stream stream, int format, int channels, int rate, int bits)
    {
        if (!(format == FormatPcm && bits is 8 or 16 or 24 or 32) && !(format == FormatFloat && bits == 32)) return null;
        int width = bits / 8, frameSize = width * channels;
        var data = new short[channels][];
        for (int c = 0; c < channels; c++) data[c] = new short[1 << 20];
        var buffer = new byte[frameSize * 65536];
        int count = 0, held = 0, got;
        while ((got = stream.Read(buffer, held, buffer.Length - held)) > 0)
        {
            int total = held + got, frames = total / frameSize;
            if ((long)count + frames > data[0].Length)
            {
                int capacity = (int)Math.Min(Array.MaxLength, Math.Max(data[0].Length * 2L, (long)count + frames));
                for (int c = 0; c < channels; c++) Array.Resize(ref data[c], capacity);
            }
            for (int f = 0; f < frames; f++)
                for (int c = 0; c < channels; c++)
                    data[c][count + f] = Sample(buffer.AsSpan((f * channels + c) * width, width), format, bits);
            count += frames;
            held = total - frames * frameSize;
            if (held > 0) Array.Copy(buffer, frames * frameSize, buffer, 0, held);
        }
        for (int c = 0; c < channels; c++) Array.Resize(ref data[c], count);
        return new(data, rate);
    }

    static short Sample(ReadOnlySpan<byte> b, int format, int bits)
    {
        if (format == FormatFloat) return (short)Math.Clamp(BinaryPrimitives.ReadSingleLittleEndian(b) * 32767f, -32768f, 32767f);
        return bits switch
        {
            8 => (short)((b[0] - 128) << 8),
            16 => BinaryPrimitives.ReadInt16LittleEndian(b),
            _ => BinaryPrimitives.ReadInt16LittleEndian(b[(bits / 8 - 2)..]),   // 上位 16 bit
        };
    }

    static bool ReadAll(Stream stream, byte[] buffer)
    {
        int done = 0;
        while (done < buffer.Length)
        {
            int got = stream.Read(buffer, done, buffer.Length - done);
            if (got <= 0) return false;
            done += got;
        }
        return true;
    }

    static void Skip(Stream stream, long count)
    {
        var buffer = new byte[65536];
        while (count > 0)
        {
            int got = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
            if (got <= 0) return;
            count -= got;
        }
    }
}
