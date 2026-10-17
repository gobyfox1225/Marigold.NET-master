using System.Numerics;

namespace Marigold.Geometry;

/// <summary>
/// 程序化基础体生成器。
/// </summary>
/// <remarks>
/// <para>
/// 渲染管线中所有几何体
/// —— 地板、天花板、墙体、门框、门板 —— 都由立方体拼成，所以一个正确的
/// <see cref="Box"/> 就足以构建整款游戏的场景。
/// </para>
/// <para>
/// 顶点按<b>面</b>重复展开（24 个顶点而非 8 个），让每个面拥有独立法线，
/// 从而得到干净的硬边光照。
/// </para>
/// </remarks>
public static class PrimitiveMeshes
{
    /// <summary>
    /// 生成一个以原点为中心、按世界轴对齐的立方体（Y 轴向上）。
    /// </summary>
    /// <param name="size">立方体的完整尺寸（长宽高）。</param>
    /// <param name="name">网格名称。</param>
    public static Mesh Box(Vector3 size, string? name = null)
    {
        Vector3 half = size * 0.5f;
        float hx = half.X, hy = half.Y, hz = half.Z;

        // 8 个角点：低四位按 (x, y, z) 的符号位组合
        Vector3[] corners =
        [
            new(-hx, -hy, -hz), // 0
            new(+hx, -hy, -hz), // 1
            new(+hx, +hy, -hz), // 2
            new(-hx, +hy, -hz), // 3
            new(-hx, -hy, +hz), // 4
            new(+hx, -hy, +hz), // 5
            new(+hx, +hy, +hz), // 6
            new(-hx, +hy, +hz), // 7
        ];

        // 每个面按“从外侧看逆时针”给出角点索引，配合外法线
        (int A, int B, int C, int D, Vector3 Normal)[] faces =
        [
            (4, 5, 6, 7, new Vector3(0, 0, 1)),   // +Z 正面
            (1, 0, 3, 2, new Vector3(0, 0, -1)),  // -Z 背面
            (5, 1, 2, 6, new Vector3(1, 0, 0)),   // +X 右面
            (0, 4, 7, 3, new Vector3(-1, 0, 0)),  // -X 左面
            (7, 6, 2, 3, new Vector3(0, 1, 0)),   // +Y 顶面
            (0, 1, 5, 4, new Vector3(0, -1, 0)),  // -Y 底面
        ];

        var builder = new MeshBuilder(name ?? "Box", 24);
        foreach ((int a, int b, int c, int d, Vector3 normal) in faces)
        {
            // 六个面的 UV 均铺满 [0, 1]，便于将来接贴图
            Vector2 uvA = new(0, 1);
            Vector2 uvB = new(1, 1);
            Vector2 uvC = new(1, 0);
            Vector2 uvD = new(0, 0);

            builder.AddQuad(
                new Vertex(corners[a], normal, uvA),
                new Vertex(corners[b], normal, uvB),
                new Vertex(corners[c], normal, uvC),
                new Vertex(corners[d], normal, uvD));
        }

        return builder.Build();
    }

    /// <summary>
    /// 生成一个以原点为中心、位于 XY 平面、法线朝 +Z 的矩形面片。
    /// </summary>
    /// <param name="width">宽度（沿 X 轴）。</param>
    /// <param name="height">高度（沿 Y 轴）。</param>
    /// <param name="name">网格名称。</param>
    public static Mesh Quad(float width, float height, string? name = null)
    {
        float hw = width * 0.5f;
        float hh = height * 0.5f;
        Vector3 normal = Vector3.UnitZ;

        var builder = new MeshBuilder(name ?? "Quad", 4);
        builder.AddQuad(
            new Vertex(new Vector3(-hw, -hh, 0), normal, new Vector2(0, 1)),
            new Vertex(new Vector3(+hw, -hh, 0), normal, new Vector2(1, 1)),
            new Vertex(new Vector3(+hw, +hh, 0), normal, new Vector2(1, 0)),
            new Vertex(new Vector3(-hw, +hh, 0), normal, new Vector2(0, 0)));

        return builder.Build();
    }

    /// <summary>
    /// 生成一个边长为 1、以原点为中心、按整数网格细分的平面（XZ 平面，法线朝 +Y）。
    /// </summary>
    /// <param name="size">平面边长。</param>
    /// <param name="subdivisions">每边的细分数，至少为 1。</param>
    /// <param name="name">网格名称。</param>
    public static Mesh Plane(float size, int subdivisions = 1, string? name = null)
    {
        subdivisions = Math.Max(1, subdivisions);
        float half = size * 0.5f;
        float step = size / subdivisions;
        var builder = new MeshBuilder(name ?? "Plane", (subdivisions + 1) * (subdivisions + 1));

        for (int z = 0; z < subdivisions; z++)
        {
            for (int x = 0; x < subdivisions; x++)
            {
                float x0 = -half + (x * step);
                float x1 = x0 + step;
                float z0 = -half + (z * step);
                float z1 = z0 + step;

                // 从上往下看（+Y 视角）为逆时针
                builder.AddQuad(
                    new Vertex(new Vector3(x0, 0, z1), Vector3.UnitY, new Vector2(x / (float)subdivisions, (z + 1) / (float)subdivisions)),
                    new Vertex(new Vector3(x1, 0, z1), Vector3.UnitY, new Vector2((x + 1) / (float)subdivisions, (z + 1) / (float)subdivisions)),
                    new Vertex(new Vector3(x1, 0, z0), Vector3.UnitY, new Vector2((x + 1) / (float)subdivisions, z / (float)subdivisions)),
                    new Vertex(new Vector3(x0, 0, z0), Vector3.UnitY, new Vector2(x / (float)subdivisions, z / (float)subdivisions)));
            }
        }

        return builder.Build();
    }
}
