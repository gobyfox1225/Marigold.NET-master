using System.Numerics;
using Marigold.Imaging;
using Marigold.Mathematics;
using Marigold.Rendering.Shading;
using Marigold.SceneGraph;

namespace Marigold.Rendering;

/// <summary>
/// 软件光栅化器：把屏幕空间三角形填充为像素。
/// </summary>
/// <remarks>
/// <para>
/// 补齐软件渲染器的核心：<b>重心坐标求交、透视校正插值、深度测试与背面剔除</b>。
/// </para>
/// <para>
/// <b>算法</b>：边函数（叉积）形式的半平面测试，天然支持增量计算与重心坐标同源。
/// </para>
/// <para>
/// <b>并行策略</b>：光栅化阶段把视口按屏幕行切成若干行带交给多线程并行填充。
/// 行带之间互不重叠，深度测试“更近者胜”，因此写帧缓冲无需加锁；
/// 统计计数用线程局部变量累加，帧末统一归并，避免热路径上的原子操作争用。
/// 注意：并行有固定开销，三角形数量少时（如几百个）应保持单线程，
/// 只有在分辨率高、三角形多时才值得开启。
/// </para>
/// </remarks>
public sealed class Rasterizer
{
    private readonly RenderSettings _settings;

    /// <summary>
    /// 走并行光栅化所需的最小包围盒像素数。
    /// </summary>
    /// <remarks>
    /// <c>Parallel.For</c> 本身的调度开销在几十微秒量级。一个房间的墙面、地面、天花板
    /// 都是铺满半个屏幕的大三角形，切分它们稳赚；而门框这类只有几百像素的小三角形
    /// 切分反而更慢。所以门槛按「包围盒有多大」来定，而不是按「有多少行」——
    /// 一行 320 像素的三角形和一行 4 像素的三角形，代价差两个数量级。
    /// </remarks>
    private const long ParallelPixelThreshold = 4096;

    private long _shadedPixels;
    private long _rejectedByDepth;
    private long _culledTriangles;
    private long _degenerateTriangles;
    private long _rasterizedTriangles;

    /// <summary>构造光栅化器。</summary>
    /// <param name="settings">渲染设置（剔除模式、深度测试等）。</param>
    public Rasterizer(RenderSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>被着色器处理过的片元总数（本次统计周期内）。</summary>
    public long ShadedPixelCount => _shadedPixels;

    /// <summary>被深度测试丢弃的片元数。</summary>
    public long RejectedByDepthCount => _rejectedByDepth;

    /// <summary>被背面剔除的三角形数。</summary>
    public long CulledTriangleCount => _culledTriangles;

    /// <summary>因退化被丢弃的三角形数。</summary>
    public long DegenerateTriangleCount => _degenerateTriangles;

    /// <summary>实际光栅化出的三角形数。</summary>
    public long RasterizedTriangleCount => _rasterizedTriangles;

    /// <summary>复位统计计数。</summary>
    public void ResetStatistics()
    {
        _shadedPixels = 0;
        _rejectedByDepth = 0;
        _culledTriangles = 0;
        _degenerateTriangles = 0;
        _rasterizedTriangles = 0;
    }

    /// <summary>
    /// 光栅化一个屏幕空间三角形。
    /// </summary>
    public bool DrawTriangle(
        IRenderTarget target,
        in ProjectedVertex v0,
        in ProjectedVertex v1,
        in ProjectedVertex v2,
        Material material,
        ISurfaceShader shader,
        in ShadingContext context)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(material);
        ArgumentNullException.ThrowIfNull(shader);

        if (!v0.IsFinite || !v1.IsFinite || !v2.IsFinite)
        {
            _degenerateTriangles++;
            return false;
        }

        // 边函数给出的二倍有符号面积（屏幕空间，Y 轴向下）。逆时针为正面时该值为负。
        float area = Edge(v0.ScreenX, v0.ScreenY, v1.ScreenX, v1.ScreenY, v2.ScreenX, v2.ScreenY);
        if (MathF.Abs(area) < 1e-8f)
        {
            _degenerateTriangles++;
            return false;
        }

        if (!ShouldDraw(area, v0, v1, v2, material, context))
        {
            _culledTriangles++;
            return false;
        }

        int minX = MathUtil.Clamp((int)MathF.Floor(MathF.Min(v0.ScreenX, MathF.Min(v1.ScreenX, v2.ScreenX))), 0, target.Width - 1);
        int maxX = MathUtil.Clamp((int)MathF.Ceiling(MathF.Max(v0.ScreenX, MathF.Max(v1.ScreenX, v2.ScreenX))), 0, target.Width - 1);
        int minY = MathUtil.Clamp((int)MathF.Floor(MathF.Min(v0.ScreenY, MathF.Min(v1.ScreenY, v2.ScreenY))), 0, target.Height - 1);
        int maxY = MathUtil.Clamp((int)MathF.Ceiling(MathF.Max(v0.ScreenY, MathF.Max(v1.ScreenY, v2.ScreenY))), 0, target.Height - 1);

        if (minX > maxX || minY > maxY)
        {
            _degenerateTriangles++;
            return false;
        }

        // 并行门槛：包围盒足够大才切分，避免小三角形承担调度开销
        long boundingPixels = (long)(maxY - minY + 1) * (maxX - minX + 1);
        int threadCount = _settings.RasterThreads;

        // 平面三角形的常量（法线、顶点色、着色器预计算）每个三角形只需算一次
        PlanarTriangle planar = PlanarTriangle.Measure(v0, v1, v2, material, shader, context);

        bool wroteAny;
        if (threadCount > 1 && boundingPixels >= ParallelPixelThreshold)
        {
            wroteAny = RasterizeParallel(target, v0, v1, v2, material, shader, context,
                minX, maxX, minY, maxY, area, threadCount, planar);
        }
        else
        {
            wroteAny = RasterizeRows(target, v0, v1, v2, material, shader, context,
                minX, maxX, minY, maxY, area, ref _shadedPixels, ref _rejectedByDepth, planar);
        }

        if (wroteAny)
        {
            _rasterizedTriangles++;
        }

        return wroteAny;
    }

