using System.Numerics;
using Marigold.Mathematics;

namespace Marigold.Geometry;

/// <summary>二维多边形顶点绕序。</summary>
public enum PolygonWinding
{
    /// <summary>退化（面积为零，无法判定绕序）。</summary>
    Degenerate = 0,

    /// <summary>逆时针。右手坐标系下，从 +Z 方向俯视时顶点按逆时针排列。</summary>
    CounterClockwise = 1,

    /// <summary>顺时针。</summary>
    Clockwise = -1,
}

/// <summary>
/// 二维多边形拉伸体生成器。
/// </summary>
/// <remarks>
/// <para>
/// 算法：顶面与底面以<b>形心</b>为轴心做三角扇，侧面为每条边生成一个独立四边形，
/// 并为侧面顶点单独复制一份以保证硬边法线。
/// </para>
/// <para>
/// <b>关键约定</b>：
/// 多边形位于 <b>XY 平面</b>，沿 <b>+Z 轴</b>拉伸，即 Z 轴是“高度”方向。
/// 这与本引擎世界空间的 Y 轴向上不同，因此把结果放进游戏场景时，
/// 需要通过 <see cref="Mesh.Transform"/> 施加一次 -90° 绕 X 轴的旋转
/// （见 <see cref="ZUpToYUpMatrix"/>）。
/// </para>
/// <para>
/// <b>设计要点</b>：
/// </para>
/// <list type="number">
/// <item><description>
/// <b>绕序自适应</b>：若直接按 <c>(dy, -dx)</c> 计算侧面法线，这只在输入多边形为逆时针时
/// 才指向外侧；若输入是顺时针，会得到一整圈朝内的法线（表现为“黑模型”）。
/// 这里先探测绕序并把多边形归一化为逆时针，因此任意输入都能得到正确的外向法线。
/// </description></item>
/// <item><description>
/// <b>显式参数校验</b>：顶点不足、高度非正、含 NaN 等情况直接抛出带说明的异常，
/// 而不是静默返回空数组。
/// </description></item>
/// <item><description>
/// <b>退化顶点清理</b>：合并重合的相邻顶点（含首尾）、跳过零长边，
/// 避免产生零面积三角形——这类三角形在光栅化阶段会浪费扫描时间。
/// </description></item>
/// <item><description>
/// <b>底面高度可指定</b>：支持从任意高度起拉，而不只是从 z=0 起。
/// </description></item>
/// </list>
/// </remarks>
public static class PolygonExtruder
{
    /// <summary>
    /// 把“Z 轴向上”的拉伸结果摆正到本引擎“Y 轴向上”世界空间的旋转矩阵。
    /// </summary>
    /// <remarks>
    /// 即 -90° 绕 X 轴旋转：拉伸时的 Z 轴高度方向映射为世界的 Y 轴方向。
    /// </remarks>
    public static Matrix4x4 ZUpToYUpMatrix => Matrix4x4.CreateRotationX(-MathUtil.HalfPi);

    /// <summary>
    /// 把二维多边形沿 +Z 拉伸为三维网格。
    /// </summary>
    /// <param name="polygon">多边形顶点，按 XY 平面给出，可顺可逆时针，会自动归一化。</param>
    /// <param name="height">拉伸高度，必须为正数。</param>
    /// <param name="name">网格名称。</param>
    /// <returns>顶面、底面与侧面齐备的封闭网格。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="polygon"/> 为 null。</exception>
    /// <exception cref="ArgumentException">顶点少于 3 个，或有效顶点不足 3 个。</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="height"/> 不是正数。</exception>
    public static Mesh Extrude(IReadOnlyList<Vector2> polygon, float height, string? name = null)
        => Extrude(polygon, 0.0f, height, name);

