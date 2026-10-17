using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Marigold.Rendering;

namespace Marigold.Imaging;

/// <summary>
/// 图像写出器：把帧缓冲保存为 PNG / BMP / PPM。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么自己写编码器</b>：.NET 的图像处理库（<c>System.Drawing.Common</c>、
/// <c>ImageSharp</c>、<c>SkiaSharp</c>）要么只在 Windows 可用、要么需要引入第三方包。
/// 而本项目只需要“把浮点帧缓冲写成 8 位 RGB”这一件事，
/// 手写三种格式加起来不到 300 行，却换来<b>零依赖、跨平台、可审计</b>。
/// </para>
/// <para>
/// <b>PNG 的压缩实现</b>：PNG 的 IDAT 数据块就是 zlib 流，而 .NET 6+ 内置了
/// <see cref="ZLibStream"/>，因此可以直接用它生成符合规范的压缩数据，
/// 不必自己实现 DEFLATE。需要手写的只有分块结构、CRC32 校验与扫描行过滤字节。
/// </para>
/// <para>
/// <b>关于伽马</b>：渲染器输出的是线性浮点颜色。统一的 <c>gamma</c> 参数
/// 决定写入磁盘时的编码：
/// </para>
/// <list type="bullet">
/// <item><description>
/// <c>1.0</c>（默认）：直接量化，<b>不做</b>伽马编码。
/// 这样图像里的数值与渲染器内部计算值一一对应，便于算法调试与像素级验证。
/// </description></item>
/// <item><description>
/// <c>1/2.2</c>：标准 sRGB 编码，观感更接近物理正确的呈现，暗部层次更丰富。
/// 若要出图给别人看，建议改成这个值。
/// </description></item>
/// </list>
/// </remarks>
public static class ImageWriter
{
    private static readonly uint[] Crc32Table = BuildCrc32Table();