    /// <summary>按行带并行光栅化，统计归并到帧级计数。</summary>
    private bool RasterizeParallel(
        IRenderTarget target,
        ProjectedVertex v0,
        ProjectedVertex v1,
        ProjectedVertex v2,
        Material material,
        ISurfaceShader shader,
        in ShadingContext context,
        int minX,
        int maxX,
        int minY,
        int maxY,
        float area,
        int threadCount,
        in PlanarTriangle planar)
    {
        int rowCount = maxY - minY + 1;
        int bands = Math.Min(threadCount, rowCount);
        int bandHeight = rowCount / bands;
        int remainder = rowCount % bands;

        // 每个线程一段行带；行带互不重叠，写帧缓冲无需加锁。
        long[] shadedPerBand = new long[bands];
        long[] rejectedPerBand = new long[bands];
        int[] wrotePerBand = new int[bands];

        // 复制成局部变量，避免 lambda 捕获 in 参数（CS1628）。
        ShadingContext ctx = context;
        PlanarTriangle flat = planar;

        System.Threading.Tasks.Parallel.For(0, bands, band =>
        {
            int start = minY + (band * bandHeight) + Math.Min(band, remainder);
            int end = start + bandHeight + (band < remainder ? 1 : 0) - 1;

            long localShaded = 0;
            long localRejected = 0;

            bool wrote = RasterizeRows(target, v0, v1, v2, material, shader, ctx,
                minX, maxX, start, end, area, ref localShaded, ref localRejected, flat);

            shadedPerBand[band] = localShaded;
            rejectedPerBand[band] = localRejected;
            wrotePerBand[band] = wrote ? 1 : 0;
        });

        bool wroteAny = false;
        for (int band = 0; band < bands; band++)
        {
            _shadedPixels += shadedPerBand[band];
            _rejectedByDepth += rejectedPerBand[band];
            wroteAny |= wrotePerBand[band] != 0;
        }

        return wroteAny;
    }

