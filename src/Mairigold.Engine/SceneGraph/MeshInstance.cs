using System.Numerics;
using Marigold.Geometry;

namespace Marigold.SceneGraph;

/// <summary>
/// 可渲染节点：把一个 <see cref="Geometry.Mesh"/> 与一份 <see cref="Material"/> 组合到场景中。
/// </summary>
/// <remarks>
/// <para>
/// 渲染管线中的墙体、地板、门框、门板全部是 <c>MeshInstance</c>；
/// 碰撞体是与之并列的另一套数据（见游戏层的
/// <c>CollisionWorld</c>），渲染与物理互不耦合。
/// </para>
/// <para>
/// 同一个 <see cref="Mesh"/> 实例可以被任意多个 <see cref="MeshInstance"/> 共享，
/// 渲染器按引用去重后只做一次变换，这也是走廊里“两个房间、几十个立方体”
/// 依然能保持很低内存占用的原因。
/// </para>
/// </remarks>
public sealed class MeshInstance : SceneNode
{
    private Mesh _mesh;
    private Material _material;

    /// <summary>构造可渲染节点。</summary>
    /// <param name="name">节点名称。</param>
    /// <param name="mesh">网格。</param>
    /// <param name="material">材质。</param>
    /// <exception cref="ArgumentNullException">参数为 null。</exception>
    public MeshInstance(string name, Mesh mesh, Material material)
        : base(name)
    {
        _mesh = mesh ?? throw new ArgumentNullException(nameof(mesh));
        _material = material ?? throw new ArgumentNullException(nameof(material));
    }

    /// <summary>网格。替换后会刷新包围盒缓存。</summary>
    public Mesh Mesh
    {
        get => _mesh;
        set => _mesh = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>材质。</summary>
    public Material Material
    {
        get => _material;
        set => _material = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>是否参与渲染。设为 <c>false</c> 时该实例被完全跳过（比把节点隐藏更省事）。</summary>
    public bool Renderable { get; set; } = true;

    /// <summary>
    /// 逐实例的 UV 缩放。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="PrimitiveMeshes.Box"/> 的六个面都把 UV 铺满 <c>[0, 1]</c>，
    /// 但同一份立方体网格会以不同尺寸被复用——14 米长的侧墙与 0.25 米厚的墙垛
    /// 共用同一份顶点。若不做处理，同一张贴图在长墙上会被拉伸成一条。
    /// </para>
    /// <para>
    /// 把缩放做在<b>实例</b>而不是<b>贴图</b>上，是因为贴图对象在多个材质间共享，
    /// 而拉伸比例取决于该块板件的<b>世界尺寸</b>。渲染器在顶点变换阶段
    /// 直接对 UV 做一次乘加，代价可以忽略。
    /// </para>
    /// <para>约定：<c>UvScale</c> 的取值应让贴图在表面上的实际尺寸接近其原始像素尺寸。</para>
    /// </remarks>
    public Vector2 UvScale { get; set; } = Vector2.One;

    /// <summary>逐实例的 UV 偏移，配合 <see cref="UvScale"/> 用于把贴图对齐到板件中心。</summary>
    public Vector2 UvOffset { get; set; } = Vector2.Zero;

    /// <summary>
    /// 按世界尺寸设置 UV 缩放：让贴图在表面上按「每米多少张贴图」均匀铺开。
    /// </summary>
    /// <param name="size">板件的世界尺寸。</param>
    /// <param name="metersPerTile">一张贴图覆盖多少米。</param>
    public void SetUvFromWorldSize(Vector3 size, float metersPerTile = 2.0f)
    {
        if (metersPerTile <= 0.0f)
        {
            return;
        }

        // 立方体的 ±Z / ±X / ±Y 三个面各自的水平、垂直跨度不同，
        // 取最大跨度做统一缩放，保证同一块板件的所有面纹理密度一致。
        float horizontal = MathF.Max(MathF.Max(size.X, size.Z), size.Y);
        float vertical = MathF.Max(size.Y, MathF.Min(size.X, size.Z));
        if (vertical <= 0.0f)
        {
            vertical = size.Y;
        }

        UvScale = new Vector2(
            MathF.Max(1.0f, horizontal / metersPerTile),
            MathF.Max(1.0f, vertical / metersPerTile));

        // 缩放以面中心为基准，避免贴图从一角开始铺（会看到接缝穿过画面中央）
        UvOffset = new Vector2(
            -(UvScale.X - 1.0f) * 0.5f,
            -(UvScale.Y - 1.0f) * 0.5f);
    }

    /// <inheritdoc />
    public override BoundingBox LocalBounds => _mesh.Bounds;

    /// <inheritdoc />
    public override string ToString() =>
        $"MeshInstance('{Name}', {_mesh.TriangleCount} 三角形, 材质='{_material.Name}')";
}
