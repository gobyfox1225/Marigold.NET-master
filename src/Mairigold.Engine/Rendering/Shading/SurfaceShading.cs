using System.Numerics;
using Marigold.Imaging;

namespace Marigold.Rendering.Shading;

/// <summary>
/// 各着色器共用的辅助计算。
/// </summary>
internal static class SurfaceShading
{
    /// <summary>
    /// 按材质是否双面，把法线翻转到朝向相机的一侧。
    /// </summary>
    /// <remarks>
    /// 双面材质从背面观察时，几何法线朝向观察者之外，若不翻转会得到全黑或全平的结果。
    /// 单面材质的背面本就被剔除，因此无需处理。
    /// </remarks>
    internal static Vector3 FaceForward(in SurfaceFragment fragment)
    {
        Vector3 normal = fragment.NormalizedNormal;
        if (!fragment.Material.DoubleSided)
        {
            return normal;
        }

        return Vector3.Dot(normal, fragment.ViewDirection) >= 0.0f ? normal : -normal;
    }

    /// <summary>对颜色叠加雾效。</summary>
    internal static RgbColor WithFog(in SurfaceFragment fragment, RgbColor color) =>
        fragment.Context.Fog.Apply(color, fragment.DistanceToCamera);

    /// <summary>把向量归一化，长度过小时返回 <paramref name="fallback"/>。</summary>
    internal static Vector3 SafeNormalize(Vector3 value, Vector3 fallback)
    {
        float lengthSquared = value.LengthSquared();
        return lengthSquared > float.Epsilon ? value / MathF.Sqrt(lengthSquared) : fallback;
    }
}