    /// <summary>填充三角形在 [rowStart, rowEnd] 行区间内的像素。</summary>
    /// <remarks>
    /// <para>
    /// <b>平面快路径</b>：本引擎的墙面、地面、天花板、门板全是平面四边形，
    /// 一个三角形的三个顶点共享同一个法线与同一个顶点色。
    /// 这种情况下法线与顶点色在三角形上是常量，逐像素插值纯属浪费——
    /// 而且插值出来的法线还要再归一化一次。所以循环外先判断「是否三点一致」，
    /// 一致就整块跳过插值，并把法线在循环外归一化好。
    /// </para>
    /// <para>
    /// 这不是为了省几个乘法，而是为了省掉逐像素的 <c>sqrt</c> 与除法：
    /// 在 Debug 构建下（无内联、无向量化）它们是像素循环里最贵的指令，
    /// 76800 个像素乘上过绘，足以把帧率压到个位数。
    /// </para>
    /// <para>
    /// 同一个常量包还会交给着色器的 <see cref="ISurfaceShader.Prepare"/> 预计算，
    /// 于是光照也在循环外完成——循环里只剩纹理采样与雾效。
    /// </para>
    /// </remarks>
    private bool RasterizeRows(
        IRenderTarget target,
        in ProjectedVertex v0,
        in ProjectedVertex v1,
        in ProjectedVertex v2,
        Material material,
        ISurfaceShader shader,
        in ShadingContext context,
        int minX,
        int maxX,
        int rowStart,
        int rowEnd,
        float area,
        ref long shadedCounter,
        ref long rejectedCounter,
        in PlanarTriangle planar)
    {
        float inverseArea = 1.0f / area;
        bool acceptPositiveSide = area > 0.0f;

        Span<float> depthSpan = target.DepthPixels;
        Span<RgbColor> colorSpan = target.ColorPixels;
        int width = target.Width;

        bool flatNormal = planar.NormalIsFlat;
        bool flatColor = planar.ColorIsFlat;
        Vector3 normal = planar.Normal;
        RgbColor vertexColor = planar.VertexColor;
        ShadingConstants constants = planar.Constants;

        // 三条边函数在屏幕空间都是 x 的一次函数：w(x) = slope·x + intercept(y)。
        // 于是每行可以解析地求出覆盖区间，不必对包围盒里每个像素做三次半平面测试；
        // 区间内的重心坐标还能用加法递推，把逐像素的乘法也一并省掉。
        // intercept(y) = alongX·y + origin，其中 alongX 与 origin 只跟三角形自身有关。
        float slope0 = v1.ScreenY - v2.ScreenY;
        float slope1 = v2.ScreenY - v0.ScreenY;
        float slope2 = v0.ScreenY - v1.ScreenY;

        float alongX0 = v2.ScreenX - v1.ScreenX;
        float origin0 = ((v2.ScreenY - v1.ScreenY) * v1.ScreenX) - (alongX0 * v1.ScreenY);
        float alongX1 = v0.ScreenX - v2.ScreenX;
        float origin1 = ((v0.ScreenY - v2.ScreenY) * v2.ScreenX) - (alongX1 * v2.ScreenY);
        float alongX2 = v1.ScreenX - v0.ScreenX;
        float origin2 = ((v1.ScreenY - v0.ScreenY) * v0.ScreenX) - (alongX2 * v0.ScreenY);

        // 统一把约束化成「边函数 ≥ 0」：背面朝内的三角形把三条边一起取反
        float sign = acceptPositiveSide ? 1.0f : -1.0f;

        // 区间边界以「采样点 x」为单位，初始范围就是包围盒本身
        float boundLow = minX - 0.5f;
        float boundHigh = maxX + 0.5f;

        bool wroteAny = false;

        for (int y = rowStart; y <= rowEnd; y++)
        {
            float sampleY = y + 0.5f;

            float lo = boundLow;
            float hi = boundHigh;

            if (!ClipRow(sign * slope0, sign * ((alongX0 * sampleY) + origin0), ref lo, ref hi) ||
                !ClipRow(sign * slope1, sign * ((alongX1 * sampleY) + origin1), ref lo, ref hi) ||
                !ClipRow(sign * slope2, sign * ((alongX2 * sampleY) + origin2), ref lo, ref hi))
            {
                continue;
            }

            int xStart = Math.Max(minX, (int)MathF.Ceiling(lo - 0.5f));
            int xEnd = Math.Min(maxX, (int)MathF.Floor(hi - 0.5f));
            if (xStart > xEnd)
            {
                continue;
            }

            float sampleX = xStart + 0.5f;

            float l0 = ((slope0 * sampleX) + (alongX0 * sampleY) + origin0) * inverseArea;
            float l1 = ((slope1 * sampleX) + (alongX1 * sampleY) + origin1) * inverseArea;
            float l2 = 1.0f - l0 - l1;

            float stepL0 = slope0 * inverseArea;
            float stepL1 = slope1 * inverseArea;
            float stepL2 = -stepL0 - stepL1;

            // 深度与 1/w 都是屏幕空间线性的，同样可以整行递推
            float depth = (l0 * v0.Depth) + (l1 * v1.Depth) + (l2 * v2.Depth);
            float invW = (l0 * v0.InvW) + (l1 * v1.InvW) + (l2 * v2.InvW);

            float stepDepth = (stepL0 * v0.Depth) + (stepL1 * v1.Depth) + (stepL2 * v2.Depth);
            float stepInvW = (stepL0 * v0.InvW) + (stepL1 * v1.InvW) + (stepL2 * v2.InvW);

            int index = (y * width) + xStart;

            for (int x = xStart;
                 x <= xEnd;
                 x++, index++, l0 += stepL0, l1 += stepL1, l2 += stepL2, depth += stepDepth, invW += stepInvW)
            {
                if (_settings.DepthTest && depth >= depthSpan[index])
                {
                    rejectedCounter++;
                    continue;
                }

                if (invW <= 0.0f)
                {
                    continue;
                }

                float w = 1.0f / invW;
                float b0 = l0 * v0.InvW * w;
                float b1 = l1 * v1.InvW * w;
                float b2 = 1.0f - b0 - b1;

                Vector3 worldPosition =
                    (v0.WorldPosition * b0) + (v1.WorldPosition * b1) + (v2.WorldPosition * b2);

                SurfaceFragment fragment = new(
                    worldPosition,
                    flatNormal ? normal : (v0.WorldNormal * b0) + (v1.WorldNormal * b1) + (v2.WorldNormal * b2),
                    (v0.TexCoord * b0) + (v1.TexCoord * b1) + (v2.TexCoord * b2),
                    flatColor ? vertexColor : InterpolateColor(v0.Color, v1.Color, v2.Color, b0, b1, b2),
                    material,
                    context);

                RgbColor src = shader.Shade(fragment, constants);

                // 按材质混合模式决定写入方式
                switch (material.BlendMode)
                {
                    case BlendMode.AlphaTest:
                        // alpha 低于阈值的片元直接丢弃
                        if (src.A < material.AlphaCutoff)
                        {
                            continue;
                        }
                        colorSpan[index] = src;
                        break;

                    case BlendMode.AlphaBlend:
                        // over 运算：dst = src*srcA + dst*(1-srcA)
                        // 材质 Opacity 与片元 alpha 相乘得到最终覆盖度
                        float srcA = MathUtil.Clamp01(src.A * material.Opacity);
                        if (srcA <= 0.0f)
                        {
                            continue;
                        }
                        RgbColor dst = colorSpan[index];
                        colorSpan[index] = new RgbColor(
                            (src.R * srcA) + (dst.R * (1.0f - srcA)),
                            (src.G * srcA) + (dst.G * (1.0f - srcA)),
                            (src.B * srcA) + (dst.B * (1.0f - srcA)),
                            1.0f);
                        break;

                    default:
                    case BlendMode.Opaque:
                        colorSpan[index] = src;
                        break;
                }

                // 半透模式关闭深度写（仍测试），让后绘的半透片元正确叠加
                if (_settings.DepthWrite && material.DepthWrite && material.BlendMode != BlendMode.AlphaBlend)
                {
                    depthSpan[index] = depth;
                }

                shadedCounter++;
                wroteAny = true;
            }
        }

        return wroteAny;
    }

