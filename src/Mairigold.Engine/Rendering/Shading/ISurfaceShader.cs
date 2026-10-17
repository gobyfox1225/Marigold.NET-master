using System.Numerics;
using Marigold.Imaging;
using Marigold.SceneGraph;

namespace Marigold.Rendering.Shading;

/// <summary>
/// 着色上下文：一次渲染过程中保持不变的全局参数。
/// </summary>
/// <remarks>
/// 以 <c>readonly struct</c> 传递，避免每个片元都通过参数列表搬运一大堆字段。
/// </remarks>
public readonly struct ShadingContext
{
    /// <summary>相机（眼点）世界坐标。</summary>
    public readonly Vector3 CameraPosition;

    /// <summary>光照设置。</summary>
    public readonly LightingSettings Lighting;

    /// <summary>雾效设置。</summary>
    public readonly FogSettings Fog;

    /// <summary>构造着色上下文。</summary>
    public ShadingContext(Vector3 cameraPosition, LightingSettings lighting, FogSettings fog)
    {
        CameraPosition = cameraPosition;
        Lighting = lighting;
        Fog = fog;
    }

    /// <summary>某个世界坐标点到相机的距离。</summary>
    public readonly float DistanceToCamera(Vector3 worldPosition) => (worldPosition - CameraPosition).Length();

    /// <summary>由表面指向相机的单位向量。</summary>
    public readonly Vector3 DirectionToCamera(Vector3 worldPosition)
    {
        Vector3 direction = CameraPosition - worldPosition;
        float lengthSquared = direction.LengthSquared();
        return lengthSquared > float.Epsilon ? direction / MathF.Sqrt(lengthSquared) : Vector3.UnitZ;
    }
}

/// <summary>
/// 单个片元（候选像素）的全部着色输入。
/// </summary>
/// <remarks>
/// 属性由光栅化器做<b>透视校正插值</b>后填入：先对 <c>属性 × 1/w</c> 做屏幕空间线性插值，
/// 再除以插值得到的 <c>1/w</c>。若直接用屏幕重心坐标插值世界坐标，
/// 近大远小的纹理与光照都会出现明显扭曲——这是手写光栅器最经典的错误来源。
/// </remarks>
public readonly struct SurfaceFragment
{
    /// <summary>插值后的世界空间位置。</summary>
    public readonly Vector3 WorldPosition;

    /// <summary>插值后的世界空间法线（未归一化）。</summary>
    public readonly Vector3 WorldNormal;

    /// <summary>插值后的纹理坐标。</summary>
    public readonly Vector2 TexCoord;

    /// <summary>插值后的顶点色。</summary>
    public readonly RgbColor VertexColor;

    /// <summary>当前片元所属的材质。</summary>
    public readonly Material Material;

    /// <summary>着色上下文。</summary>
    public readonly ShadingContext Context;

    /// <summary>构造片元。</summary>
    public SurfaceFragment(
        Vector3 worldPosition,
        Vector3 worldNormal,
        Vector2 texCoord,
        RgbColor vertexColor,
        Material material,
        in ShadingContext context)
    {
        WorldPosition = worldPosition;
        WorldNormal = worldNormal;
        TexCoord = texCoord;
        VertexColor = vertexColor;
        Material = material;
        Context = context;
    }

    /// <summary>片元到相机的距离，用于雾计算。</summary>
    public float DistanceToCamera => Context.DistanceToCamera(WorldPosition);

    /// <summary>归一化后的世界法线。若插值结果退化，则回退到 +Y。</summary>
    /// <remarks>
    /// 光栅化器在平面三角形上会先把法线归一化好再传进来，
    /// 那时这里的 <c>lengthSquared</c> 已经是 1。多一个「本来就是单位向量」的短路判断，
    /// 就能省掉逐像素的一次开方与三次除法——这是像素循环里最贵的几条指令。
    /// </remarks>
    public Vector3 NormalizedNormal
    {
        get
        {
            float lengthSquared = WorldNormal.LengthSquared();

            if (MathF.Abs(lengthSquared - 1.0f) < 1e-4f)
            {
                return WorldNormal;
            }

            return lengthSquared > float.Epsilon
                ? WorldNormal / MathF.Sqrt(lengthSquared)
                : Vector3.UnitY;
        }
    }

    /// <summary>由表面指向相机的单位向量。</summary>
    public Vector3 ViewDirection => Context.DirectionToCamera(WorldPosition);

    /// <summary>材质的反照率与顶点色相乘后的最终基础色（alpha 取顶点色 alpha）。</summary>
    public RgbColor Albedo
    {
        get
        {
            RgbColor m = Material.Albedo;
            RgbColor v = VertexColor;
            // 反照率只关心 RGB 相乘；alpha 由顶点色决定，材质的覆盖度由 Opacity 单独表达
            return new RgbColor(m.R * v.R, m.G * v.G, m.B * v.B, v.A);
        }
    }
}

