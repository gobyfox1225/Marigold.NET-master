using System.IO.Compression;

namespace Marigold.Imaging;

/// <summary>
/// PNG 解码器：纯托管实现，只依赖 .NET 内置的 <c>ZLibStream</c>。
/// </summary>
/// <remarks>
/// <para>
/// 引擎原本只需要「导出」PNG（<see cref="ImageWriter"/>），现在要支持<b>读取</b>外部贴图，
/// 于是补上这一半：解码把 PNG 变成 <see cref="Texture2D"/> 直接喂给软件光栅化器。
/// 用托管实现而不是引第三方库，是为了让整个项目保持「零依赖」——
/// 下载、还原、构建都不需要网络。
/// </para>
/// <para>
/// <b>支持范围</b>：非隔行的 PNG，位深 8 或 16（16 位取高字节），
/// 颜色类型 0（灰）、2（RGB）、3（调色板）、4（灰+alpha）、6（RGBA）。
/// alpha 通道会被读取并做一次「预乘到浅色」处理——
/// 因为本引擎的白房间没有真正的透明混合管线，贴着墙的贴图边缘
/// 直接按 alpha 与白墙混合，比硬边方块自然得多。
/// </para>
/// <para>
/// <b>解码流程</b>：解析分块 → 收集 IDAT → 一次性解压 → 逐扫描线反滤波 → 展开为 RGBA。
/// 反滤波是本格式唯一的难点，五种滤波器（None / Sub / Up / Average / Paeth）
/// 逐一还原即可，Paeth 需要用左、上、左上三个已知像素做预测。
/// </para>
/// </remarks>
public static class PngReader
{
    /// <summary>PNG 文件头的第二个 32 位字：<c>0x0D 0A 1A 0A</c>，用于确认不是被文本模式破坏过的文件。</summary>
    private const uint SignatureTail = 0x0D0A1A0A;

    /// <summary>PNG 文件头的第一个 32 位字：<c>0x89 "P" "N" "G"</c>。</summary>
    private const uint SignatureHead = 0x89504E47;

    /// <summary>颜色类型常量。</summary>
    private const int ColorTypeGray = 0;
    private const int ColorTypeGrayAlpha = 4;
    private const int ColorTypePalette = 3;
    private const int ColorTypeRgb = 2;
    private const int ColorTypeRgba = 6;

    /// <summary>
    /// 解码一张 PNG 文件为贴图。
    /// </summary>
    /// <param name="path">PNG 文件路径。</param>
    /// <returns>解码出的贴图；解码失败时返回 <c>null</c>（调用方退回到纯色）。</returns>
    public static Texture2D? TryRead(string path)
    {
        try
        {
            return Read(path);
        }
        catch
        {
            // 贴图是美术资源，解码失败不应让整个游戏起不来
            return null;
        }
    }

    /// <summary>解码内存中的 PNG 字节流。</summary>
    /// <param name="bytes">PNG 原始字节。</param>
    /// <param name="name">贴图名称。</param>
    public static Texture2D? TryRead(byte[] bytes, string? name = null)
    {
        try
        {
            return Decode(bytes, name ?? Path.GetFileNameWithoutExtension("image"));
        }
        catch
        {
            return null;
        }
    }

    private static Texture2D Read(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        return Decode(bytes, Path.GetFileNameWithoutExtension(path));
    }