    /// <summary>
    /// 按一条边的半平面约束收窄本行的 x 区间。
    /// </summary>
    /// <param name="slope">边函数对 x 的斜率（已按内外侧取好符号）。</param>
    /// <param name="intercept">边函数在 <c>x = 0</c> 处的值（已按内外侧取好符号）。</param>
    /// <param name="low">当前下界，会被收窄。</param>
    /// <param name="high">当前上界，会被收窄。</param>
    /// <returns>收窄后区间是否仍然非空。</returns>
    /// <remarks>
    /// 约束形式统一为 <c>slope·x + intercept ≥ 0</c>：
    /// 斜率为正给出下界，斜率为负给出上界，斜率为零时整行要么全在里要么全在外。
    /// 这样每行只有三次除法，而省下的是区间外每一个像素的三次半平面测试。
    /// </remarks>
    private static bool ClipRow(float slope, float intercept, ref float low, ref float high)
    {
        if (slope > 0.0f)
        {
            float limit = -intercept / slope;
            if (limit > low)
            {
                low = limit;
            }
        }
        else if (slope < 0.0f)
        {
            float limit = -intercept / slope;
            if (limit < high)
            {
                high = limit;
            }
        }
        else if (intercept < 0.0f)
        {
            return false;
        }

        return low <= high;
    }

