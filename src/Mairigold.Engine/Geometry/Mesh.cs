using System.Numerics;

namespace Marigold.Geometry;

/// <summary>
/// 三角网格：一组顶点加上一组三角形索引。
/// </summary>
/// <remarks>
/// <para>
/// 把一组顶点和一组索引提升为一个自带不变式校验、包围盒缓存与变换能力的正式类型。
/// </para>
/// <para>
/// <b>绕序约定</b>：所有三角形以<b>逆时针</b>（CCW）为正面，几何法线
/// <c>(v1 - v0) × (v2 - v0)</c> 指向物体外侧。这与 OpenGL 的默认正面朝向一致，
/// 也是本引擎光栅器的背面剔除依据。
/// </para>
/// <para>
/// 网格是<b>不可变</b>的：构造时完成全套校验，之后不会被修改。
/// 需要变形时用 <see cref="Transform"/> 生成新网格，或通过 <see cref="MeshBuilder"/> 重建。
/// </para>
/// </remarks>
public sealed class Mesh
{
    private readonly Vertex[] _vertices;
    private readonly int[] _indices;

    /// <summary>构造网格。</summary>
    /// <param name="vertices">顶点数组，不能被外部继续修改。</param>
    /// <param name="indices">三角形索引数组，长度必须是 3 的倍数。</param>
    /// <param name="name">网格名称，用于调试与统计。</param>
    /// <exception cref="ArgumentException">索引数量不是 3 的倍数。</exception>
    /// <exception cref="ArgumentOutOfRangeException">存在越界索引。</exception>
    public Mesh(Vertex[] vertices, int[] indices, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        ArgumentNullException.ThrowIfNull(indices);

        if (indices.Length % 3 != 0)
        {
            throw new ArgumentException(
                $"索引数量必须是 3 的倍数，当前为 {indices.Length}。", nameof(indices));
        }

        for (int i = 0; i < indices.Length; i++)
        {
            if ((uint)indices[i] >= (uint)vertices.Length)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(indices), indices[i],
                    $"索引 #{i} 越界：顶点数量为 {vertices.Length}。");
            }
        }

        _vertices = vertices;
        _indices = indices;
        Name = name ?? "Mesh";

        Bounds = vertices.Length == 0
            ? new BoundingBox(Vector3.Zero, Vector3.Zero)
            : ComputeBounds(vertices);
    }

    /// <summary>网格名称。</summary>
    public string Name { get; }

    /// <summary>作为副本暴露的顶点数据，供渲染管线以 <c>Span</c> 零拷贝遍历。</summary>
    public ReadOnlySpan<Vertex> Vertices => _vertices;

    /// <summary>作为副本暴露的索引数据。</summary>
    public ReadOnlySpan<int> Indices => _indices;

    /// <summary>顶点数量。</summary>
    public int VertexCount => _vertices.Length;

    /// <summary>三角形数量。</summary>
    public int TriangleCount => _indices.Length / 3;

    /// <summary>模型空间轴对齐包围盒。</summary>
    public BoundingBox Bounds { get; }

    /// <summary>是否为空网格。</summary>
    public bool IsEmpty => _indices.Length == 0;

    /// <summary>
    /// 用矩阵变换整份网格，返回新网格。
    /// </summary>
    /// <param name="matrix">世界（或局部）变换矩阵。</param>
    /// <remarks>
    /// 法线使用世界矩阵的<b>逆转置</b>变换，这样在非等比缩放下法线依然垂直于表面，
    /// 否则一旦对模型做非等比缩放，光照就会出现错误。
    /// </remarks>
    public Mesh Transform(Matrix4x4 matrix)
    {
        Matrix4x4 normalMatrix = matrix;
        if (Matrix4x4.Invert(matrix, out Matrix4x4 inverse))
        {
            normalMatrix = Matrix4x4.Transpose(inverse);
        }

        Vertex[] transformed = new Vertex[_vertices.Length];
        for (int i = 0; i < transformed.Length; i++)
        {
            transformed[i] = _vertices[i].Transformed(matrix, normalMatrix);
        }

        return new Mesh(transformed, (int[])_indices.Clone(), Name);
    }

    /// <summary>只做平移的快速变换（法线保持不变）。</summary>
    public Mesh Translate(Vector3 offset)
    {
        Vertex[] transformed = new Vertex[_vertices.Length];
        for (int i = 0; i < transformed.Length; i++)
        {
            Vertex v = _vertices[i];
            transformed[i] = new Vertex(v.Position + offset, v.Normal, v.TexCoord, v.Color);
        }

        return new Mesh(transformed, (int[])_indices.Clone(), Name);
    }

    /// <summary>把网格合并进一个构建器（等价于 <see cref="MeshBuilder.AddMesh(Mesh, Matrix4x4?)"/>）。</summary>
    public MeshBuilder AppendTo(MeshBuilder builder, Matrix4x4? transform = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddMesh(this, transform);
    }

    private static BoundingBox ComputeBounds(ReadOnlySpan<Vertex> vertices)
    {
        Vector3 min = vertices[0].Position;
        Vector3 max = min;
        for (int i = 1; i < vertices.Length; i++)
        {
            min = Vector3.Min(min, vertices[i].Position);
            max = Vector3.Max(max, vertices[i].Position);
        }

        return new BoundingBox(min, max);
    }

    /// <inheritdoc />
    public override string ToString() =>
        $"Mesh('{Name}', {VertexCount} 顶点, {TriangleCount} 三角形, bounds={Bounds})";
}