    /// <summary>
    /// 把二维多边形从指定底面高度沿 +Z 拉伸为三维网格。
    /// </summary>
    /// <param name="polygon">多边形顶点，按 XY 平面给出。</param>
    /// <param name="baseHeight">底面所在的 Z 高度。</param>
    /// <param name="height">拉伸高度，必须为正数。</param>
    /// <param name="name">网格名称。</param>
    public static Mesh Extrude(IReadOnlyList<Vector2> polygon, float baseHeight, float height, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(polygon);
        if (polygon.Count < 3)
        {
            throw new ArgumentException(
                $"拉伸多边形至少需要 3 个顶点，当前为 {polygon.Count} 个。", nameof(polygon));
        }

        if (!(height > 0.0f) || !float.IsFinite(height))
        {
            throw new ArgumentOutOfRangeException(nameof(height), height, "拉伸高度必须是有限正数。");
        }

        for (int i = 0; i < polygon.Count; i++)
        {
            if (!float.IsFinite(polygon[i].X) || !float.IsFinite(polygon[i].Y))
            {
                throw new ArgumentException($"多边形第 {i} 个顶点包含 NaN 或无穷值。", nameof(polygon));
            }
        }

        List<Vector2> ring = Normalize(polygon);
        if (ring.Count < 3)
        {
            throw new ArgumentException(
                "清理重合顶点后有效顶点不足 3 个，无法生成拉伸体。", nameof(polygon));
        }

        int n = ring.Count;
        float top = baseHeight + height;
        float bottom = baseHeight;

        var builder = new MeshBuilder(name ?? "ExtrudedPolygon", (n * 2) + 2 + (n * 4));

        // ---- 形心：顶/底三角扇的轴心 ----
        float cx = 0.0f;
        float cy = 0.0f;
        foreach (Vector2 p in ring)
        {
            cx += p.X;
            cy += p.Y;
        }

        cx /= n;
        cy /= n;

        Vector3 normalTop = new(0, 0, 1);
        Vector3 normalBottom = new(0, 0, -1);

        // ---- 顶面：形心 + 环形顶点，法线 +Z，逆时针 ----
        int topCenter = builder.AddVertex(new Vector3(cx, cy, top), normalTop);
        int topRing = builder.VertexCount;
        for (int i = 0; i < n; i++)
        {
            builder.AddVertex(new Vector3(ring[i].X, ring[i].Y, top), normalTop);
        }

        for (int i = 0; i < n; i++)
        {
            int next = (i + 1) % n;
            builder.AddTriangleIndexed(topCenter, topRing + i, topRing + next);
        }

        // ---- 底面：形心 + 环形顶点，法线 -Z，绕序反向以保持逆时针外向 ----
        int bottomCenter = builder.AddVertex(new Vector3(cx, cy, bottom), normalBottom);
        int bottomRing = builder.VertexCount;
        for (int i = 0; i < n; i++)
        {
            builder.AddVertex(new Vector3(ring[i].X, ring[i].Y, bottom), normalBottom);
        }

        for (int i = 0; i < n; i++)
        {
            int next = (i + 1) % n;
            builder.AddTriangleIndexed(bottomCenter, bottomRing + next, bottomRing + i);
        }

        // ---- 侧面：每条边一个四边形，顶点按边独立复制以获得硬边法线 ----
        for (int i = 0; i < n; i++)
        {
            Vector2 current = ring[i];
            Vector2 next = ring[(i + 1) % n];

            float dx = next.X - current.X;
            float dy = next.Y - current.Y;
            float length = MathF.Sqrt((dx * dx) + (dy * dy));
            if (length <= MathUtil.Epsilon)
            {
                // Normalize 已剔除重合点，这里是针对近似重合的兜底
                continue;
            }

            // 逆时针多边形上，边方向顺时针旋转 90° 即为指向外侧的法线
            Vector3 normal = new(dy / length, -dx / length, 0.0f);

            // 四边形按逆时针环形顺序给出：左上 -> 左下 -> 右下 -> 右上
            Vertex topLeft = new(new Vector3(current.X, current.Y, top), normal);
            Vertex bottomLeft = new(new Vector3(current.X, current.Y, bottom), normal);
            Vertex bottomRight = new(new Vector3(next.X, next.Y, bottom), normal);
            Vertex topRight = new(new Vector3(next.X, next.Y, top), normal);

            builder.AddQuad(topLeft, bottomLeft, bottomRight, topRight);
        }

        return builder.Build();
    }

    /// <summary>
    /// 计算多边形的有符号面积（鞋带公式）。
    /// </summary>
    /// <returns>逆时针为正，顺时针为负。</returns>
    public static float ComputeSignedArea(IReadOnlyList<Vector2> polygon)
    {
        ArgumentNullException.ThrowIfNull(polygon);

        float area = 0.0f;
        int n = polygon.Count;
        for (int i = 0; i < n; i++)
        {
            Vector2 current = polygon[i];
            Vector2 next = polygon[(i + 1) % n];
            area += (current.X * next.Y) - (next.X * current.Y);
        }

        return area * 0.5f;
    }

    /// <summary>探测多边形绕序。</summary>
    public static PolygonWinding GetWinding(IReadOnlyList<Vector2> polygon)
    {
        float area = ComputeSignedArea(polygon);
        if (MathF.Abs(area) <= MathUtil.Epsilon)
        {
            return PolygonWinding.Degenerate;
        }

        return area > 0.0f ? PolygonWinding.CounterClockwise : PolygonWinding.Clockwise;
    }

    /// <summary>
    /// 清理重合顶点并把多边形归一化为逆时针绕序。
    /// </summary>
    /// <returns>归一化后的顶点列表（可能是原列表的副本）。</returns>
    public static List<Vector2> Normalize(IReadOnlyList<Vector2> polygon)
    {
        ArgumentNullException.ThrowIfNull(polygon);

        List<Vector2> cleaned = new(polygon.Count);
        foreach (Vector2 point in polygon)
        {
            if (cleaned.Count > 0)
            {
                Vector2 previous = cleaned[^1];
                float dx = point.X - previous.X;
                float dy = point.Y - previous.Y;
                if ((dx * dx) + (dy * dy) <= MathUtil.Epsilon)
                {
                    continue;
                }
            }

            cleaned.Add(point);
        }

        // 首尾重合同样去掉
        while (cleaned.Count > 1)
        {
            Vector2 first = cleaned[0];
            Vector2 last = cleaned[^1];
            float dx = first.X - last.X;
            float dy = first.Y - last.Y;
            if ((dx * dx) + (dy * dy) > MathUtil.Epsilon)
            {
                break;
            }

            cleaned.RemoveAt(cleaned.Count - 1);
        }

        if (cleaned.Count >= 3 && GetWinding(cleaned) == PolygonWinding.Clockwise)
        {
            cleaned.Reverse();
        }

        return cleaned;
    }
}
