using Marigold.Imaging;

namespace Marigold.Rendering;

/// <summary>
/// 帧缓冲：一块颜色缓冲加一块深度缓冲，是渲染器的输出目标。
/// </summary>
/// <remarks>
/// <para>
/// 帧缓冲是渲染器的核心，由两块缓冲组成：
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>颜色缓冲</b>用浮点 <see cref="RgbColor"/> 存储，保留过曝信息，
/// 到写文件时才量化为 8 位。
/// </description></item>
/// <item><description>
/// <b>深度缓冲</b>按 NDC 深度（-1 近 / +1 远）存储，采用“更近者胜”，
/// 从而正确处理面与面之间的遮挡，而不需要依赖绘制顺序。
/// </description></item>
/// </list>
/// <para>
/// 缓冲使用一维数组加行主序索引，且刻意避免使用二维数组——
/// 单块连续内存在逐像素写入时能显著减少缓存未命中。
/// </para>
/// </remarks>
public sealed class FrameBuffer : IRenderTarget
{
    private readonly RgbColor[] _color;
    private readonly float[] _depth;

    /// <summary>构造帧缓冲。</summary>
    /// <param name="width">宽度（像素），必须为正。</param>
    /// <param name="height">高度（像素），必须为正。</param>
    /// <exception cref="ArgumentOutOfRangeException">宽或高不是正数。</exception>
    public FrameBuffer(int width, int height)
    {
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), width, "宽度必须为正数。");
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height), height, "高度必须为正数。");
        }

        Width = width;
        Height = height;
        _color = new RgbColor[width * height];
        _depth = new float[width * height];
        ClearDepth();
    }

    /// <summary>宽度（像素）。</summary>
    public int Width { get; }

    /// <summary>高度（像素）。</summary>
    public int Height { get; }

    /// <summary>宽高比。</summary>
    public float AspectRatio => Width / (float)Height;

    /// <summary>像素总数。</summary>
    public int PixelCount => _color.Length;

    /// <summary>颜色缓冲的原始数据。</summary>
    public Span<RgbColor> ColorPixels => _color;

    /// <summary>深度缓冲的原始数据。</summary>
    public Span<float> DepthPixels => _depth;

    /// <summary>
    /// 用指定颜色清屏，并把深度缓冲复位到远平面。
    /// </summary>
    /// <param name="color">背景色。</param>
    public void Clear(RgbColor color)
    {
        _color.AsSpan().Fill(color);
        ClearDepth();
    }

    /// <summary>只复位深度缓冲。</summary>
    /// <param name="depth">复位到的深度值，默认为远平面 +1。</param>
    public void ClearDepth(float depth = 1.0f) => _depth.AsSpan().Fill(depth);

    /// <summary>只清空颜色缓冲。</summary>
    public void ClearColor(RgbColor color) => _color.AsSpan().Fill(color);

    /// <summary>判断像素坐标是否在视口内。</summary>
    public bool Contains(int x, int y) => (uint)x < (uint)Width && (uint)y < (uint)Height;

    /// <summary>把二维坐标转换为一维索引。</summary>
    public int IndexOf(int x, int y) => (y * Width) + x;

    /// <summary>读取某个像素的颜色（越界返回黑色）。</summary>
    public RgbColor GetPixel(int x, int y) => Contains(x, y) ? _color[IndexOf(x, y)] : RgbColor.Black;

    /// <summary>写入某个像素的颜色（越界忽略）。</summary>
    public void SetPixel(int x, int y, RgbColor color)
    {
        if (Contains(x, y))
        {
            _color[IndexOf(x, y)] = color;
        }
    }

    /// <summary>读取深度值（越界返回远平面）。</summary>
    public float GetDepth(int x, int y) => Contains(x, y) ? _depth[IndexOf(x, y)] : 1.0f;

    /// <summary>写入深度值（越界忽略）。</summary>
    public void SetDepth(int x, int y, float depth)
    {
        if (Contains(x, y))
        {
            _depth[IndexOf(x, y)] = depth;
        }
    }

    /// <summary>
    /// 把图像按整数倍缩小，得到更小尺寸的新帧缓冲（仅取颜色，深度重置）。
    /// </summary>
    /// <remarks>用于生成缩略图或供 ASCII 预览做降采样。</remarks>
    public FrameBuffer Downsample(int factor)
    {
        if (factor <= 1)
        {
            return this;
        }

        int newWidth = Math.Max(1, Width / factor);
        int newHeight = Math.Max(1, Height / factor);
        FrameBuffer result = new(newWidth, newHeight);

        for (int y = 0; y < newHeight; y++)
        {
            for (int x = 0; x < newWidth; x++)
            {
                float r = 0.0f;
                float g = 0.0f;
                float b = 0.0f;
                int samples = 0;

                for (int sy = 0; sy < factor; sy++)
                {
                    for (int sx = 0; sx < factor; sx++)
                    {
                        int px = (x * factor) + sx;
                        int py = (y * factor) + sy;
                        if (!Contains(px, py))
                        {
                            continue;
                        }

                        RgbColor c = _color[IndexOf(px, py)];
                        r += c.R;
                        g += c.G;
                        b += c.B;
                        samples++;
                    }
                }

                result.SetPixel(x, y, samples == 0
                    ? RgbColor.Black
                    : new RgbColor(r / samples, g / samples, b / samples));
            }
        }

        return result;
    }

    /// <summary>统计图像亮度范围，便于快速判断是否“整张全白”或“整张全黑”。</summary>
    public (float Min, float Max, float Mean) GetLuminanceRange()
    {
        float min = float.MaxValue;
        float max = float.MinValue;
        double sum = 0.0;

        foreach (RgbColor color in _color)
        {
            float lum = color.Luminance;
            if (lum < min)
            {
                min = lum;
            }

            if (lum > max)
            {
                max = lum;
            }

            sum += lum;
        }

        if (_color.Length == 0)
        {
            return (0.0f, 0.0f, 0.0f);
        }

        return (min, max, (float)(sum / _color.Length));
    }

    /// <summary>
    /// 逐像素比较两张同尺寸帧缓冲。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 这是给渲染器做<b>回归验证</b>用的工具。最能说明问题的一个用例是检查走廊循环：
    /// 如果两个房间的复用逻辑正确，那么“第一次推进走廊之后第 K 帧”与
    /// “第二次推进之后第 K 帧”应当是完全相同的画面——
    /// 因为玩家在同一房间内的相对位置一致，前方的门也一样远。
    /// 一旦有人改坏了搬运顺序或碰撞时序，这个比较会立刻报警。
    /// </para>
    /// <para>
    /// 比较前会把两个颜色分量钳制到 [0, 1]，避免过曝值的差异被放大。
    /// </para>
    /// </remarks>
    /// <param name="left">帧缓冲 A。</param>
    /// <param name="right">帧缓冲 B。</param>
    /// <param name="tolerance">判定为“不同”的通道差值阈值，默认为 1/255。</param>
    /// <exception cref="ArgumentException">两张图像尺寸不一致。</exception>
    public static FrameDifference Compare(FrameBuffer left, FrameBuffer right, float tolerance = 1.0f / 255.0f)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        if (left.Width != right.Width || left.Height != right.Height)
        {
            throw new ArgumentException(
                $"尺寸不一致：{left.Width}×{left.Height} 与 {right.Width}×{right.Height}。");
        }

        double sum = 0.0;
        float max = 0.0f;
        int differing = 0;

        for (int i = 0; i < left._color.Length; i++)
        {
            RgbColor a = left._color[i].Clamped();
            RgbColor b = right._color[i].Clamped();

            float dr = MathF.Abs(a.R - b.R);
            float dg = MathF.Abs(a.G - b.G);
            float db = MathF.Abs(a.B - b.B);

            float pixelDifference = MathF.Max(dr, MathF.Max(dg, db));
            sum += (dr + dg + db) / 3.0;

            if (pixelDifference > max)
            {
                max = pixelDifference;
            }

            if (pixelDifference > tolerance)
            {
                differing++;
            }
        }

        return new FrameDifference(
            (float)(sum / left._color.Length),
            max,
            differing,
            left._color.Length);
    }

    /// <inheritdoc />
    public override string ToString() => $"FrameBuffer({Width}×{Height})";
}