    /// <summary>
    /// 一个三角形上「整块不变」的着色输入。
    /// </summary>
    /// <remarks>
    /// 由 <see cref="DrawTriangle"/> 在进入像素循环之前算好，逐行带共享。
    /// 因为整个三角形共用一份，所以它同时是并行行带的输入——
    /// 每个行带不必再算一遍法线归一化与光照。
    /// </remarks>
    private readonly struct PlanarTriangle
    {
        /// <summary>三个顶点的法线是否一致。为真时 <see cref="Normal"/> 已是单位向量。</summary>
        public readonly bool NormalIsFlat;

        /// <summary>三个顶点的顶点色是否一致。</summary>
        public readonly bool ColorIsFlat;

        /// <summary>常量法线。</summary>
        public readonly Vector3 Normal;

        /// <summary>常量顶点色。</summary>
        public readonly RgbColor VertexColor;

        /// <summary>着色器预计算出的常量包；<c>Valid</c> 为假时着色器会自行逐像素重算。</summary>
        public readonly ShadingConstants Constants;

        private PlanarTriangle(
            bool normalIsFlat,
            bool colorIsFlat,
            Vector3 normal,
            RgbColor vertexColor,
            in ShadingConstants constants)
        {
            NormalIsFlat = normalIsFlat;
            ColorIsFlat = colorIsFlat;
            Normal = normal;
            VertexColor = vertexColor;
            Constants = constants;
        }

        /// <summary>测量一个三角形，并在平面三角形上顺带把着色器的常量算出来。</summary>
        public static PlanarTriangle Measure(
            in ProjectedVertex v0,
            in ProjectedVertex v1,
            in ProjectedVertex v2,
            Material material,
            ISurfaceShader shader,
            in ShadingContext context)
        {
            bool flatNormal =
                v0.WorldNormal == v1.WorldNormal && v1.WorldNormal == v2.WorldNormal;
            bool flatColor =
                v0.Color.R == v1.Color.R && v0.Color.G == v1.Color.G && v0.Color.B == v1.Color.B &&
                v1.Color.R == v2.Color.R && v1.Color.G == v2.Color.G && v1.Color.B == v2.Color.B;

            Vector3 normal = flatNormal ? NormalizeOrUp(v0.WorldNormal) : default;
            RgbColor vertexColor = flatColor ? v0.Color : default;

            ShadingConstants constants = ShadingConstants.None;
            if (flatNormal && flatColor)
            {
                // 采样片元只用来读取「整块不变」的那部分输入：材质、法线、顶点色。
                SurfaceFragment sample = new(
                    v0.WorldPosition, normal, v0.TexCoord, vertexColor, material, context);

                constants = shader.Prepare(sample);
            }

            return new PlanarTriangle(flatNormal, flatColor, normal, vertexColor, constants);
        }
    }

    /// <summary>归一化一个向量；长度过小时回退到 +Y。</summary>
    private static Vector3 NormalizeOrUp(Vector3 value)
    {
        float lengthSquared = value.LengthSquared();
        return lengthSquared > float.Epsilon ? value / MathF.Sqrt(lengthSquared) : Vector3.UnitY;
    }

    /// <summary>边函数：返回点相对有向边 (ax,ay)→(bx,by) 的位置。</summary>
    private static float Edge(float ax, float ay, float bx, float by, float px, float py) =>
        ((bx - ax) * (py - ay)) - ((by - ay) * (px - ax));

    /// <summary>根据剔除模式判断三角形是否应当绘制。</summary>
    private bool ShouldDraw(
        float screenArea,
        in ProjectedVertex v0,
        in ProjectedVertex v1,
        in ProjectedVertex v2,
        Material material,
        in ShadingContext context)
    {
        if (material.DoubleSided)
        {
            return true;
        }

        switch (_settings.CullMode)
        {
            case CullMode.None:
                return true;

            case CullMode.FrontFace:
                return screenArea >= 0.0f;

            case CullMode.BackFaceByNormal:
                {
                    Vector3 normal = Vector3.Cross(
                        v1.WorldPosition - v0.WorldPosition,
                        v2.WorldPosition - v0.WorldPosition);

                    Vector3 toCamera = context.CameraPosition - v0.WorldPosition;
                    return Vector3.Dot(normal, toCamera) > 0.0f;
                }

            case CullMode.BackFace:
            default:
                return screenArea < 0.0f;
        }
    }

    /// <summary>按重心坐标插值顶点色（含 alpha）。</summary>
    private static RgbColor InterpolateColor(RgbColor c0, RgbColor c1, RgbColor c2, float b0, float b1, float b2) =>
        new(
            (c0.R * b0) + (c1.R * b1) + (c2.R * b2),
            (c0.G * b0) + (c1.G * b1) + (c2.G * b2),
            (c0.B * b0) + (c1.B * b1) + (c2.B * b2),
            (c0.A * b0) + (c1.A * b1) + (c2.A * b2));

    /// <inheritdoc />
    public override string ToString() =>
        $"Rasterizer(cull={_settings.CullMode}, depthTest={_settings.DepthTest}, threads={_settings.RasterThreads}, drawn={RasterizedTriangleCount}, pixels={ShadedPixelCount})";
}