/// <summary>
/// 一个三角形上<b>不随像素变化</b>的着色输入。
/// </summary>
/// <remarks>
/// <para>
/// 软件光栅化的性能几乎全压在像素循环里，而循环里最贵的东西往往与像素无关。
/// 本引擎的墙、地、顶、门板都是平面四边形：一个三角形的三个顶点共享同一个法线、
/// 同一个顶点色，材质也是同一个。也就是说<b>法线、反照率、光照贡献在整块上是常量</b>，
/// 逐像素重算它们纯属浪费，而且算出来的法线还要再归一化一次。
/// </para>
/// <para>
/// 把这一坨提到每个三角形算一次，是这套渲染器能不能跑到 60 帧的分水岭：
/// 循环里就只剩纹理采样与雾效这两件真正逐像素的事。
/// </para>
/// <para>
/// 光的<b>方向</b>仍然是逐像素的（<see cref="SurfaceFragment.ViewDirection"/> 等），
/// 所以法线偏移、双面翻转这类依赖视线的效果不会走快路径——
/// 它们会让 <see cref="ISurfaceShader.Prepare"/> 返回 <see cref="None"/>。
/// </para>
/// </remarks>
public readonly struct ShadingConstants
{
    /// <summary>是否可用。为 <c>false</c> 时着色器必须走逐像素的通用路径。</summary>
    public readonly bool Valid;

    /// <summary>三角形上的常量法线（已归一化）。</summary>
    public readonly Vector3 Normal;

    /// <summary>环境光与方向光合成的辐照度。</summary>
    public readonly RgbColor Radiance;

    /// <summary>材质的反照率与常量顶点色相乘的结果（不含贴图）。</summary>
    public readonly RgbColor Albedo;

    /// <summary>构造常量包。</summary>
    /// <param name="normal">已归一化的法线。</param>
    /// <param name="radiance">合成辐照度。</param>
    /// <param name="albedo">不含贴图的反照率。</param>
    public ShadingConstants(Vector3 normal, RgbColor radiance, RgbColor albedo)
    {
        Valid = true;
        Normal = normal;
        Radiance = radiance;
        Albedo = albedo;
    }

    /// <summary>「没有常量可用」，表示请走逐像素通用路径。</summary>
    public static ShadingConstants None => default;
}

/// <summary>
/// 表面着色器：把片元输入变成最终像素颜色。
/// </summary>
/// <remarks>
/// <para>
/// 把着色逻辑抽成接口而不是写死在光栅化器里，是为了让“光照模型”成为可替换的插件：
/// 本工程附带朗伯漫反射与无光照两种实现；要加描边、卡通着色或程序化条纹，
/// 只需新增一个实现类，不必碰光栅化代码。
/// </para>
/// <para>
/// <b>两段式接口</b>：<see cref="Prepare"/> 每个三角形调一次，算不随像素变化的量；
/// <see cref="Shade"/> 每个像素调一次，只做真正逐像素的事。
/// 光栅化器只在确认三角形是平面（三点法线与顶点色都一致）时才调用
/// <see cref="Prepare"/>，否则直接传 <see cref="ShadingConstants.None"/>。
/// 因此实现者<b>必须</b>在 <see cref="Shade"/> 里处理 <c>Valid == false</c> 的情况。
/// </para>
/// <para>
/// <b>实现约定</b>：
/// </para>
/// <list type="bullet">
/// <item><description>返回值为<b>线性空间</b>颜色，可以大于 1（过曝），由输出阶段统一钳制与编码。</description></item>
/// <item><description>实现应当是<b>无状态</b>且线程安全的：同一实例会被多个片元并发或连续复用。</description></item>
/// <item><description>不要在实现里读写帧缓冲；那是光栅化器的职责。</description></item>
/// </list>
/// </remarks>
public interface ISurfaceShader
{
    /// <summary>着色器名称，用于调试与统计。</summary>
    string Name { get; }

    /// <summary>
    /// 每个三角形调用一次：把不随像素变化的量先算好。
    /// </summary>
    /// <param name="sample">三角形上的一个样本片元，用于读取材质与常量几何。</param>
    /// <returns>
    /// 常量包。返回 <see cref="ShadingConstants.None"/> 表示这个三角形不适合走快路径，
    /// 光栅化器会照常逐像素调用 <see cref="Shade"/>。
    /// </returns>
    /// <remarks>
    /// 默认实现一律返回 <see cref="ShadingConstants.None"/>，
    /// 于是「不实现它」与「没有这个接口」的行为完全一致——新增着色器不会被它绊住。
    /// </remarks>
    ShadingConstants Prepare(in SurfaceFragment sample) => ShadingConstants.None;

    /// <summary>计算一个片元的最终颜色。</summary>
    /// <param name="fragment">片元输入。</param>
    /// <param name="constants">
    /// <see cref="Prepare"/> 的结果。若 <c>Valid</c> 为 <c>false</c>，实现必须忽略它并自行算齐全部输入。
    /// </param>
    /// <returns>线性空间颜色。</returns>
    RgbColor Shade(in SurfaceFragment fragment, in ShadingConstants constants);
}
