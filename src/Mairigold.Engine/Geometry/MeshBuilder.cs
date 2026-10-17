using System.Numerics;

namespace Marigold.Geometry;

/// <summary>
/// 网格构建器：以可增长缓冲区逐步累积三角形，最后 <see cref="Build"/> 出一个不可变网格。
/// </summary>
/// <remarks>
/// <para>
/// 把顶点与索引的累积工作交给构建器，调用方不需要再手动记录
/// “底面中心点在顶点列表中的索引位置” 这类容易写错的偏移量。
/// </para>
/// <para>
/// 构建器本身不是线程安全的。多线程生成几何时，每个线程持有自己的构建器，
/// 最后再合并即可。
/// </para>
/// </remarks>
public sealed class MeshBuilder
{
    private readonly string _name;
    private readonly List<Vertex> _vertices;
    private readonly List<int> _indices;

    /// <summary>构造一个构建器。</summary>
    /// <param name="name">生成网格的名称。</param>
    /// <param name="capacityHint">预估顶点数量，用于减少列表扩容。</param>
    public MeshBuilder(string? name = null, int capacityHint = 0)
    {
        _name = name ?? "Mesh";
        _vertices = new List<Vertex>(Math.Max(capacityHint, 16));
        _indices = new List<int>(Math.Max(capacityHint, 16));
    }

    /// <summary>已写入的顶点数量。</summary>
    public int VertexCount => _vertices.Count;

    /// <summary>已写入的索引数量。</summary>
    public int IndexCount => _indices.Count;

    /// <summary>已写入的三角形数量。</summary>
    public int TriangleCount => _indices.Count / 3;

    /// <summary>追加一个顶点，返回它的索引。</summary>
    public int AddVertex(in Vertex vertex)
    {
        _vertices.Add(vertex);
        return _vertices.Count - 1;
    }

    /// <summary>追加一个顶点，返回它的索引。</summary>
    public int AddVertex(Vector3 position, Vector3 normal) => AddVertex(new Vertex(position, normal));

    /// <summary>追加一个顶点，返回它的索引。</summary>
    public int AddVertex(Vector3 position, Vector3 normal, Vector2 texCoord) =>
        AddVertex(new Vertex(position, normal, texCoord));

    /// <summary>
    /// 直接用已有顶点索引追加一个三角形。
    /// </summary>
    /// <remarks>
    /// 用于形心三角扇这类需要复用同一批顶点（由外部记录索引）的场景，
    /// 避免为每个三角形重复写入顶点。
    /// </remarks>
    public void AddTriangleIndexed(int a, int b, int c)
    {
        if ((uint)a >= (uint)_vertices.Count || (uint)b >= (uint)_vertices.Count || (uint)c >= (uint)_vertices.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(a), $"索引越界：当前顶点数量为 {_vertices.Count}。");
        }

        _indices.Add(a);
        _indices.Add(b);
        _indices.Add(c);
    }

    /// <summary>按逆时针绕序追加一个三角形。</summary>
    public void AddTriangle(in Vertex a, in Vertex b, in Vertex c)
    {
        _indices.Add(AddVertex(a));
        _indices.Add(AddVertex(b));
        _indices.Add(AddVertex(c));
    }

    /// <summary>按逆时针绕序追加一个三角形。</summary>
    public void AddTriangle(Vector3 a, Vector3 b, Vector3 c, Vector3 normal)
    {
        AddTriangle(new Vertex(a, normal), new Vertex(b, normal), new Vertex(c, normal));
    }

    /// <summary>
    /// 追加一个四边形（按 a→b→c→d 的逆时针顺序），内部拆成两个三角形。
    /// </summary>
    /// <remarks>
    /// 四个顶点只写入一次，两个三角形共享它们。
    /// 若在这里改成连续调用两次 <see cref="AddTriangle(in Vertex, in Vertex, in Vertex)"/>，
    /// 顶点数会翻倍到 6 个——四边形共面的情况下渲染结果看不出区别，
    /// 但内存与顶点变换开销都会平白多出一半。
    /// </remarks>
    public void AddQuad(in Vertex a, in Vertex b, in Vertex c, in Vertex d)
    {
        int indexA = AddVertex(a);
        int indexB = AddVertex(b);
        int indexC = AddVertex(c);
        int indexD = AddVertex(d);

        _indices.Add(indexA);
        _indices.Add(indexB);
        _indices.Add(indexC);

        _indices.Add(indexA);
        _indices.Add(indexC);
        _indices.Add(indexD);
    }

    /// <summary>把已有网格（可附带变换）合并进来。</summary>
    public MeshBuilder AddMesh(Mesh mesh, Matrix4x4? transform = null)
    {
        ArgumentNullException.ThrowIfNull(mesh);

        int baseIndex = _vertices.Count;
        if (transform.HasValue)
        {
            Matrix4x4 matrix = transform.Value;
            Matrix4x4 normalMatrix = matrix;
            if (Matrix4x4.Invert(matrix, out Matrix4x4 inverse))
            {
                normalMatrix = Matrix4x4.Transpose(inverse);
            }

            ReadOnlySpan<Vertex> source = mesh.Vertices;
            for (int i = 0; i < source.Length; i++)
            {
                _vertices.Add(source[i].Transformed(matrix, normalMatrix));
            }
        }
        else
        {
            foreach (Vertex vertex in mesh.Vertices)
            {
                _vertices.Add(vertex);
            }
        }

        foreach (int index in mesh.Indices)
        {
            _indices.Add(baseIndex + index);
        }

        return this;
    }

    /// <summary>产出不可变网格。构建器可以继续复用来构建下一个网格。</summary>
    public Mesh Build() => new(_vertices.ToArray(), _indices.ToArray(), _name);

    /// <summary>清空缓冲区以便复用（保留已分配的容量）。</summary>
    public void Clear()
    {
        _vertices.Clear();
        _indices.Clear();
    }
}
