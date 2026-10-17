using Marigold.Imaging;
using Marigold.Mathematics;

namespace Marigold.Rendering.Shading;

/// <summary>
/// 无光照着色器：直接输出反照率，可选叠加雾。
/// </summary>
/// <remarks>
/// 适合调试（观察几何形状与法线是否正确）以及 UI、自发光表面。
/// 排查“画面全黑”这类问题时，第一步通常就是把着色器换成它：
/// 若换成无光照后画面正常，说明几何与投影没问题，问题出在光照参数上。
/// </remarks>
public sealed class UnlitShader : ISurfaceShader
{
    /// <summary>构造无光照着色器。</summary>
    /// <param name="applyFog">是否叠加雾效。</param>
    public UnlitShader(bool applyFog = true)
    {
        ApplyFog = applyFog;
    }

    /// <inheritdoc />
    public string Name => "Unlit";

    /// <summary>是否叠加雾效。</summary>
    public bool ApplyFog { get; set; }

    /// <inheritdoc />
    /// <remarks>
    /// 无光照的结果与法线、视线都无关，因此永远可以预计算反照率，
    /// 逐像素只剩贴图采样与雾效。
    /// </remarks>
    public ShadingConstants Prepare(in SurfaceFragment sample) =>
        new(sample.NormalizedNormal, RgbColor.White, sample.Albedo);

    /// <inheritdoc />
    public RgbColor Shade(in SurfaceFragment fragment, in ShadingConstants constants)
    {
        RgbColor albedo = constants.Valid ? constants.Albedo : fragment.Albedo;
        RgbColor color = ApplyTexture(fragment, albedo);

        // 把材质不透明度与顶点色 alpha 写入输出，供光栅化器做混合判定
        float alpha = fragment.Material.Opacity * fragment.VertexColor.A;
        color = new RgbColor(color.R, color.G, color.B, alpha);

        return ApplyFog ? SurfaceShading.WithFog(fragment, color) : color;
    }

    /// <summary>
    /// 把贴图采样结果与已算好的反照率按材质强度混合。
    /// </summary>
    /// <param name="fragment">片元输入，提供贴图与纹理坐标。</param>
    /// <param name="untextured">不含贴图的反照率。</param>
    private static RgbColor ApplyTexture(in SurfaceFragment fragment, RgbColor untextured)
    {
        Texture2D? texture = fragment.Material.Texture;
        if (texture is null)
        {
            return untextured;
        }

        float strength = fragment.Material.TexturedAlbedo;
        if (strength <= 0.0f)
        {
            return untextured;
        }

        RgbColor texel = texture.SampleFiltered(fragment.TexCoord.X, fragment.TexCoord.Y);
        return new RgbColor(
            untextured.R * MathUtil.Lerp(1.0f, texel.R, strength),
            untextured.G * MathUtil.Lerp(1.0f, texel.G, strength),
            untextured.B * MathUtil.Lerp(1.0f, texel.B, strength));
    }

    /// <inheritdoc />
    public override string ToString() => $"UnlitShader(fog={ApplyFog})";
}
