using Marigold.Mathematics;

namespace Marigold.Imaging;

/// <summary>
/// 线性浮点 RGBA 颜色。
/// </summary>
/// <remarks>
/// <para>
/// 渲染管线内部一律使用浮点线性颜色，只有在写出 8 位图像时才做钳制与量化。
/// 这样做的原因是：光照累加可能会超过 1.0（过曝），如果中间就用字节存储，
/// 加法会溢出截断，白墙交界处会出现难看的色块。
/// </para>
/// <para>
/// <b>Alpha 通道</b>：0 表示完全透明，1 表示完全不透明。RGB 与 Alpha 解耦——
/// 颜色可以亮过 1.0（HDR 过曝），同时 Alpha 独立描述覆盖度。默认构造（仅传 RGB）时
/// Alpha 取 1.0，因此既有的「只关心颜色」的代码无需改动。
/// </para>
/// <para>
/// <b>颜色空间</b>：本结构体不隐含任何伽马编码。是否做 sRGB 编码由
/// <see cref="ImageWriter"/> 在输出阶段按需决定。
/// </para>
/// </remarks>
public readonly struct RgbColor : IEquatable<RgbColor>
{
    /// <summary>红色分量，通常位于 [0, 1]，允许超出以表示过曝。</summary>
    public readonly float R;

    /// <summary>绿色分量。</summary>
    public readonly float G;

    /// <summary>蓝色分量。</summary>
    public readonly float B;

    /// <summary>Alpha（不透明度），0 = 完全透明，1 = 完全不透明。</summary>
    public readonly float A;

    /// <summary>纯黑。</summary>
    public static readonly RgbColor Black = new(0.0f, 0.0f, 0.0f);

    /// <summary>纯白（不透明）。</summary>
    public static readonly RgbColor White = new(1.0f, 1.0f, 1.0f);

    /// <summary>完全透明（alpha = 0）。</summary>
    public static readonly RgbColor Transparent = new(0.0f, 0.0f, 0.0f, 0.0f);

    /// <summary>构造一个不透明颜色（alpha = 1）。</summary>
    public RgbColor(float r, float g, float b) : this(r, g, b, 1.0f)
    {
    }

    /// <summary>构造一个带 alpha 的颜色。</summary>
    public RgbColor(float r, float g, float b, float a)
    {
        R = r;
        G = g;
        B = b;
        A = a;
    }

    /// <summary>用同一个灰度值构造不透明颜色。</summary>
    public static RgbColor FromGray(float gray) => new(gray, gray, gray);

    /// <summary>用 0-255 的字节构造不透明颜色（按线性值直接映射，不做伽马解码）。</summary>
    public static RgbColor FromBytes(byte r, byte g, byte b) =>
        new(r / 255.0f, g / 255.0f, b / 255.0f);

    /// <summary>用 0-255 的字节构造带 alpha 的颜色。</summary>
    public static RgbColor FromBytes(byte r, byte g, byte b, byte a) =>
        new(r / 255.0f, g / 255.0f, b / 255.0f, a / 255.0f);

    /// <summary>感知亮度（Rec. 709 权重），用于 ASCII 预览等灰度场景。</summary>
    public float Luminance => (0.2126f * R) + (0.7152f * G) + (0.0722f * B);

    /// <summary>返回把各分量限制到 [0, 1] 后的新颜色。</summary>
    public RgbColor Clamped() =>
        new(MathUtil.Clamp01(R), MathUtil.Clamp01(G), MathUtil.Clamp01(B), MathUtil.Clamp01(A));

    /// <summary>两个颜色线性插值（含 alpha），<paramref name="t"/> 为 0 时返回本颜色。</summary>
    public RgbColor LerpTo(RgbColor target, float t) => new(
        MathUtil.Lerp(R, target.R, t),
        MathUtil.Lerp(G, target.G, t),
        MathUtil.Lerp(B, target.B, t),
        MathUtil.Lerp(A, target.A, t));

    /// <summary>仅对 RGB 三通道插值，alpha 取本颜色。</summary>
    /// <remarks>用于已知不透明材质的光照累加，避免 alpha 被错误平均。</remarks>
    public RgbColor LerpRgbTo(RgbColor target, float t) => new(
        MathUtil.Lerp(R, target.R, t),
        MathUtil.Lerp(G, target.G, t),
        MathUtil.Lerp(B, target.B, t),
        A);

    /// <summary>逐分量相乘（含 alpha，模拟光照与反照率相乘）。</summary>
    public static RgbColor operator *(RgbColor left, RgbColor right) =>
        new(left.R * right.R, left.G * right.G, left.B * right.B, left.A * right.A);

    /// <summary>整体缩放亮度（alpha 不变）。</summary>
    public static RgbColor operator *(RgbColor color, float scale) =>
        new(color.R * scale, color.G * scale, color.B * scale, color.A);

    /// <summary>整体缩放亮度（交换参数顺序）。</summary>
    public static RgbColor operator *(float scale, RgbColor color) => color * scale;

    /// <summary>逐分量相加（含 alpha）。</summary>
    public static RgbColor operator +(RgbColor left, RgbColor right) =>
        new(left.R + right.R, left.G + right.G, left.B + right.B, left.A + right.A);

    /// <summary>逐分量相减（alpha 不变）。</summary>
    public static RgbColor operator -(RgbColor left, RgbColor right) =>
        new(left.R - right.R, left.G - right.G, left.B - right.B, left.A);

    /// <summary>
    /// 把线性浮点颜色量化为 8 位通道值。
    /// </summary>
    /// <param name="value">线性分量值。</param>
    /// <param name="gamma">
    /// 输出伽马。<c>1.0</c> 表示不做伽马编码；
    /// 设为 <c>1/2.2f</c> 可得到 sRGB 编码的标准观感。
    /// </param>
    public static byte Quantize(float value, float gamma = 1.0f)
    {
        float v = MathUtil.Clamp01(value);
        if (gamma > 0.0f && MathF.Abs(gamma - 1.0f) > MathUtil.Epsilon)
        {
            v = MathF.Pow(v, gamma);
        }

        // 加 0.5 做四舍五入，避免整体偏暗
        return (byte)MathUtil.Clamp((int)((v * 255.0f) + 0.5f), 0, 255);
    }

    /// <inheritdoc />
    public bool Equals(RgbColor other) => R == other.R && G == other.G && B == other.B && A == other.A;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is RgbColor other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(R, G, B, A);

    /// <summary>相等运算符。</summary>
    public static bool operator ==(RgbColor left, RgbColor right) => left.Equals(right);

    /// <summary>不等运算符。</summary>
    public static bool operator !=(RgbColor left, RgbColor right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString() => $"Rgb({R:0.###}, {G:0.###}, {B:0.###}, {A:0.###})";
}
