namespace Marigold.Imaging;

/// <summary>图像文件格式。</summary>
public enum ImageFormat
{
    /// <summary>
    /// PNG，8 位真彩色。
    /// </summary>
    /// <remarks>
    /// 首选格式：无损、体积小、所有看图工具都支持。本引擎自带编码器，
    /// 只依赖 .NET 内置的 <c>ZLibStream</c>，不需要任何第三方图像库。
    /// </remarks>
    Png = 0,

    /// <summary>BMP，24 位未压缩。文件体积大但结构最简单，任何 Windows 程序都能打开。</summary>
    Bmp = 1,

    /// <summary>PPM（P6），最朴素的图像格式，常用于算法验证与管线对接。</summary>
    Ppm = 2,
}
