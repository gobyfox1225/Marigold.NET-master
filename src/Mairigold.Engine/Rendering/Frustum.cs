using System.Numerics;
using Marigold.Geometry;
using Marigold.Mathematics;

namespace Marigold.Rendering;

/// <summary>
/// 平面，用 <c>dot(Normal, point) + D</c> 表示。
/// </summary>
/// <remarks>约定法线指向“保留侧”，因此距离大于等于零表示点在平面内侧。</remarks>
public readonly struct Plane
{
    /// <summary>归一化的平面法线。</summary>
    public readonly Vector3 Normal;

    /// <summary>平面常数项。</summary>
    public readonly float D;

    /// <summary>构造平面。</summary>
    /// <param name="normal">法线，内部会自动归一化。</param>
    /// <param name="d">常数项。</param>
    public Plane(Vector3 normal, float d)
    {
        float length = normal.Length();
        if (length > MathUtil.Epsilon)
        {
            Normal = normal / length;
            D = d / length;
        }
        else
        {
            Normal = Vector3.Zero;
            D = 0.0f;
        }
    }

    /// <summary>点到平面的有符号距离，正值在保留侧。</summary>
    public float SignedDistance(Vector3 point) => Vector3.Dot(Normal, point) + D;

    /// <inheritdoc />
    public override string ToString() => $"Plane(n={Normal}, d={D:0.###})";
}

/// <summary>
/// 视锥体：由六个平面组成的凸体，用于在光栅化之前整块丢弃看不见的对象。
/// </summary>
/// <remarks>
/// <para>
/// 平面直接从“视图 × 投影”矩阵中提取（Gribb–Hartmann 方法）：
/// 行向量约定下，裁剪坐标的第 i 个分量等于 <c>dot(p, 矩阵第 i 列)</c>，
/// 于是 <c>-w ≤ x ≤ w</c> 等价于 <c>w ± x ≥ 0</c>，
/// 六条不等式正好对应六个平面的系数。
/// </para>
/// <para>
/// <b>为什么需要一个房间几十个立方体也要剔除</b>：引擎 用两个房间往返复用，
/// 每个房间约 12 个立方体、144 个三角形。虽然总量不大，但走廊里绝大多数物体
/// 位于相机背后，剔除它们可以省下完整的顶点变换 + 裁剪 + 光栅化开销，
/// 这在软件渲染器里是最划算的一笔优化。
/// </para>
/// <para>
/// 包围盒与视锥的判定是<b>保守</b>的：只有确认完全在外才返回 <c>false</c>，
/// 因此不会误剔可见物体。
/// </para>
/// </remarks>
public sealed class Frustum
{
    private readonly Plane[] _planes = new Plane[6];

    private Frustum()
    {
    }

    /// <summary>左平面。</summary>
    public ref readonly Plane Left => ref _planes[0];

    /// <summary>右平面。</summary>
    public ref readonly Plane Right => ref _planes[1];

    /// <summary>下平面。</summary>
    public ref readonly Plane Bottom => ref _planes[2];

    /// <summary>上平面。</summary>
    public ref readonly Plane Top => ref _planes[3];

    /// <summary>近平面。</summary>
    public ref readonly Plane Near => ref _planes[4];

    /// <summary>远平面。</summary>
    public ref readonly Plane Far => ref _planes[5];

    /// <summary>由视图投影矩阵提取视锥体的六个平面。</summary>
    /// <param name="viewProjection">视图 × 投影矩阵（行向量约定）。</param>
    public static Frustum FromViewProjection(Matrix4x4 viewProjection)
    {
        // 行向量矩阵的各列：clip.i = dot(point, column_i)
        Vector4 column0 = new(viewProjection.M11, viewProjection.M21, viewProjection.M31, viewProjection.M41);
        Vector4 column1 = new(viewProjection.M12, viewProjection.M22, viewProjection.M32, viewProjection.M42);
        Vector4 column2 = new(viewProjection.M13, viewProjection.M23, viewProjection.M33, viewProjection.M43);
        Vector4 column3 = new(viewProjection.M14, viewProjection.M24, viewProjection.M34, viewProjection.M44);

        Frustum frustum = new();
        frustum._planes[0] = FromVector(column3 + column0); // 左：  w + x >= 0
        frustum._planes[1] = FromVector(column3 - column0); // 右：  w - x >= 0
        frustum._planes[2] = FromVector(column3 + column1); // 下：  w + y >= 0
        frustum._planes[3] = FromVector(column3 - column1); // 上：  w - y >= 0
        frustum._planes[4] = FromVector(column3 + column2); // 近：  w + z >= 0
        frustum._planes[5] = FromVector(column3 - column2); // 远：  w - z >= 0
        return frustum;
    }

    /// <summary>判断轴对齐包围盒是否与视锥体相交或位于其内部。</summary>
    public bool Intersects(BoundingBox bounds)
    {
        Vector3 min = bounds.Min;
        Vector3 max = bounds.Max;

        foreach (Plane plane in _planes)
        {
            if (plane.Normal == Vector3.Zero)
            {
                continue;
            }

            // 取沿法线方向最远的那个角点（“正顶点”）。
            // 若连它都在平面外侧，则整个盒必然在外侧。
            Vector3 positive = new(
                plane.Normal.X >= 0.0f ? max.X : min.X,
                plane.Normal.Y >= 0.0f ? max.Y : min.Y,
                plane.Normal.Z >= 0.0f ? max.Z : min.Z);

            if (plane.SignedDistance(positive) < 0.0f)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>判断一个点是否在视锥体内。</summary>
    public bool Contains(Vector3 point)
    {
        foreach (Plane plane in _planes)
        {
            if (plane.SignedDistance(point) < 0.0f)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>用外接球做粗略判定（更快，但更保守）。</summary>
    public bool IntersectsSphere(Vector3 center, float radius)
    {
        foreach (Plane plane in _planes)
        {
            if (plane.SignedDistance(center) < -radius)
            {
                return false;
            }
        }

        return true;
    }

    private static Plane FromVector(Vector4 coefficients) =>
        new(new Vector3(coefficients.X, coefficients.Y, coefficients.Z), coefficients.W);

    /// <inheritdoc />
    public override string ToString() =>
        $"Frustum(near={Near.Normal}/{Near.D:0.##}, far={Far.Normal}/{Far.D:0.##})";
}
