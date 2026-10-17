using Marigold.Mathematics;

namespace Marigold.Rendering;

/// <summary>
/// 三角形裁剪器：在齐次裁剪空间对近、远平面执行 Sutherland–Hodgman 裁剪。
/// </summary>
/// <remarks>
/// <para>
/// <b>这一步为什么必须有</b>：渲染器的走廊很长，玩家会频繁贴近甚至穿过门框与墙体，
/// 几乎每一帧都有三角形跨越相机平面。如果不裁剪，这些三角形的投影会退化或翻转，
/// 表现为“贴墙时整块画面被涂满”。
/// 一旦相机位于多边形内部，不裁剪就会画错。
/// </para>
/// <para>
/// 裁剪结果最多 4 个顶点（三角形被单个平面切一刀），因此输出缓冲区容量取 4 即可；
/// 这里给出 8 的余量，方便将来扩展到完整的六平面裁剪。
/// </para>
/// <para>
/// <b>快速路径</b>：绝大多数三角形完全位于视锥内侧或完全在外侧，
/// 一次分量比较就能跳过整个裁剪流程，避免为每个三角形分配中间顶点。
/// </para>
/// </remarks>
public static class TriangleClipper
{
    /// <summary>裁剪后可能产生的最大顶点数。</summary>
    public const int MaxClippedVertices = 8;

    /// <summary>判断三角形是否完全位于近平面内侧（可以直接进入透视除法）。</summary>
    public static bool IsFullyInsideNearPlane(in ClipVertex a, in ClipVertex b, in ClipVertex c) =>
        a.NearPlaneDistance >= 0.0f && b.NearPlaneDistance >= 0.0f && c.NearPlaneDistance >= 0.0f;

    /// <summary>判断三角形是否完全位于近平面外侧（可以整块丢弃）。</summary>
    public static bool IsFullyOutsideNearPlane(in ClipVertex a, in ClipVertex b, in ClipVertex c) =>
        a.NearPlaneDistance < 0.0f && b.NearPlaneDistance < 0.0f && c.NearPlaneDistance < 0.0f;

    /// <summary>判断三角形是否完全位于远平面之外（可以整块丢弃）。</summary>
    public static bool IsFullyOutsideFarPlane(in ClipVertex a, in ClipVertex b, in ClipVertex c) =>
        a.IsBeyondFarPlane && b.IsBeyondFarPlane && c.IsBeyondFarPlane;

    /// <summary>
    /// 用近平面裁剪一个三角形。
    /// </summary>
    /// <param name="a">三角形顶点 A。</param>
    /// <param name="b">三角形顶点 B。</param>
    /// <param name="c">三角形顶点 C。</param>
    /// <param name="output">输出多边形顶点，容量至少 <see cref="MaxClippedVertices"/>。</param>
    /// <returns>输出顶点数量：0 表示完全被裁掉，3 或 4 表示需要三角化的多边形。</returns>
    /// <exception cref="ArgumentException">输出缓冲区太小。</exception>
    /// <remarks>
    /// 顶点以三个独立参数传入而不是打包成数组，是为了让调用方可以使用栈上缓冲
    /// 并且做到<b>零堆分配</b>——每个三角形一次数组分配足以让 GC 在复杂场景下持续介入。
    /// </remarks>
    public static int ClipAgainstNearPlane(
        in ClipVertex a,
        in ClipVertex b,
        in ClipVertex c,
        Span<ClipVertex> output)
    {
        if (output.Length < MaxClippedVertices)
        {
            throw new ArgumentException($"输出缓冲区至少需要 {MaxClippedVertices} 个顶点。", nameof(output));
        }

        ReadOnlySpan<ClipVertex> input = stackalloc ClipVertex[3] { a, b, c };
        return ClipAgainstNearPlane(input, output);
    }

    /// <summary>
    /// 用近平面裁剪一个三角形（多边形入口，便于将来扩展到任意凸多边形）。
    /// </summary>
    /// <param name="input">输入三角形（3 个顶点）。</param>
    /// <param name="output">输出多边形顶点，容量至少 <see cref="MaxClippedVertices"/>。</param>
    /// <returns>输出顶点数量。</returns>
    public static int ClipAgainstNearPlane(ReadOnlySpan<ClipVertex> input, Span<ClipVertex> output)
    {
        if (input.Length != 3)
        {
            throw new ArgumentException("输入必须是 3 个顶点的三角形。", nameof(input));
        }

        if (output.Length < MaxClippedVertices)
        {
            throw new ArgumentException($"输出缓冲区至少需要 {MaxClippedVertices} 个顶点。", nameof(output));
        }

        int outCount = 0;
        for (int i = 0; i < 3; i++)
        {
            ClipVertex current = input[i];
            ClipVertex next = input[(i + 1) % 3];

            float dCurrent = current.NearPlaneDistance;
            float dNext = next.NearPlaneDistance;

            bool currentInside = dCurrent >= 0.0f;
            bool nextInside = dNext >= 0.0f;

            if (currentInside)
            {
                output[outCount++] = current;
            }

            // 跨越平面：在交点处插入一个新顶点
            if (currentInside != nextInside)
            {
                float denominator = dCurrent - dNext;
                if (MathF.Abs(denominator) > MathUtil.Epsilon)
                {
                    float t = dCurrent / denominator;
                    output[outCount++] = ClipVertex.Lerp(current, next, t);
                }
            }
        }

        return outCount;
    }

    /// <summary>
    /// 把凸多边形按三角扇拆解为三角形并逐个回调。
    /// </summary>
    /// <remarks>
    /// 裁剪结果是凸多边形，用 <c>(0, i, i+1)</c> 的扇式拆分即可，
    /// 且不会破坏原有的顶点绕序——这一点对背面剔除至关重要。
    /// </remarks>
    public static void TriangulateFan(ReadOnlySpan<ClipVertex> polygon, Action<ClipVertex, ClipVertex, ClipVertex> emit)
    {
        ArgumentNullException.ThrowIfNull(emit);

        for (int i = 1; i + 1 < polygon.Length; i++)
        {
            emit(polygon[0], polygon[i], polygon[i + 1]);
        }
    }
}