    /// <summary>根据扩展名推断图像格式，无法识别时返回 <see cref="ImageFormat.Png"/>。</summary>
    public static ImageFormat DetectFormat(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".bmp" => ImageFormat.Bmp,
            ".ppm" or ".pnm" => ImageFormat.Ppm,
            _ => ImageFormat.Png,
        };
    }

    /// <summary>按扩展名推断格式并写出文件。</summary>
    /// <param name="buffer">源帧缓冲。</param>
    /// <param name="path">输出路径，目录不存在会自动创建。</param>
    /// <param name="gamma">输出伽马，1.0 表示不做编码。</param>
    public static void Write(FrameBuffer buffer, string path, float gamma = 1.0f) =>
        Write(buffer, path, DetectFormat(path), gamma);

    /// <summary>写出文件并指定格式。</summary>
    public static void Write(FrameBuffer buffer, string path, ImageFormat format, float gamma = 1.0f)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using FileStream stream = File.Create(path);
        Write(buffer, stream, format, gamma);
    }

    /// <summary>写出到流。</summary>
    public static void Write(FrameBuffer buffer, Stream stream, ImageFormat format, float gamma = 1.0f)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(stream);

        switch (format)
        {
            case ImageFormat.Bmp:
                WriteBmp(buffer, stream, gamma);
                break;
            case ImageFormat.Ppm:
                WritePpm(buffer, stream, gamma);
                break;
            case ImageFormat.Png:
            default:
                WritePng(buffer, stream, gamma);
                break;
        }
    }

    /// <summary>把帧缓冲编码为 PNG 字节数组。</summary>
    public static byte[] EncodePng(FrameBuffer buffer, float gamma = 1.0f)
    {
        using MemoryStream stream = new();
        WritePng(buffer, stream, gamma);
        return stream.ToArray();
    }

    /// <summary>
    /// 写出 PNG（8 位真彩色，无隔行，扫描行过滤全部使用 None）。
    /// </summary>
    /// <remarks>
    /// PNG 结构：<c>签名 + IHDR + IDAT + IEND</c>，每个块都带长度与 CRC32。
    /// 过滤字节全部取 0（不过滤）是刻意选择：本引擎的输出大量是纯色区块，
    /// 未经过滤的数据本身就有极高的压缩率，并省掉一层逐像素预测计算。
    /// </remarks>
    public static void WritePng(FrameBuffer buffer, Stream stream, float gamma = 1.0f)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(stream);

        const int bytesPerPixel = 3;
        int stride = (buffer.Width * bytesPerPixel) + 1; // +1 为每行的过滤字节

        byte[] raw = new byte[stride * buffer.Height];
        Span<RgbColor> source = buffer.ColorPixels;

        for (int y = 0; y < buffer.Height; y++)
        {
            int rowStart = y * stride;
            raw[rowStart] = 0; // 过滤类型 None

            for (int x = 0; x < buffer.Width; x++)
            {
                RgbColor color = source[(y * buffer.Width) + x];
                int offset = rowStart + 1 + (x * bytesPerPixel);
                raw[offset] = RgbColor.Quantize(color.R, gamma);
                raw[offset + 1] = RgbColor.Quantize(color.G, gamma);
                raw[offset + 2] = RgbColor.Quantize(color.B, gamma);
            }
        }

        byte[] compressed;
        using (MemoryStream compressedStream = new())
        {
            using (ZLibStream zlib = new(compressedStream, CompressionLevel.Optimal, leaveOpen: true))
            {
                zlib.Write(raw, 0, raw.Length);
            }

            compressed = compressedStream.ToArray();
        }

        // PNG 文件签名
        stream.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        // IHDR
        byte[] header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0, 4), buffer.Width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4, 4), buffer.Height);
        header[8] = 8;  // 位深 8
        header[9] = 2;  // 颜色类型 2 = 真彩色 RGB
        header[10] = 0; // 压缩方法
        header[11] = 0; // 过滤方法
        header[12] = 0; // 隔行方式：无
        WriteChunk(stream, "IHDR", header);

        WriteChunk(stream, "IDAT", compressed);
        WriteChunk(stream, "IEND", []);
    }

    /// <summary>写出 24 位 BMP（自下而上存储）。</summary>
    public static void WriteBmp(FrameBuffer buffer, Stream stream, float gamma = 1.0f)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(stream);

        int rowStride = ((buffer.Width * 3) + 3) & ~3; // 每行按 4 字节对齐
        int pixelDataSize = rowStride * buffer.Height;
        const int fileHeaderSize = 14;
        const int infoHeaderSize = 40;

        Span<RgbColor> source = buffer.ColorPixels;
        byte[] file = new byte[fileHeaderSize + infoHeaderSize + pixelDataSize];

        // BITMAPFILEHEADER
        file[0] = (byte)'B';
        file[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(2, 4), file.Length);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(10, 4), fileHeaderSize + infoHeaderSize);

        // BITMAPINFOHEADER
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(14, 4), infoHeaderSize);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(18, 4), buffer.Width);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(22, 4), buffer.Height); // 正数 = 自下而上
        BinaryPrimitives.WriteInt16LittleEndian(file.AsSpan(26, 2), 1);
        BinaryPrimitives.WriteInt16LittleEndian(file.AsSpan(28, 2), 24);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(34, 4), pixelDataSize);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(38, 4), 2835); // 约 72 DPI
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(42, 4), 2835);

        int dataOffset = fileHeaderSize + infoHeaderSize;
        for (int y = 0; y < buffer.Height; y++)
        {
            // BMP 的行序自下而上，且通道顺序为 BGR
            int sourceRow = buffer.Height - 1 - y;
            int rowStart = dataOffset + (y * rowStride);

            for (int x = 0; x < buffer.Width; x++)
            {
                RgbColor color = source[(sourceRow * buffer.Width) + x];
                int offset = rowStart + (x * 3);
                file[offset] = RgbColor.Quantize(color.B, gamma);
                file[offset + 1] = RgbColor.Quantize(color.G, gamma);
                file[offset + 2] = RgbColor.Quantize(color.R, gamma);
            }
        }

        stream.Write(file, 0, file.Length);
    }

    /// <summary>写出 PPM（P6 二进制格式）。</summary>
    public static void WritePpm(FrameBuffer buffer, Stream stream, float gamma = 1.0f)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(stream);

        byte[] header = Encoding.ASCII.GetBytes($"P6\n{buffer.Width} {buffer.Height}\n255\n");
        stream.Write(header, 0, header.Length);

        byte[] row = new byte[buffer.Width * 3];
        Span<RgbColor> source = buffer.ColorPixels;

        for (int y = 0; y < buffer.Height; y++)
        {
            for (int x = 0; x < buffer.Width; x++)
            {
                RgbColor color = source[(y * buffer.Width) + x];
                row[x * 3] = RgbColor.Quantize(color.R, gamma);
                row[(x * 3) + 1] = RgbColor.Quantize(color.G, gamma);
                row[(x * 3) + 2] = RgbColor.Quantize(color.B, gamma);
            }

            stream.Write(row, 0, row.Length);
        }
    }

    /// <summary>
    /// 写出一个 PNG 分块。
    /// </summary>
    /// <remarks>结构：4 字节长度 + 4 字节类型 + 数据 + 4 字节 CRC32（校验范围覆盖类型与数据）。</remarks>
    private static void WriteChunk(Stream stream, string type, ReadOnlySpan<byte> data)
    {
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);

        // CRC 需要覆盖类型与数据，因此先算后写
        uint crc = 0xFFFFFFFFu;
        crc = UpdateCrc(crc, typeBytes);
        crc = UpdateCrc(crc, data);
        crc ^= 0xFFFFFFFFu;

        Span<byte> header = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, data.Length);
        stream.Write(header);

        stream.Write(typeBytes, 0, typeBytes.Length);
        stream.Write(data);

        BinaryPrimitives.WriteUInt32BigEndian(header, crc);
        stream.Write(header);
    }

    private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
        {
            crc = Crc32Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    private static uint[] BuildCrc32Table()
    {
        uint[] table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[i] = c;
        }

        return table;
    }
}
