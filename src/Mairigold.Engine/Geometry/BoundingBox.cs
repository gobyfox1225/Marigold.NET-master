using System.Numerics;

namespace Marigold.Geometry;

/// <summary>
/// 轴对齐包围盒（AABB），用于视锥剔除、场景统计与碰撞体表达。
/// </summary>
public readonly struct BoundingBox : IEquatable<BoundingBox>
{
    /// <summary>最小角点。</summary>
    public readonly Vector3 Min;

    /// <summary>最大角点。</summary>
    public readonly Vector3 Max;

    /// <summary>构造包围盒。</summary>
    public BoundingBox(Vector3 min, Vector3 max)
    {
        Min = Vector3.Min(min, max);
        Max = Vector3.Max(min, max);
    }

    /// <summary>用中心点与尺寸构造包围盒。</summary>
    public static BoundingBox FromCenterSize(Vector3 center, Vector3 size) =>
        new(center - (size * 0.5f), center + (size * 0.5f));

    /// <summary>包围盒中心。</summary>
    public Vector3 Center => (Min + Max) * 0.5f;

    /// <summary>包围盒尺寸。</summary>
    public Vector3 Size => Max - Min;

    /// <summary>包围盒外接球半径。</summary>
    public float BoundingRadius => (Max - Min).Length() * 0.5f;

    /// <summary>盒内体积（用于校验网格是否封闭且绕序正确）。</summary>
    public float Volume
    {
        get
        {
            Vector3 s = Size;
            return s.X * s.Y * s.Z;
        }
    }

    /// <summary>由一组点计算包围盒。</summary>
    public static BoundingBox FromPoints(IReadOnlyList<Vector3> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count == 0)
        {
            return new BoundingBox(Vector3.Zero, Vector3.Zero);
        }

        Vector3 min = points[0];
        Vector3 max = points[0];
        for (int i = 1; i < points.Count; i++)
        {
            min = Vector3.Min(min, points[i]);
            max = Vector3.Max(max, points[i]);
        }

        return new BoundingBox(min, max);
    }

    /// <summary>合并两个包围盒。</summary>
    public BoundingBox Union(BoundingBox other) =>
        new(Vector3.Min(Min, other.Min), Vector3.Max(Max, other.Max));

    /// <summary>用一个点扩展包围盒。</summary>
    public BoundingBox Expand(Vector3 point) =>
        new(Vector3.Min(Min, point), Vector3.Max(Max, point));

    /// <summary>按各方向统一外扩。</summary>
    public BoundingBox Expand(float amount) =>
        new(Min - new Vector3(amount), Max + new Vector3(amount));

    /// <summary>判断点是否在盒内。</summary>
    public bool Contains(Vector3 point) =>
        point.X >= Min.X && point.X <= Max.X &&
        point.Y >= Min.Y && point.Y <= Max.Y &&
        point.Z >= Min.Z && point.Z <= Max.Z;

    /// <summary>判断与另一个包围盒是否相交（接触不算相交）。</summary>
    public bool Intersects(BoundingBox other) =>
        Min.X < other.Max.X && Max.X > other.Min.X &&
        Min.Y < other.Max.Y && Max.Y > other.Min.Y &&
        Min.Z < other.Max.Z && Max.Z > other.Min.Z;

    /// <summary>取八个角点。</summary>
    public Vector3[] GetCorners() =>
    [
        new(Min.X, Min.Y, Min.Z),
        new(Max.X, Min.Y, Min.Z),
        new(Max.X, Max.Y, Min.Z),
        new(Min.X, Max.Y, Min.Z),
        new(Min.X, Min.Y, Max.Z),
        new(Max.X, Min.Y, Max.Z),
        new(Max.X, Max.Y, Max.Z),
        new(Min.X, Max.Y, Max.Z),
    ];

    /// <summary>用矩阵变换包围盒，返回变换后的新轴对齐包围盒。</summary>
    public BoundingBox Transform(Matrix4x4 matrix)
    {
        BoundingBox result = new(Vector3.Transform(Min, matrix), Vector3.Transform(Min, matrix));
        foreach (Vector3 corner in GetCorners())
        {
            result = result.Expand(Vector3.Transform(corner, matrix));
        }

        return result;
    }

    /// <inheritdoc />
    public bool Equals(BoundingBox other) => Min.Equals(other.Min) && Max.Equals(other.Max);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is BoundingBox other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Min, Max);

    /// <summary>相等运算符。</summary>
    public static bool operator ==(BoundingBox left, BoundingBox right) => left.Equals(right);

    /// <summary>不等运算符。</summary>
    public static bool operator !=(BoundingBox left, BoundingBox right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString() => $"AABB(min={Min}, max={Max})";
}