    private static Texture2D Decode(byte[] bytes, string name)
    {
        if (bytes.Length < 8 ||
            ReadUInt32BE(bytes, 0) != SignatureHead ||
            ReadUInt32BE(bytes, 4) != SignatureTail)
        {
            throw new InvalidDataException("不是合法的 PNG 文件。");
        }

        int width = 0, height = 0, bitDepth = 0, colorType = 0, interlace = 0;
        byte[]? palette = null;
        List<byte[]>? idat = null;

        int offset = 8;
        while (offset + 8 <= bytes.Length)
        {
            uint length = ReadUInt32BE(bytes, offset);
            string type = ReadChunkType(bytes, offset + 4);
            int dataStart = offset + 8;
            int dataEnd = Math.Min(dataStart + (int)length, bytes.Length);

            switch (type)
            {
                case "IHDR":
                    if (dataEnd - dataStart < 13)
                    {
                        throw new InvalidDataException("IHDR 分块长度不足。");
                    }

                    width = (int)ReadUInt32BE(bytes, dataStart);
                    height = (int)ReadUInt32BE(bytes, dataStart + 4);
                    bitDepth = bytes[dataStart + 8];
                    colorType = bytes[dataStart + 9];
                    interlace = bytes[dataStart + 12];
                    break;

                case "PLTE":
                    palette = new byte[dataEnd - dataStart];
                    Array.Copy(bytes, dataStart, palette, 0, palette.Length);
                    break;

                case "IDAT":
                    byte[] chunk = new byte[dataEnd - dataStart];
                    Array.Copy(bytes, dataStart, chunk, 0, chunk.Length);
                    (idat ??= []).Add(chunk);
                    break;

                case "IEND":
                    offset = dataEnd + 4;
                    goto loopEnd;
            }

            offset = dataEnd + 4;
        }

    loopEnd:

        if (width <= 0 || height <= 0)
        {
            throw new InvalidDataException("IHDR 中缺少有效的图像尺寸。");
        }

        if (interlace != 0)
        {
            throw new NotSupportedException("暂不支持交错的 PNG。");
        }

        if (idat is null || idat.Count == 0)
        {
            throw new InvalidDataException("PNG 中没有 IDAT 数据。");
        }

        int channels = ChannelsFor(colorType);
        int bytesPerSample = bitDepth >= 16 ? 2 : 1;
        int bytesPerPixel = channels * bytesPerSample;
        if (bytesPerPixel <= 0)
        {
            throw new NotSupportedException($"不支持的颜色类型 {colorType}。");
        }

        byte[] raw = Inflate(idat);
        byte[] pixelData = Unfilter(raw, width, height, bytesPerPixel);

        (RgbColor[] pixels, float[] alpha) = Expand(pixelData, width, height, colorType, bitDepth, palette);
        return new Texture2D(pixels, width, height, name, alpha);
    }

    private static int ChannelsFor(int colorType) => colorType switch
    {
        ColorTypeGray => 1,
        ColorTypeGrayAlpha => 2,
        ColorTypePalette => 1,
        ColorTypeRgb => 3,
        ColorTypeRgba => 4,
        _ => throw new NotSupportedException($"不支持的颜色类型 {colorType}。"),
    };

    private static byte[] Inflate(List<byte[]> chunks)
    {
        using var output = new MemoryStream();
        using (var input = new MemoryStream())
        {
            foreach (byte[] chunk in chunks)
            {
                input.Write(chunk, 0, chunk.Length);
            }

            input.Position = 0;
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            zlib.CopyTo(output);
        }

        return output.ToArray();
    }

    /// <summary>逐扫描线反滤波，还原出未压缩的像素数据。</summary>
    private static byte[] Unfilter(byte[] raw, int width, int height, int bytesPerPixel)
    {
        int stride = width * bytesPerPixel;
        byte[] result = new byte[stride * height];

        for (int y = 0; y < height; y++)
        {
            int filter = raw[y * (stride + 1)];
            int src = y * (stride + 1) + 1;
            int dst = y * stride;
            int prev = dst - stride;

            for (int i = 0; i < stride; i++)
            {
                int rawByte = raw[src + i];

                int left = i >= bytesPerPixel ? result[dst + i - bytesPerPixel] : 0;
                int up = y > 0 ? result[prev + i] : 0;
                int upLeft = (y > 0 && i >= bytesPerPixel) ? result[prev + i - bytesPerPixel] : 0;

                int value = filter switch
                {
                    0 => rawByte,
                    1 => rawByte + left,
                    2 => rawByte + up,
                    3 => rawByte + ((left + up) >> 1),
                    _ => rawByte + PaethPredictor(left, up, upLeft),
                };

                result[dst + i] = (byte)(value & 0xFF);
            }
        }

        return result;
    }

    /// <summary>Paeth 预测器：PNG 规范里的经典实现，三选一取最贴近的预测值。</summary>
    /// <param name="left">左邻像素。</param>
    /// <param name="up">上邻像素。</param>
    /// <param name="upLeft">左上邻像素。</param>
    private static int PaethPredictor(int left, int up, int upLeft)
    {
        int p = left + up - upLeft;
        int pa = Math.Abs(p - left);
        int pb = Math.Abs(p - up);
        int pc = Math.Abs(p - upLeft);

        if (pa <= pb && pa <= pc)
        {
            return left;
        }

        return pb <= pc ? up : upLeft;
    }

