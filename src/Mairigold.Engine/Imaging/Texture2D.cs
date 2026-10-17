using System.Numerics;
using Marigold.Mathematics;

namespace Marigold.Imaging;

/// <summary>纹理寻址方式（超出 [0,1] 时的处理）。</summary>
public enum TextureAddressMode
{
    /// <summary>重复平铺，用于墙纸、地板这类需要无缝铺开的表面。</summary>
    Wrap = 0,

    /// <summary>拉伸到边界像素，用于门牌、文字牌这类一次性贴片。</summary>
    Clamp = 1,
}

/// <summary>纹理采样过滤方式。</summary>
public enum TextureFilterMode
{
    /// <summary>邻近采样，保留像素颗粒感，符合 渲染器的低分辨率美术。</summary>
    Nearest = 0,

    /// <summary>双线性插值，远距离墙面更干净，代价是多一次取址。</summary>
    Linear = 1,
}

/// <summary>
/// 一张二维贴图：宽高 + 逐像素线性 RGB（与渲染管线内部一致）。
/// </summary>
/// <remarks>
/// <para>
/// 引擎内部一律用浮点线性颜色存储贴图，与 <see cref="FrameBuffer"/> 保持一致。
/// 若在这里就量化成 8 位字节，墙面贴图在强环境光（本项目 ambient 0.88 + sun 0.85）
/// 下会出现明显色阶断层。
/// </para>
/// <para>
/// <b>坐标约定</b>：<c>v = 0</c> 对应<b>贴图顶行</b>，<c>v = 1</c> 对应底行。
/// 这与引擎程序化网格（<see cref="Geometry.PrimitiveMeshes.Box"/> 六个面均把顶点 <c>v=0</c>
/// 放在 <c>y = +h</c> 一侧）天然一致，因此加载外部图片时无需再翻转一次。
/// </para>
/// <para>
/// <b>为什么不用字节数组</b>：同一张贴图会被成千上万个片元采样，
/// 用 <c>RgbColor[]</c> 让采样代码直接走 <c>Span</c> 索引，没有任何装箱与转换开销。
/// </para>
/// </remarks>
public sealed class Texture2D
{
    /// <summary>贴图像素数据，按行优先存储，第 0 行在顶部。</summary>
    private readonly RgbColor[] _pixels;

    /// <summary>
    /// 逐像素 alpha（0 = 全透明，1 = 不透明）。为 <c>null</c> 表示整张图完全不透明。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 本引擎没有透明混合管线（软件光栅化器只写不透明的颜色与深度），
    /// 因此 alpha 不在渲染时参与混合，而是在<b>资源准备阶段</b>用来把贴图
    /// 合成到它将要贴附的那个表面上——见 <see cref="Composite"/>。
    /// </para>
    /// <para>
    /// 这样做的直接好处是：角色的贴图边缘不会出现一圈白边或黑边。
    /// 若简单地按固定底色预乘，站在肉色墙面前的白色怪物就会自带一圈白晕；
    /// 按目标表面的贴图逐像素合成，则镂空区域与被遮挡的墙面完全一致，
    /// 看起来就是「怪物站在那里」而不是「一张带白底的图片站在那里」。
    /// </para>
    /// </remarks>
    private readonly float[]? _alpha;

    /// <summary>宽度的「2 的幂减一」掩码；为 -1 表示宽度不是 2 的幂。</summary>
    private readonly int _widthMask;

    /// <summary>高度的「2 的幂减一」掩码；为 -1 表示高度不是 2 的幂。</summary>
    private readonly int _heightMask;

