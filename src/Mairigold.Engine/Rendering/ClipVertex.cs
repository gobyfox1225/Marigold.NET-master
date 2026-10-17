using System.Numerics;
using Marigold.Imaging;
using Marigold.Mathematics;

namespace Marigold.Rendering;

/// <summary>
/// 管线中间态顶点：已完成世界变换与裁剪空间变换，但尚未做透视除法。
/// </summary>
/// <remarks>
/// <para>
/// 这是从 <see cref="Geometry.Vertex"/> 向屏幕空间过渡的载体，同时携带
/// “裁剪需要的信息”（齐次坐标）与“着色需要的信息”（世界坐标与法线）。
/// </para>
/// <para>
/// 之所以要先保留齐次坐标再做除法：透视除法会让相机背后的点被“镜像”到画面里，
/// 只有先在齐次空间完成近平面裁剪，才能保证除法后的坐标是可信的。
/// 若直接对坐标做除法，当模型跨越相机平面时会出现撕裂与翻面。
/// </para>
/// </remarks>
public struct ClipVertex
{
    /// <summary>裁剪空间齐次坐标（未做透视除法）。</summary>
    public Vector4 ClipPosition;

    /// <summary>世界空间位置，用于光照与雾。</summary>
    public Vector3 WorldPosition;

    /// <summary>世界空间法线（已归一化），用于光照。</summary>
    public Vector3 WorldNormal;

    /// <summary>纹理坐标。</summary>
    public Vector2 TexCoord;

    /// <summary>顶点色。</summary>
    public RgbColor Color;

    /// <summary>构造裁剪顶点。</summary>
    public ClipVertex(Vector4 clipPosition, Vector3 worldPosition, Vector3 worldNormal, Vector2 texCoord, RgbColor color)
    {
        ClipPosition = clipPosition;
        WorldPosition = worldPosition;
        WorldNormal = worldNormal;
        TexCoord = texCoord;
        Color = color;
    }

    /// <summary>
    /// 本顶点到近平面的有符号距离。
    /// </summary>
    /// <remarks>
    /// 深度区间为 [-1, 1] 时，近平面条件为 <c>z + w ≥ 0</c>。
    /// </remarks>
    public readonly float NearPlaneDistance => ClipPosition.Z + ClipPosition.W;

    /// <summary>本顶点是否位于远平面之外（<c>z - w &gt; 0</c>）。</summary>
    public readonly bool IsBeyondFarPlane => ClipPosition.Z - ClipPosition.W > 0.0f;

    /// <summary>在本顶点与另一个顶点之间线性插值（用于裁剪生成新顶点）。</summary>
    /// <param name="a">起点。</param>
    /// <param name="b">终点。</param>
    /// <param name="t">插值系数，0 返回 <paramref name="a"/>。</param>
    public static ClipVertex Lerp(in ClipVertex a, in ClipVertex b, float t)
    {
        Vector3 normal = Vector3.Lerp(a.WorldNormal, b.WorldNormal, t);
        float lengthSquared = normal.LengthSquared();
        if (lengthSquared > float.Epsilon)
        {
            normal /= MathF.Sqrt(lengthSquared);
        }

        return new ClipVertex(
            Vector4.Lerp(a.ClipPosition, b.ClipPosition, t),
            Vector3.Lerp(a.WorldPosition, b.WorldPosition, t),
            normal,
            Vector2.Lerp(a.TexCoord, b.TexCoord, t),
            a.Color.LerpTo(b.Color, t));
    }
}

/// <summary>
/// 已完成透视除法的屏幕空间顶点，是光栅化阶段的输入。
/// </summary>
public struct ProjectedVertex
{
    /// <summary>屏幕 X 坐标（像素，左上角为原点，向右为正）。</summary>
    public float ScreenX;

    /// <summary>屏幕 Y 坐标（像素，向下为正）。</summary>
    public float ScreenY;

    /// <summary>NDC 深度，-1 为近平面、+1 为远平面。</summary>
    public float Depth;

    /// <summary>齐次坐标 W 的倒数，透视校正插值的关键量。</summary>
    public float InvW;

    /// <summary>世界空间位置。</summary>
    public Vector3 WorldPosition;

    /// <summary>世界空间法线。</summary>
    public Vector3 WorldNormal;

    /// <summary>纹理坐标。</summary>
    public Vector2 TexCoord;

    /// <summary>顶点色。</summary>
    public RgbColor Color;

    /// <summary>坐标是否为可用数值。</summary>
    public readonly bool IsFinite =>
        MathUtil.IsFinite(ScreenX) && MathUtil.IsFinite(ScreenY) &&
        MathUtil.IsFinite(Depth) && MathUtil.IsFinite(InvW) &&
        MathUtil.IsFinite(WorldPosition);

    /// <summary>
    /// 由裁剪顶点构造屏幕顶点。
    /// </summary>
    /// <param name="clip">已确认位于近平面内侧的裁剪顶点。</param>
    /// <param name="viewportWidth">视口宽度。</param>
    /// <param name="viewportHeight">视口高度。</param>
    public static ProjectedVertex FromClip(in ClipVertex clip, int viewportWidth, int viewportHeight)
    {
        float w = clip.ClipPosition.W;
        float invW = MathF.Abs(w) > MathUtil.Epsilon ? 1.0f / w : 0.0f;

        float ndcX = clip.ClipPosition.X * invW;
        float ndcY = clip.ClipPosition.Y * invW;
        float ndcZ = clip.ClipPosition.Z * invW;

        return new ProjectedVertex
        {
            // NDC 的 Y 轴向上，屏幕的 Y 轴向下，因此这里做一次翻转
            ScreenX = ((ndcX * 0.5f) + 0.5f) * viewportWidth,
            ScreenY = (0.5f - (ndcY * 0.5f)) * viewportHeight,
            Depth = ndcZ,
            InvW = invW,
            WorldPosition = clip.WorldPosition,
            WorldNormal = clip.WorldNormal,
            TexCoord = clip.TexCoord,
            Color = clip.Color,
        };
    }
}
