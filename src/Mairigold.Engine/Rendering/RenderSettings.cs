using System.Numerics;
using Marigold.Imaging;
using Marigold.Mathematics;

namespace Marigold.Rendering;

/// <summary>
/// 单盏方向光：颜色、强度与照射方向。
/// </summary>
/// <remarks>
/// 方向光无位置，只有「光线传播方向」。着色时取反得到「由表面指向光源」的方向。
/// </remarks>
public readonly struct DirectionalLight
{
    /// <summary>光的颜色。</summary>
    public readonly RgbColor Color;

    /// <summary>光强度（与颜色相乘得到能量）。</summary>
    public readonly float Intensity;

    /// <summary>光线传播方向（从光源指向场景），约定为单位向量。</summary>
    public readonly Vector3 Direction;

    /// <summary>构造方向光。</summary>
    /// <param name="color">光的颜色。</param>
    /// <param name="intensity">光强度。</param>
    /// <param name="direction">光线传播方向，将内部归一化。</param>
    public DirectionalLight(RgbColor color, float intensity, Vector3 direction)
    {
        Color = color;
        Intensity = intensity;
        Direction = Vector3.Normalize(direction);
    }

    /// <summary>由表面指向光源的方向（即 <see cref="Direction"/> 的反向）。</summary>
    public Vector3 DirectionToLight => -Direction;

    /// <summary>光的能量贡献（颜色 × 强度）。</summary>
    public RgbColor Term => Color * Intensity;

    /// <inheritdoc />
    public override string ToString() => $"DirLight(int={Intensity}, dir={Direction})";
}

/// <summary>三角形剔除模式。</summary>
public enum CullMode
{
    /// <summary>不剔除，全部绘制（调试与双面几何使用）。</summary>
    None = 0,

    /// <summary>
    /// 按屏幕空间绕序剔除背面。
    /// </summary>
    /// <remarks>
    /// 行业标准做法，开销最低。要求网格严格遵循“逆时针为正面”的绕序约定。
    /// </remarks>
    BackFace = 1,

    /// <summary>按屏幕空间绕序剔除正面（用于观察建筑内部结构）。</summary>
    FrontFace = 2,

    /// <summary>
    /// 按世界空间法线与视线方向的夹角剔除背面。
    /// </summary>
    /// <remarks>
    /// 比绕序判断稳健：即使网格绕序不一致，只要法线朝外就能得到正确结果。
    /// 代价是需要用到片元的法线数据，且对非封闭网格（薄片）不适用。
    /// </remarks>
    BackFaceByNormal = 3,
}

/// <summary>雾的计算方式。</summary>
public enum FogMode
{
    /// <summary>线性雾：在 [Start, End] 之间线性过渡。</summary>
    Linear = 0,

    /// <summary>指数雾：<c>1 - exp(-density × distance)</c>。</summary>
    Exponential = 1,

    /// <summary>指数平方雾：<c>1 - exp(-(density × distance)²)</c>，远处浓度上升更慢。</summary>
    ExponentialSquared = 2,
}

/// <summary>
/// 雾效设置。
/// </summary>
/// <remarks>
/// <para>
/// 雾在本项目里不是为了天气，而是<b>核心美术手段</b>。渲染器的“无限”错觉
/// 一半来自房间复用，另一半来自这层白雾：它让走廊尽头溶进纯白背景，
/// 玩家看不到任何墙面边界，大脑就无法判断走廊究竟有多长。
/// </para>
/// <para>
/// 使用密度 0.035 的纯白雾色作为默认值。
/// </para>
/// </remarks>
public sealed class FogSettings
{
    /// <summary>是否启用雾。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>雾的计算方式。</summary>
    public FogMode Mode { get; set; } = FogMode.Exponential;

    /// <summary>雾色。引擎 使用纯白，与背景色一致。</summary>
    public RgbColor Color { get; set; } = RgbColor.White;

    /// <summary>雾浓度。指数模式下的密度系数。</summary>
    public float Density { get; set; } = 0.035f;

    /// <summary>线性雾的起始距离。</summary>
    public float Start { get; set; } = 0.0f;

