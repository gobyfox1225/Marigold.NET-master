using System.Numerics;
using Marigold.Mathematics;

namespace Marigold.Rendering;

/// <summary>
/// 旧式屏幕投影的回归对照实现。
/// </summary>
/// <remarks>
/// <para>
/// <b>用途</b>：这不是引擎的主投影路径，而是<b>回归对照基准</b>。
/// 保留一份完全等价（包括全部魔法常数与运算顺序）的实现，
/// 可以在重构过程中随时验证“新渲染器画出来的东西和当初的是不是同一个东西”，
/// 也方便单元测试用一个已知输入去锁定行为。
/// </para>
/// <para>
/// 引擎正式渲染请使用 <see cref="Camera"/>：它基于标准的视图 / 投影矩阵，
/// 支持任意朝向、正确的近远裁剪与深度测试，而本类的实现存在如下固有限制：
/// </para>
/// <list type="bullet">
/// <item><description>只支持绕 Y 再绕 X 的两轴旋转，且旋转轴固定在世界原点；</description></item>
/// <item><description>焦距与深度偏移是硬编码的常数（350 / 150），无法通过参数描述 FOV；</description></item>
/// <item><description>没有近平面裁剪，相机后方的顶点会被投影到前景（“镜像翻转”），也没有深度缓冲；</description></item>
/// <item><description>屏幕 Y 轴方向为向下，与图像行号一致，但未做视口缩放。</description></item>
/// </list>
/// </remarks>
public static class LegacyProjection
{
    /// <summary>焦距常数（<c>distance = 350</c>）。</summary>
    public const float DefaultFocalDistance = 350.0f;

    /// <summary>深度偏移常数（<c>+ 150</c>）。</summary>
    public const float DefaultDepthOffset = 150.0f;

    /// <summary>默认屏幕宽度（380）。</summary>
    public const float DefaultScreenWidth = 380.0f;

    /// <summary>默认屏幕高度（540）。</summary>
    public const float DefaultScreenHeight = 540.0f;

    /// <summary>
    /// 把三维点投影到屏幕坐标。
    /// </summary>
    /// <param name="point">待投影的点。</param>
    /// <param name="rotationXDegrees">绕 X 轴旋转角度（角度制）。</param>
    /// <param name="rotationYDegrees">绕 Y 轴旋转角度（角度制）。</param>
    /// <param name="screenWidth">屏幕宽度。</param>
    /// <param name="screenHeight">屏幕高度。</param>
    /// <param name="focalDistance">焦距（透视强度），越大越接近正交投影。</param>
    /// <param name="depthOffset">深度偏移，把投影中心沿 +Z 推开，等效于相机后退。</param>
    /// <returns>屏幕坐标，Y 轴向下。</returns>
    public static Vector2 Project(
        Vector3 point,
        float rotationXDegrees,
        float rotationYDegrees,
        float screenWidth = DefaultScreenWidth,
        float screenHeight = DefaultScreenHeight,
        float focalDistance = DefaultFocalDistance,
        float depthOffset = DefaultDepthOffset)
    {
        float radiansX = MathUtil.ToRadians(rotationXDegrees);
        float radiansY = MathUtil.ToRadians(rotationYDegrees);

        // 1. 绕 Y 轴旋转
        float x1 = (point.X * MathF.Cos(radiansY)) + (point.Z * MathF.Sin(radiansY));
        float y1 = point.Y;
        float z1 = (-point.X * MathF.Sin(radiansY)) + (point.Z * MathF.Cos(radiansY));

        // 2. 绕 X 轴旋转
        float x2 = x1;
        float y2 = (y1 * MathF.Cos(radiansX)) - (z1 * MathF.Sin(radiansX));
        float z2 = (y1 * MathF.Sin(radiansX)) + (z1 * MathF.Cos(radiansX));

        // 3. 透视投影
        float factor = focalDistance / (focalDistance + z2 + depthOffset);

        float centerX = screenWidth * 0.5f;
        float centerY = screenHeight * 0.5f;

        return new Vector2(centerX + (x2 * factor), centerY + (y2 * factor));
    }

    /// <summary>
    /// 投影时使用的缩放因子，便于测试与文档说明。
    /// </summary>
    /// <remarks>
    /// 缩放因子公式：<c>focal / (focal + z2 + offset)</c>。
    /// </remarks>
    public static float ComputeFactor(
        Vector3 point,
        float rotationXDegrees,
        float rotationYDegrees,
        float focalDistance = DefaultFocalDistance,
        float depthOffset = DefaultDepthOffset)
    {
        float radiansX = MathUtil.ToRadians(rotationXDegrees);
        float radiansY = MathUtil.ToRadians(rotationYDegrees);

        float z1 = (-point.X * MathF.Sin(radiansY)) + (point.Z * MathF.Cos(radiansY));
        float z2 = (point.Y * MathF.Sin(radiansX)) + (z1 * MathF.Cos(radiansX));

        return focalDistance / (focalDistance + z2 + depthOffset);
    }
}
