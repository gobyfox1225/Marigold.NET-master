using System.Collections.Generic;
using System.Numerics;
using Marigold.Imaging;
using Marigold.Mathematics;
using Marigold.SceneGraph;

namespace Marigold.Rendering.Shading;

/// <summary>
/// 朗伯漫反射着色器：环境光 + 单方向光 + 雾。
/// </summary>
/// <remarks>
/// <para>
/// 公式：<c>颜色 = 反照率 × (环境光 + 方向光 × max(dot(N, L), 0))</c>，最后叠加雾。
/// </para>
/// <para>
/// 这个模型看似简单，但恰好是 渲染器的美术需求：
/// 环境光分量（0.88，接近全亮）负责把整个房间压成一片均匀的白，
/// 方向光分量（0.85，55° 俯角）只用来给地板、天花板与面对镜头的墙面
/// 制造极轻微的明度差，让三维结构不至于完全消失。
/// 一旦把环境光调低、方向光调高，画面就会立刻变成普通的阴暗走廊，
/// 那种“无处可躲的白”的氛围也随之消失。
/// </para>
/// <para>
/// 因为环境光与方向光都只跟法线有关，而墙面、地面这类平面三角形上的法线是常量，
/// 所以这个着色器几乎总能走 <see cref="Prepare"/> 的快路径：
/// 辐照度每个三角形只算一次，像素循环里只剩贴图与雾。
/// </para>
/// </remarks>
public sealed class LambertShader : ISurfaceShader
{
    /// <summary>构造朗伯着色器。</summary>
    /// <param name="applyFog">是否叠加雾效。</param>
    /// <param name="normalOffsetBias">法线偏移，用于压制共面闪烁。默认 0。</param>
    public LambertShader(bool applyFog = true, float normalOffsetBias = 0.0f)
    {
        ApplyFog = applyFog;
        NormalOffsetBias = normalOffsetBias;
    }

    /// <inheritdoc />
    public string Name => "Lambert";

    /// <summary>是否叠加雾效。</summary>
    public bool ApplyFog { get; set; }

    /// <summary>法线偏移量。</summary>
    public float NormalOffsetBias { get; set; }

    /// <inheritdoc />
    /// <remarks>
    /// 只有「法线恒定、顶点色恒定、且不依赖视线」的三角形才拿得到有效的常量包，
    /// 此时逐像素就只剩贴图采样与雾效两件事。
    /// </remarks>
    public ShadingConstants Prepare(in SurfaceFragment sample)
    {
        Material material = sample.Material;

        // 法线偏移与双面翻转都要用逐像素的视线方向，常量包无从谈起
        if (NormalOffsetBias > 0.0f || material.DoubleSided)
        {
            return ShadingConstants.None;
        }

        return new ShadingConstants(sample.NormalizedNormal, ComputeRadiance(sample), sample.Albedo);
    }

    /// <inheritdoc />
    public RgbColor Shade(in SurfaceFragment fragment, in ShadingConstants constants)
    {
        // 常量包可用时，辐照度与反照率都已算好；否则退回逐像素重算
        RgbColor radiance = constants.Valid ? constants.Radiance : ComputeRadiance(fragment);
        RgbColor albedo = constants.Valid ? constants.Albedo : fragment.Albedo;

        RgbColor color = ApplyTexture(fragment, albedo) * radiance;

        // 把材质不透明度与顶点色 alpha 写入输出，供光栅化器做混合判定
        float alpha = fragment.Material.Opacity * fragment.VertexColor.A;
        color = new RgbColor(color.R, color.G, color.B, alpha);

        return ApplyFog ? SurfaceShading.WithFog(fragment, color) : color;
    }

    /// <summary>
    /// 计算片元处的辐照度：环境光 + 主方向光 + 附加方向光。
    /// </summary>
    /// <param name="fragment">片元输入。</param>
    private RgbColor ComputeRadiance(in SurfaceFragment fragment)
    {
        LightingSettings lighting = fragment.Context.Lighting;

        // 环境光：与法线无关的常量项
        RgbColor radiance = lighting.AmbientTerm;

        if (fragment.Material.AcceptsLighting)
        {
            Vector3 normal = SurfaceShading.FaceForward(fragment);
            if (NormalOffsetBias > 0.0f)
            {
                normal = SurfaceShading.SafeNormalize(
                    normal + (fragment.ViewDirection * NormalOffsetBias), normal);
            }

            // 主方向光
            float lambert = Vector3.Dot(normal, lighting.DirectionToSun);
            if (lambert > 0.0f)
            {
                radiance += lighting.SunTerm * lambert;
            }

            // 附加方向光：逐盏累加朗伯贡献
            List<DirectionalLight> extras = lighting.AdditionalLights;
            if (extras is { Count: > 0 })
            {
                for (int i = 0; i < extras.Count; i++)
                {
                    DirectionalLight light = extras[i];
                    float dot = Vector3.Dot(normal, light.DirectionToLight);
                    if (dot > 0.0f)
                    {
                        radiance += light.Term * dot;
                    }
                }
            }
        }

        return radiance;
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
    public override string ToString() => $"LambertShader(fog={ApplyFog})";
}