    /// <summary>线性雾的结束距离。</summary>
    public float End { get; set; } = 40.0f;

    /// <summary>
    /// 计算指定距离处的雾浓度（0 = 完全清晰，1 = 完全被雾吞没）。
    /// </summary>
    public float Evaluate(float distance)
    {
        if (!Enabled || distance <= 0.0f)
        {
            return 0.0f;
        }

        return Mode switch
        {
            FogMode.Linear => MathUtil.Clamp01(RemapDistance(distance)),
            FogMode.ExponentialSquared => 1.0f - MathF.Exp(-(Density * distance) * (Density * distance)),
            _ => 1.0f - MathF.Exp(-Density * distance),
        };
    }

    /// <summary>把雾叠加到颜色上。</summary>
    public RgbColor Apply(RgbColor surfaceColor, float distance)
    {
        float amount = Evaluate(distance);
        return amount <= 0.0f ? surfaceColor : surfaceColor.LerpTo(Color, amount);
    }

    /// <summary>复制一份设置。</summary>
    public FogSettings Clone() => new()
    {
        Enabled = Enabled,
        Mode = Mode,
        Color = Color,
        Density = Density,
        Start = Start,
        End = End,
    };

    private float RemapDistance(float distance)
    {
        float span = End - Start;
        if (span <= MathUtil.Epsilon)
        {
            return distance >= End ? 1.0f : 0.0f;
        }

        return (distance - Start) / span;
    }
}

/// <summary>
/// 光照设置：环境光 + 主方向光 + 可选附加方向光列表。
/// </summary>
/// <remarks>
/// <para>
/// 主方向光保留为快捷字段（<see cref="SunColor"/> / <see cref="SunIntensity"/> / <see cref="SunDirection"/>），
/// 适合单光源场景。需要多光源时把附加方向光加入 <see cref="AdditionalLights"/>，
/// 着色器会累加主光与附加光的朗伯贡献。
/// </para>
/// <para>
/// <see cref="DirectionalLight"/> 是值类型，列表为空时无额外开销。
/// </para>
/// </remarks>
public sealed class LightingSettings
{
    /// <summary>环境光颜色。</summary>
    public RgbColor AmbientColor { get; set; } = RgbColor.White;

    /// <summary>环境光强度，默认 0.3。</summary>
    public float AmbientIntensity { get; set; } = 0.3f;

    /// <summary>主方向光颜色。</summary>
    public RgbColor SunColor { get; set; } = RgbColor.White;

    /// <summary>主方向光强度，默认 0.7。</summary>
    public float SunIntensity { get; set; } = 0.7f;

    /// <summary>
    /// 主方向光的<b>照射方向</b>（光线传播方向，即从光源指向场景）。
    /// </summary>
    /// <remarks>
    /// 默认值为 55° 俯角姿态：光从上方略偏前的位置射下。
    /// 着色时取反得到“由表面指向光源”的方向向量。
    /// </remarks>
    public Vector3 SunDirection { get; set; } = Vector3.Normalize(new Vector3(0.0f, -0.8192f, 0.5736f));

    /// <summary>附加方向光列表。着色时在主光之后逐盏累加朗伯贡献。</summary>
    /// <remarks>默认为空列表。列表本身可变，但元素为值类型，遍历无装箱开销。</remarks>
    public List<DirectionalLight> AdditionalLights { get; set; } = new();

    /// <summary>由表面指向主光源的方向（即 <see cref="SunDirection"/> 的反向）。</summary>
    public Vector3 DirectionToSun => -SunDirection;

    /// <summary>环境光贡献量。</summary>
    public RgbColor AmbientTerm => AmbientColor * AmbientIntensity;

    /// <summary>主方向光贡献量。</summary>
    public RgbColor SunTerm => SunColor * SunIntensity;

    /// <summary>把一盏附加方向光添加到 <see cref="AdditionalLights"/>。</summary>
    public LightingSettings AddLight(RgbColor color, float intensity, Vector3 direction)
    {
        AdditionalLights.Add(new DirectionalLight(color, intensity, direction));
        return this;
    }

