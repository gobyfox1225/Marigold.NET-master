using System.Numerics;
using Marigold.Imaging;

namespace Marigold.Geometry;

/// <summary>
/// 顶点数据：位置、法线、纹理坐标与顶点色。
/// </summary>
/// <remarks>
/// <para>
/// 若顶点用字典表示（<c>{'pos': ..., 'normal': ...}</c>），单个顶点要占上百字节，
/// 且每次属性访问都是一次哈希查找。
/// 这里改为 <c>readonly struct</c>：无堆分配、连续内存、可被 CPU 缓存预取，
/// 是软件光栅器能达到可用帧率的前提。
/// </para>
/// <para>
/// 结构体大小为 12(位置) + 12(法线) + 8(UV) + 12(顶点色) = 44 字节，
/// 与字典表示相比内存占用下降约 4 倍。
/// </para>
/// </remarks>
public readonly struct Vertex : IEquatable<Vertex>
{
    /// <summary>模型空间位置。</summary>
    public readonly Vector3 Position;

    /// <summary>模型空间法线（约定为单位向量）。</summary>
    public readonly Vector3 Normal;

    /// <summary>纹理坐标。</summary>
    public readonly Vector2 TexCoord;

    /// <summary>顶点色，会与材质反照率相乘。</summary>
    public readonly RgbColor Color;

    /// <summary>默认顶点色为白色。</summary>
    public static readonly RgbColor DefaultColor = RgbColor.White;

    /// <summary>构造顶点（UV 为零、顶点色为白）。</summary>
    public Vertex(Vector3 position, Vector3 normal)
        : this(position, normal, Vector2.Zero, RgbColor.White)
    {
    }

    /// <summary>构造顶点（顶点色为白）。</summary>
    public Vertex(Vector3 position, Vector3 normal, Vector2 texCoord)
        : this(position, normal, texCoord, RgbColor.White)
    {
    }

    /// <summary>完整构造顶点。</summary>
    public Vertex(Vector3 position, Vector3 normal, Vector2 texCoord, RgbColor color)
    {
        Position = position;
        Normal = normal;
        TexCoord = texCoord;
        Color = color;
    }

    /// <summary>用矩阵变换位置与法线，返回新顶点。</summary>
    /// <param name="positionMatrix">位置变换矩阵。</param>
    /// <param name="normalMatrix">法线变换矩阵，应为世界矩阵的逆转置。</param>
    public Vertex Transformed(Matrix4x4 positionMatrix, Matrix4x4 normalMatrix)
    {
        Vector3 normal = Vector3.TransformNormal(Normal, normalMatrix);
        float lengthSquared = normal.LengthSquared();
        if (lengthSquared > float.Epsilon)
        {
            normal /= MathF.Sqrt(lengthSquared);
        }

        return new Vertex(Vector3.Transform(Position, positionMatrix), normal, TexCoord, Color);
    }

    /// <inheritdoc />
    public bool Equals(Vertex other) =>
        Position.Equals(other.Position) &&
        Normal.Equals(other.Normal) &&
        TexCoord.Equals(other.TexCoord) &&
        Color.Equals(other.Color);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Vertex other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Position, Normal, TexCoord, Color);

    /// <summary>相等运算符。</summary>
    public static bool operator ==(Vertex left, Vertex right) => left.Equals(right);

    /// <summary>不等运算符。</summary>
    public static bool operator !=(Vertex left, Vertex right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString() => $"Vertex(pos={Position}, nrm={Normal})";
}