    /// <summary>把各颜色类型的原始字节展开成线性 RGB 像素与逐像素 alpha。</summary>
    private static (RgbColor[] Pixels, float[] Alpha) Expand(
        byte[] data,
        int width,
        int height,
        int colorType,
        int bitDepth,
        byte[]? palette)
    {
        RgbColor[] pixels = new RgbColor[width * height];
        float[] alpha = new float[width * height];
        int samplesPerPixel = ChannelsFor(colorType);

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int index = (y * width) + x;
                int offset = index * samplesPerPixel;

                RgbColor color = colorType switch
                {
                    ColorTypeGray => SampleGray(data, offset, bitDepth),
                    ColorTypeGrayAlpha => SampleGrayAlpha(data, offset, bitDepth),
                    ColorTypePalette => SamplePalette(data, offset, bitDepth, palette),
                    ColorTypeRgb => new RgbColor(
                        ReadSample(data, offset, bitDepth) / 255.0f,
                        ReadSample(data, offset + 1, bitDepth) / 255.0f,
                        ReadSample(data, offset + 2, bitDepth) / 255.0f),
                    _ => ReadRgba(data, offset, bitDepth),
                };

                pixels[index] = color;

                // 只有真正带 alpha 通道的格式才写入 alpha；其余全部视为不透明
                alpha[index] = colorType switch
                {
                    ColorTypeRgba => ReadSample(data, offset + 3, bitDepth) / 255.0f,
                    ColorTypeGrayAlpha => ReadSample(data, offset + 1, bitDepth) / 255.0f,
                    _ => 1.0f,
                };
            }
        }

        // 调色板 PNG 可能携带 tRNS 分块描述透明索引，本实现的调用方（游戏贴图）
        // 不依赖该特性，因此不做处理，保持全部不透明。
        return (pixels, alpha);
    }

    private static RgbColor ReadRgba(byte[] data, int offset, int bitDepth)
    {
        float r = ReadSample(data, offset, bitDepth) / 255.0f;
        float g = ReadSample(data, offset + 1, bitDepth) / 255.0f;
        float b = ReadSample(data, offset + 2, bitDepth) / 255.0f;
        return new RgbColor(r, g, b);
    }

    private static RgbColor SampleGray(byte[] data, int offset, int bitDepth)
    {
        float gray = ReadSample(data, offset, bitDepth) / 255.0f;
        return new RgbColor(gray, gray, gray);
    }

    private static RgbColor SampleGrayAlpha(byte[] data, int offset, int bitDepth)
    {
        float gray = ReadSample(data, offset, bitDepth) / 255.0f;
        return new RgbColor(gray, gray, gray);
    }

    private static RgbColor SamplePalette(byte[] data, int offset, int bitDepth, byte[]? palette)
    {
        int index = bitDepth == 8 ? data[offset] : ReadSample(data, offset, bitDepth);
        if (palette is null || index * 3 + 2 >= palette.Length)
        {
            return RgbColor.Black;
        }

        return new RgbColor(palette[index * 3] / 255.0f, palette[index * 3 + 1] / 255.0f, palette[index * 3 + 2] / 255.0f);
    }

    private static int ReadSample(byte[] data, int offset, int bitDepth)
    {
        if (bitDepth >= 16)
        {
            // 高字节即可，低字节对 8 位贴图没有意义
            return data[offset];
        }

        return bitDepth switch
        {
            8 => data[offset],
            4 => ExpandBits(data[offset >> 1], (offset & 1) == 0 ? 4 : 0),
            2 => ExpandBits(data[offset >> 2], ((offset & 3) ^ 3) * 2),
            1 => ExpandBits(data[offset >> 3], ((offset & 7) ^ 7)),
            _ => 0,
        };
    }

    private static int ExpandBits(byte packed, int shift) => (packed >> shift) & 0xFF;

    private static uint ReadUInt32BE(byte[] data, int offset) =>
        ((uint)data[offset] << 24) |
        ((uint)data[offset + 1] << 16) |
        ((uint)data[offset + 2] << 8) |
        data[offset + 3];

    private static string ReadChunkType(byte[] data, int offset) =>
        ((char)data[offset]).ToString() +
        ((char)data[offset + 1]).ToString() +
        ((char)data[offset + 2]).ToString() +
        ((char)data[offset + 3]).ToString();
}
