using Marigold.Imaging;

namespace Marigold.SceneGraph;

/// <summary>材质的光照模型。</summary>
public enum MaterialShadingModel
{
    /// <summary>无光照：直接输出反照率，用于 UI、天空盒或调试可视化。</summary>
    Unlit = 0,

    /// <summary>朗伯漫反射 + 环境光。</summary>
    Lambert = 1,
}

/// <summary>材质的透明混合模式。</summary>
public enum BlendMode
{
    /// <summary>不透明：颜色直接写入，深度正常测试与写入。</summary>
    Opaque = 0,

    /// <summary>Alpha 混合：按源 alpha 与目标颜色做 over 运算，关闭深度写入。</summary>
    AlphaBlend = 1,

    /// <summary>Alpha 测试：alpha 低于阈值的片元直接丢弃，不混合；深度照常写入。</summary>
    /// <remarks>适合带硬边的镂空贴图（栅栏、树叶），无半透明边缘但能正确遮挡。</remarks>
    AlphaTest = 2,
}

/// <summary>
/// 表面材质：反照率 + 着色模型 + 少量渲染状态。
/// </summary>
/// <remarks>
/// <para>
/// 把墙面、门框、门板等材质的取值整理成可复用、
/// 可命名、可序列化的对象，而不是每次都重新构造一个。
/// </para>
/// <para>
/// <b>为什么把材质与网格分离</b>：渲染管线中同一份立方体网格会被复用于地板、
/// 墙体、门框、门板等几十个位置。如果让每个网格实例各自持有一份材质覆盖，
/// 材质对象会被重复创建几十次；分离之后可以共享引用，
/// 也让渲染器的状态切换（同类材质连续绘制）成为可能。
/// </para>
/// </remarks>
public sealed class Material
{
    /// <summary>构造材质。</summary>
    /// <param name="albedo">漫反射反照率。</param>
    /// <param name="shadingModel">着色模型。</param>
    /// <param name="name">材质名称。</param>
    public Material(RgbColor albedo, MaterialShadingModel shadingModel = MaterialShadingModel.Lambert, string? name = null)
    {
        Albedo = albedo;
        ShadingModel = shadingModel;
        Name = name ?? "Material";
    }

    /// <summary>材质名称，用于调试与统计。</summary>
    public string Name { get; set; }

    /// <summary>反照率（基础颜色）。</summary>
    public RgbColor Albedo { get; set; }

    /// <summary>着色模型。</summary>
    public MaterialShadingModel ShadingModel { get; set; }

    /// <summary>
    /// 是否绘制双面。
    /// </summary>
    /// <remarks>
    /// 默认单面。薄片状几何（如之后可能加入的告示牌）需要置为 <c>true</c>，
    /// 否则从背面看会直接消失。
    /// </remarks>
    public bool DoubleSided { get; set; }

    /// <summary>是否写入深度缓冲。半透明物体通常关闭。</summary>
    public bool DepthWrite { get; set; } = true;

    /// <summary>是否接受方向光。</summary>
    public bool AcceptsLighting { get; set; } = true;

    /// <summary>
    /// 透明混合模式。默认 <see cref="BlendMode.Opaque"/>。
    /// </summary>
    /// <remarks>
    /// 设为 <see cref="BlendMode.AlphaBlend"/> 后，光栅化器会：
    /// <list type="bullet">
    /// <item><description>关闭该三角形的深度写入（但仍做深度测试，让被遮挡的半透物体正确剔除）；</description></item>
    /// <item><description>把着色后的源颜色按 <c>srcA</c> 与帧缓冲已有颜色做 over 混合：<c>dst = src*srcA + dst*(1-srcA)</c>。</description></item>
    /// </list>
    /// 注意：半透明物体应在不透明物体之后绘制，本引擎当前不自动排序，请按绘制顺序保证正确性。
    /// </remarks>
    public BlendMode BlendMode { get; set; } = BlendMode.Opaque;

    /// <summary>
    /// 整体不透明度，作用于着色后的最终颜色。<c>1</c> 完全不透明，<c>0</c> 完全透明。
    /// </summary>
    /// <remarks>
    /// 仅在 <see cref="BlendMode"/> 为 <see cref="BlendMode.AlphaBlend"/> 时参与混合；
    /// Opaque 模式下该值被忽略。它会被顶点色的 alpha 进一步相乘。
    /// </remarks>
    public float Opacity { get; set; } = 1.0f;

    /// <summary>
    /// Alpha 测试阈值。片元 alpha 小于该值时被丢弃。
    /// 仅在 <see cref="BlendMode"/> 为 <see cref="BlendMode.AlphaTest"/> 时生效。
    /// </summary>
    public float AlphaCutoff { get; set; } = 0.5f;

    /// <summary>
    /// 贴图（可选）。有值时着色器会按 <see cref="TexturedAlbedo"/> 采样并与反照率相乘，
    /// 于是同一张 <see cref="Texture2D"/> 可以被染色成金 / 蓝等不同版本的墙纸。
    /// </summary>
    public Texture2D? Texture { get; set; }

    /// <summary>
    /// 贴图强度：<c>0</c> 完全不使用贴图，<c>1</c> 完全用贴图取代反照率。
    /// 留一点余地是为了让「纯白墙」与「有纹理的墙」共用同一个材质模板。
    /// </summary>
    public float TexturedAlbedo { get; set; }

    /// <summary>在 <see cref="Texture"/> 基础上构造一个带贴图的材质副本。</summary>
    /// <param name="texture">贴图。</param>
    /// <param name="name">材质名称。</param>
    public Material WithTexture(Texture2D texture, string? name = null) => new(Albedo, ShadingModel, name ?? Name)
    {
        DoubleSided = DoubleSided,
        DepthWrite = DepthWrite,
        AcceptsLighting = AcceptsLighting,
        Texture = texture,
        TexturedAlbedo = 1.0f,
    };

    /// <summary>创建一个无光照的纯色材质。</summary>
    public static Material Solid(RgbColor albedo, string? name = null) =>
        new(albedo, MaterialShadingModel.Unlit, name);

    /// <summary>创建一个朗伯漫反射材质。</summary>
    public static Material Lambert(RgbColor albedo, string? name = null) =>
        new(albedo, MaterialShadingModel.Lambert, name);

    /// <summary>基于当前材质复制一份，便于做细微变体（例如临时高亮）。</summary>
    public Material Clone(string? newName = null) => new(Albedo, ShadingModel, newName ?? Name)
    {
        DoubleSided = DoubleSided,
        DepthWrite = DepthWrite,
        AcceptsLighting = AcceptsLighting,
        Texture = Texture,
        TexturedAlbedo = TexturedAlbedo,
    };

    /// <inheritdoc />
    public override string ToString() =>
        $"Material('{Name}', albedo={Albedo}, model={ShadingModel})";
}