    /// <summary>构造一张空贴图（用于后续逐像素填充）。</summary>
    /// <param name="width">宽度（像素）。</param>
    /// <param name="height">高度（像素）。</param>
    /// <param name="name">名称。</param>
    /// <exception cref="ArgumentOutOfRangeException">宽高非正数。</exception>
    public Texture2D(int width, int height, string? name = null)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width), "贴图宽度必须为正数。");
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height), "贴图高度必须为正数。");

        Width = width;
        Height = height;
        Name = name ?? "Texture";
        _pixels = new RgbColor[width * height];
        _widthMask = MaskOf(width);
        _heightMask = MaskOf(height);
    }

    /// <summary>用已有像素数据构造贴图。</summary>
    /// <param name="pixels">行优先的像素数据，长度必须等于 宽 × 高。</param>
    /// <param name="width">宽度（像素）。</param>
    /// <param name="height">高度（像素）。</param>
    /// <param name="name">名称。</param>
    /// <param name="alpha">逐像素 alpha，可为 <c>null</c>。</param>
    public Texture2D(RgbColor[] pixels, int width, int height, string? name = null, float[]? alpha = null)
        : this(width, height, name)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        if (pixels.Length < width * height)
        {
            throw new ArgumentException("像素数据长度不足以填满该尺寸的贴图。", nameof(pixels));
        }

        _pixels = pixels;
        _alpha = alpha is { Length: > 0 } && alpha.Length >= width * height ? alpha : null;
    }

    /// <summary>贴图是否带 alpha 通道。</summary>
    public bool HasAlpha => _alpha is not null;

    /// <summary>贴图宽度（像素）。</summary>
    public int Width { get; }

    /// <summary>贴图高度（像素）。</summary>
    public int Height { get; }

    /// <summary>贴图名称，用于调试与统计。</summary>
    public string Name { get; }

    /// <summary>像素数据（只读视图，供采样器直接遍历）。</summary>
    public ReadOnlySpan<RgbColor> Pixels => _pixels;

    /// <summary>
    /// 平铺次数。大于 1 时 <c>Texture2D</c> 在采样前把 UV 乘以它，
    /// 于是同一张图会在一个面上重复铺开——墙纸、地板、天花板都靠这个参数。
    /// </summary>
    public Vector2 Tiling { get; set; } = new(1.0f, 1.0f);

    /// <summary>寻址方式。</summary>
    public TextureAddressMode AddressMode { get; set; } = TextureAddressMode.Wrap;

    /// <summary>过滤方式。</summary>
    public TextureFilterMode FilterMode { get; set; } = TextureFilterMode.Nearest;

    /// <summary>
    /// 采样一张贴图（返回未经过滤的基准颜色，供着色器自行乘光照）。
    /// </summary>
    /// <param name="u">水平坐标，0 在左，1 在右。</param>
    /// <param name="v">垂直坐标，0 在<b>顶</b>行，1 在底行。</param>
    public RgbColor Sample(float u, float v)
    {
        float su = u * Tiling.X;
        float sv = v * Tiling.Y;

        int x = (int)(su * Width);
        int y = (int)(sv * Height);

        if (AddressMode == TextureAddressMode.Wrap)
        {
            x = WrapX(x);
            y = WrapY(y);
        }
        else
        {
            x = MathUtil.Clamp(x, 0, Width - 1);
            y = MathUtil.Clamp(y, 0, Height - 1);
        }

        return _pixels[(y * Width) + x];
    }

    /// <summary>双线性采样，用于远距离墙面压掉摩尔纹。</summary>
    /// <param name="u">水平坐标。</param>
    /// <param name="v">垂直坐标（0 在顶行）。</param>
    /// <remarks>
    /// 四个取样点必须按 <see cref="AddressMode"/> 处理后再取：
    /// 平铺墙面每 1.6 米就重复一次，UV 会跑到 [0,1] 之外很远处，
    /// 此时若把坐标钳到边缘，整面墙都会变成最边上一列像素拉出来的长条。
    /// </remarks>
    public RgbColor SampleLinear(float u, float v)
    {
        float su = (u * Tiling.X * Width) - 0.5f;
        float sv = (v * Tiling.Y * Height) - 0.5f;

        int x0 = (int)MathF.Floor(su);
        int y0 = (int)MathF.Floor(sv);

        float tx = su - x0;
        float ty = sv - y0;

        bool wrap = AddressMode == TextureAddressMode.Wrap;

        int x1 = x0 + 1;
        int y1 = y0 + 1;

        if (wrap)
        {
            x0 = WrapX(x0);
            x1 = WrapX(x1);
            y0 = WrapY(y0);
            y1 = WrapY(y1);
        }
        else
        {
            x0 = MathUtil.Clamp(x0, 0, Width - 1);
            x1 = MathUtil.Clamp(x1, 0, Width - 1);
            y0 = MathUtil.Clamp(y0, 0, Height - 1);
            y1 = MathUtil.Clamp(y1, 0, Height - 1);
        }

        RgbColor c00 = _pixels[(y0 * Width) + x0];
        RgbColor c10 = _pixels[(y0 * Width) + x1];
        RgbColor c01 = _pixels[(y1 * Width) + x0];
        RgbColor c11 = _pixels[(y1 * Width) + x1];

        RgbColor top = c00.LerpTo(c10, tx);
        RgbColor bottom = c01.LerpTo(c11, tx);
        return top.LerpTo(bottom, ty);
    }

    /// <summary>带过滤的采样入口，由着色器按 <see cref="FilterMode"/> 调用。</summary>
    /// <param name="u">水平坐标。</param>
    /// <param name="v">垂直坐标（0 在顶行）。</param>
    public RgbColor SampleFiltered(float u, float v) =>
        FilterMode == TextureFilterMode.Linear ? SampleLinear(u, v) : Sample(u, v);

    /// <summary>把整张贴图的每个像素乘以一个颜色（代码染色，例如金色 / 蓝色墙纸）。</summary>
    /// <param name="tint">染色系数。</param>
    public void Tint(RgbColor tint)
    {
        for (int i = 0; i < _pixels.Length; i++)
        {
            _pixels[i] = _pixels[i] * tint;
        }
    }

    /// <summary>整体乘以一个标量亮度。</summary>
    /// <param name="scale">亮度系数。</param>
    public void Scale(float scale)
    {
        for (int i = 0; i < _pixels.Length; i++)
        {
            _pixels[i] = _pixels[i] * scale;
        }
    }

    /// <summary>复制一份贴图（共享独立的像素数组），便于做染色变体。</summary>
    /// <param name="newName">新名称。</param>
    public Texture2D Clone(string? newName = null)
    {
        RgbColor[] copy = new RgbColor[Width * Height];
        Array.Copy(_pixels, copy, copy.Length);

        float[]? alphaCopy = null;
        if (_alpha is not null)
        {
            alphaCopy = new float[_alpha.Length];
            Array.Copy(_alpha, alphaCopy, alphaCopy.Length);
        }

        return new Texture2D(copy, Width, Height, newName ?? Name, alphaCopy)
        {
            Tiling = Tiling,
            AddressMode = AddressMode,
            FilterMode = FilterMode,
        };
    }

    /// <summary>
    /// 把本贴图（前景）按 alpha 合成到另一张贴图（背景）上，得到一张完全不透明的新贴图。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 背景贴图会以 <paramref name="backgroundTiling"/> 平铺，并可用
    /// <paramref name="backgroundOffset"/> 平移对齐。之所以要传入平铺与偏移，
    /// 是因为合成结果随后会被贴到某个表面上，而那个表面自己也在用
    /// <c>UvScale</c> / <c>UvOffset</c> 平铺背景——
    /// 只有让两者的取样结果一致，镂空区域才会与被遮挡的表面严丝合缝。
    /// </para>
    /// <para>
    /// 若本贴图没有 alpha 通道，则原样返回自身。
    /// </para>
    /// </remarks>
    /// <param name="background">背景贴图。</param>
    /// <param name="backgroundTiling">背景在合成结果上的平铺次数。</param>
    /// <param name="backgroundOffset">背景的 UV 偏移。</param>
    public Texture2D Composite(Texture2D background, Vector2? backgroundTiling = null, Vector2? backgroundOffset = null)
    {
        ArgumentNullException.ThrowIfNull(background);
        if (_alpha is null)
        {
            return this;
        }

        Vector2 tiling = backgroundTiling ?? Vector2.One;
        Vector2 offset = backgroundOffset ?? Vector2.Zero;

        RgbColor[] result = new RgbColor[Width * Height];
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int index = (y * Width) + x;
                float a = _alpha[index];
                RgbColor foreground = _pixels[index];

                if (a >= 0.999f)
                {
                    result[index] = foreground;
                    continue;
                }

                if (a <= 0.001f)
                {
                    result[index] = SampleBackdrop(background, x, y, tiling, offset);
                    continue;
                }

                RgbColor backdrop = SampleBackdrop(background, x, y, tiling, offset);
                result[index] = backdrop.LerpTo(foreground, a);
            }
        }

        return new Texture2D(result, Width, Height, $"{Name}+{background.Name}")
        {
            FilterMode = FilterMode,
            AddressMode = AddressMode,
        };
    }

    /// <summary>按合成结果的像素坐标取样背景贴图。</summary>
    private RgbColor SampleBackdrop(Texture2D background, int x, int y, Vector2 tiling, Vector2 offset)
    {
        float u = (x + 0.5f) / Width;
        float v = (y + 0.5f) / Height;

        int bx = (int)(((u * tiling.X) + offset.X) * background.Width);
        int by = (int)(((v * tiling.Y) + offset.Y) * background.Height);

        bx = background.WrapX(bx);
        by = background.WrapY(by);

        return background._pixels[(by * background.Width) + bx];
    }

    private static int WrapIndex(int value, int size, int mask)
    {
        if (mask >= 0)
        {
            return value & mask;
        }

        int n = value % size;
        return n < 0 ? n + size : n;
    }

    /// <summary>把宽度或高度折算成「2 的幂减一」掩码；不是 2 的幂时返回 -1。</summary>
    private static int MaskOf(int size) => (size & (size - 1)) == 0 ? size - 1 : -1;

    /// <summary>
    /// 水平方向的下标折叠。
    /// </summary>
    /// <remarks>
    /// 折叠本来靠整数取模，但取模在像素循环里极贵：一次双线性采样要折四个下标，
    /// 一面铺满屏幕的墙就是几十万次除法。而代码生成的墙纸、地板、天花板尺寸
    /// 几乎总是 2 的幂（128×128、256×256），此时掩码位与的结果与取模完全一致，
    /// 且对任意大小的下标都成立——不需要调用方保证下标的偏离幅度。
    /// </remarks>
    private int WrapX(int value) => WrapIndex(value, Width, _widthMask);

    /// <summary>垂直方向的下标折叠，规则同 <see cref="WrapX"/>。</summary>
    private int WrapY(int value) => WrapIndex(value, Height, _heightMask);

    /// <inheritdoc />
    public override string ToString() => $"Texture2D('{Name}', {Width}×{Height}, tiling={Tiling})";
}