    /// <summary>复制一份设置（含附加光源的浅拷贝）。</summary>
    public LightingSettings Clone() => new()
    {
        AmbientColor = AmbientColor,
        AmbientIntensity = AmbientIntensity,
        SunColor = SunColor,
        SunIntensity = SunIntensity,
        SunDirection = SunDirection,
        AdditionalLights = new List<DirectionalLight>(AdditionalLights),
    };
}

/// <summary>
/// 渲染器设置：光栅化状态、剔除、光照与雾的集合。
/// </summary>
public sealed class RenderSettings
{
    /// <summary>三角形剔除模式。</summary>
    public CullMode CullMode { get; set; } = CullMode.BackFace;

    /// <summary>是否启用深度测试（关闭后后画的三角形会覆盖先画的）。</summary>
    public bool DepthTest { get; set; } = true;

    /// <summary>是否写入深度缓冲。</summary>
    public bool DepthWrite { get; set; } = true;

    /// <summary>是否启用逐对象的视锥剔除。</summary>
    public bool ObjectCulling { get; set; } = true;

    /// <summary>清屏色。引擎 使用纯白。</summary>
    public RgbColor BackgroundColor { get; set; } = RgbColor.White;

    /// <summary>光照设置。</summary>
    public LightingSettings Lighting { get; set; } = new();

    /// <summary>雾效设置。</summary>
    public FogSettings Fog { get; set; } = new();

    /// <summary>是否统计渲染数据（关闭可省下少量统计开销）。</summary>
    public bool CollectStatistics { get; set; } = true;

    /// <summary>
    /// 并行光栅化使用的线程数。1 表示单线程（默认，小场景最快）；
    /// 大于 1 时启用 <c>Parallel.For</c> 按屏幕行带并行填充，仅在三角形多、
    /// 分辨率高时才有收益。并行有固定开销，场景太小反而更慢。
    /// </summary>
    public int RasterThreads { get; set; } = 1;

    /// <summary>
    /// 生成一套中性默认设置：中灰背景、白光、无雾、单线程。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 通用渲染器的中性起点：不预设任何美术风格，由调用方按需调整。
    /// 光照取环境 0.3 + 主方向光 0.7，地面辐照度约 0.87 不溢出，
    /// 墙面 0.3，二者有合理反差，便于直接看到几何结构。
    /// </para>
    /// <para>
    /// 若需要多核并行，把 <see cref="RasterThreads"/> 设为大于 1 的值。
    /// </para>
    /// </remarks>
    public static RenderSettings CreateDefault() => new()
    {
        CullMode = CullMode.BackFace,
        DepthTest = true,
        DepthWrite = true,
        ObjectCulling = true,
        BackgroundColor = new RgbColor(0.15f, 0.15f, 0.18f),
        RasterThreads = Math.Max(1, Environment.ProcessorCount - 1),
        Lighting = new LightingSettings
        {
            AmbientColor = RgbColor.White,
            AmbientIntensity = 0.3f,
            SunColor = RgbColor.White,
            SunIntensity = 0.7f,
            SunDirection = Vector3.Normalize(new Vector3(0.0f, -0.8192f, 0.5736f)),
        },
        Fog = new FogSettings
        {
            Enabled = false,
            Mode = FogMode.Exponential,
            Color = RgbColor.Black,
            Density = 0.02f,
        },
    };

    /// <summary>复制一份设置（含光照与雾的深拷贝）。</summary>
    public RenderSettings Clone() => new()
    {
        CullMode = CullMode,
        DepthTest = DepthTest,
        DepthWrite = DepthWrite,
        ObjectCulling = ObjectCulling,
        BackgroundColor = BackgroundColor,
        CollectStatistics = CollectStatistics,
        RasterThreads = RasterThreads,
        Lighting = Lighting.Clone(),
        Fog = Fog.Clone(),
    };

    /// <inheritdoc />
    public override string ToString() =>
        $"RenderSettings(cull={CullMode}, fog={Fog.Mode}/{Fog.Density}, ambient={Lighting.AmbientIntensity}, sun={Lighting.SunIntensity})";
}
