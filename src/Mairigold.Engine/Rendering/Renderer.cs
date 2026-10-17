using System.Diagnostics;
using System.Numerics;
using Marigold.Geometry;
using Marigold.Mathematics;
using Marigold.Rendering.Shading;
using Marigold.SceneGraph;

namespace Marigold.Rendering;

/// <summary>一帧渲染的统计数据。</summary>
/// <param name="SubmittedInstances">提交的可渲染实例数。</param>
/// <param name="CulledInstances">被视锥剔除的实例数。</param>
/// <param name="SubmittedTriangles">提交的三角形数。</param>
/// <param name="ClippedTriangles">被裁剪阶段丢弃的三角形数。</param>
/// <param name="CulledTriangles">被背面剔除的三角形数。</param>
/// <param name="DegenerateTriangles">退化或被视口完全排除的三角形数。</param>
/// <param name="DrawnTriangles">实际绘制出像素的三角形数。</param>
/// <param name="ShadedPixels">被着色器处理的像素数。</param>
/// <param name="ElapsedMilliseconds">本帧耗时（毫秒）。</param>
public readonly record struct RenderStatistics(
    int SubmittedInstances,
    int CulledInstances,
    int SubmittedTriangles,
    int ClippedTriangles,
    int CulledTriangles,
    int DegenerateTriangles,
    int DrawnTriangles,
    long ShadedPixels,
    double ElapsedMilliseconds)
{
    /// <summary>本帧绘制的三角形占提交量的比例。</summary>
    public float DrawRatio => SubmittedTriangles == 0 ? 0.0f : DrawnTriangles / (float)SubmittedTriangles;

    /// <inheritdoc />
    public override string ToString() =>
        $"实例 {SubmittedInstances}(剔 {CulledInstances}) / 三角形 提交 {SubmittedTriangles}" +
        $"(视图裁剪 {ClippedTriangles}, 背面剔除 {CulledTriangles}, 退化 {DegenerateTriangles}, 绘出 {DrawnTriangles})" +
        $" / 像素 {ShadedPixels} / {ElapsedMilliseconds:0.##} ms";
}

/// <summary>
/// 渲染器：编排完整的一帧。
/// </summary>
/// <remarks>
/// <para>
/// 每帧的处理顺序，也是软件渲染器的标准流程：
/// </para>
/// <list type="number">
/// <item><description><b>刷新世界矩阵</b>——场景图自顶向下连乘一次，之后全程复用。</description></item>
/// <item><description><b>清屏</b>——颜色填背景色，深度复位到远平面。</description></item>
/// <item><description><b>构造视锥</b>——从视图投影矩阵提取六个平面，用于整块剔除不可见对象。</description></item>
/// <item><description><b>逐实例</b>——包围盒剔除 → 世界变换 → 逐三角形：近平面裁剪 → 透视除法 → 背面剔除 → 光栅化。</description></item>
/// </list>
/// <para>
/// 相比“顶点投影 + 手动连线”的简单做法，这一流程补上了使其成为<b>渲染器</b>
/// 而非<b>投影演示</b>的三块拼图：剔除（省算力）、裁剪（防穿帮）、深度缓冲（正确遮挡）。
/// </para>
/// </remarks>
public sealed class Renderer
{
    private readonly Dictionary<MaterialShadingModel, ISurfaceShader> _shaders = [];
    private readonly Rasterizer _rasterizer;
    private readonly List<MeshInstance> _instanceBuffer = [];

    /// <summary>构造渲染器。</summary>
    /// <param name="settings">渲染设置。传 <c>null</c> 使用默认设置。</param>
    public Renderer(RenderSettings? settings = null)
    {
        Settings = settings ?? new RenderSettings();
        _rasterizer = new Rasterizer(Settings);

        _shaders[MaterialShadingModel.Lambert] = new LambertShader();
        _shaders[MaterialShadingModel.Unlit] = new UnlitShader();
    }

    /// <summary>渲染设置。可在帧间随时修改。</summary>
    public RenderSettings Settings { get; }

    /// <summary>最近一帧的统计信息。</summary>
    public RenderStatistics LastStatistics { get; private set; }

    /// <summary>已渲染的帧数。</summary>
    public long FrameCount { get; private set; }

    /// <summary>底层光栅化器，暴露出来是为了让上层能读取更细的统计。</summary>
    public Rasterizer Rasterizer => _rasterizer;

    /// <summary>替换某一种着色模型对应的着色器实现。</summary>
    public void SetShader(MaterialShadingModel model, ISurfaceShader shader)
    {
        ArgumentNullException.ThrowIfNull(shader);

        if (shader is LambertShader lambert)
        {
            lambert.ApplyFog = Settings.Fog.Enabled;
        }
        else if (shader is UnlitShader unlit)
        {
            unlit.ApplyFog = Settings.Fog.Enabled;
        }

        _shaders[model] = shader;
    }

    /// <summary>取得某种着色模型对应的着色器。</summary>
    public ISurfaceShader GetShader(MaterialShadingModel model) =>
        _shaders.TryGetValue(model, out ISurfaceShader? shader) ? shader : _shaders[MaterialShadingModel.Lambert];

    /// <summary>
    /// 渲染一帧。
    /// </summary>
    /// <param name="scene">待渲染的场景。</param>
    /// <param name="camera">相机。</param>
    /// <param name="target">输出渲染目标。可为内置 <see cref="FrameBuffer"/>，也可为自定义 <see cref="IRenderTarget"/> 实现。</param>
    /// <exception cref="ArgumentNullException">任一参数为 null。</exception>
    public void Render(Scene scene, Camera camera, IRenderTarget target)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentNullException.ThrowIfNull(target);

        Stopwatch stopwatch = Stopwatch.StartNew();

        // 1. 刷新世界矩阵，并清空输出
        scene.UpdateWorldTransforms();
        target.Clear(Settings.BackgroundColor);
        _rasterizer.ResetStatistics();

        // 2. 构造相机矩阵与视锥
        Matrix4x4 viewProjection = camera.GetViewProjectionMatrix(target.AspectRatio);
        Frustum frustum = Frustum.FromViewProjection(viewProjection);
        ShadingContext context = new(camera.Position, Settings.Lighting, Settings.Fog);

        int submittedInstances = 0;
        int culledInstances = 0;
        int submittedTriangles = 0;
        int clippedTriangles = 0;

        // 裁剪输出缓冲只分配一次，在整帧内复用。
        Span<ClipVertex> clipPolygon = stackalloc ClipVertex[TriangleClipper.MaxClippedVertices];

        // 3. 逐实例、逐三角形
        scene.CollectMeshInstances(_instanceBuffer);

        foreach (MeshInstance instance in _instanceBuffer)
        {
            submittedInstances++;

            Matrix4x4 world = instance.WorldMatrix;

            if (Settings.ObjectCulling)
            {
                BoundingBox worldBounds = instance.Mesh.Bounds.Transform(world);
                if (!frustum.Intersects(worldBounds))
                {
                    culledInstances++;
                    continue;
                }
            }

            Mesh mesh = instance.Mesh;
            Material material = instance.Material;
            ISurfaceShader shader = GetShader(material.ShadingModel);

            // 逐实例 UV 变换：让同一份立方体网格在不同尺寸的板件上
            // 都能保持一致的纹理密度（见 MeshInstance.SetUvFromWorldSize）
            Vector2 uvScale = instance.UvScale;
            Vector2 uvOffset = instance.UvOffset;

            Matrix4x4 normalMatrix = world;
            if (Matrix4x4.Invert(world, out Matrix4x4 inverseWorld))
            {
                // 法线用逆转置矩阵变换：非等比缩放下仍保持垂直于表面
                normalMatrix = Matrix4x4.Transpose(inverseWorld);
            }

            ReadOnlySpan<Vertex> vertices = mesh.Vertices;
            ReadOnlySpan<int> indices = mesh.Indices;

            for (int i = 0; i + 2 < indices.Length; i += 3)
            {
                submittedTriangles++;

                ClipVertex a = ToClipVertex(vertices[indices[i]], world, normalMatrix, viewProjection, uvScale, uvOffset);
                ClipVertex b = ToClipVertex(vertices[indices[i + 1]], world, normalMatrix, viewProjection, uvScale, uvOffset);
                ClipVertex c = ToClipVertex(vertices[indices[i + 2]], world, normalMatrix, viewProjection, uvScale, uvOffset);

                // 快速拒绝：整块在近平面之外，或整块超出远平面
                if (TriangleClipper.IsFullyOutsideNearPlane(a, b, c) ||
                    TriangleClipper.IsFullyOutsideFarPlane(a, b, c))
                {
                    clippedTriangles++;
                    continue;
                }

                if (TriangleClipper.IsFullyInsideNearPlane(a, b, c))
                {
                    // 快速路径：无需裁剪，直接投影。
                    // 逐三角形的成败由光栅化器统一累计，这里不需要逐个判断返回值。
                    DrawProjected(target, a, b, c, material, shader, context);
                }
                else
                {
                    int count = TriangleClipper.ClipAgainstNearPlane(a, b, c, clipPolygon);
                    clippedTriangles++;

                    // 裁剪结果是凸多边形，按三角扇还原为三角形，绕序保持不变
                    for (int k = 1; k + 1 < count; k++)
                    {
                        DrawProjected(target, clipPolygon[0], clipPolygon[k], clipPolygon[k + 1], material, shader, context);
                    }
                }
            }
        }

        stopwatch.Stop();
        FrameCount++;

        LastStatistics = new RenderStatistics(
            submittedInstances,
            culledInstances,
            submittedTriangles,
            clippedTriangles,
            (int)_rasterizer.CulledTriangleCount,
            (int)_rasterizer.DegenerateTriangleCount,
            (int)_rasterizer.RasterizedTriangleCount,
            _rasterizer.ShadedPixelCount,
            stopwatch.Elapsed.TotalMilliseconds);
    }

    /// <summary>把顶点变换到裁剪空间，并顺带施加逐实例的 UV 缩放与偏移。</summary>
    /// <param name="vertex">输入顶点。</param>
    /// <param name="world">世界矩阵。</param>
    /// <param name="normalMatrix">法线矩阵（世界矩阵的逆转置）。</param>
    /// <param name="viewProjection">视图投影矩阵。</param>
    /// <param name="uvScale">UV 缩放。</param>
    /// <param name="uvOffset">UV 偏移。</param>
    private static ClipVertex ToClipVertex(
        in Vertex vertex,
        Matrix4x4 world,
        Matrix4x4 normalMatrix,
        Matrix4x4 viewProjection,
        Vector2 uvScale,
        Vector2 uvOffset)
    {
        Vector3 worldPosition = Vector3.Transform(vertex.Position, world);

        Vector3 normal = Vector3.TransformNormal(vertex.Normal, normalMatrix);
        float lengthSquared = normal.LengthSquared();
        if (lengthSquared > float.Epsilon)
        {
            normal /= MathF.Sqrt(lengthSquared);
        }

        Vector4 clipPosition = Vector4.Transform(new Vector4(worldPosition, 1.0f), viewProjection);

        Vector2 uv = new(
            (vertex.TexCoord.X * uvScale.X) + uvOffset.X,
            (vertex.TexCoord.Y * uvScale.Y) + uvOffset.Y);

        return new ClipVertex(clipPosition, worldPosition, normal, uv, vertex.Color);
    }

    /// <summary>投影三个裁剪顶点并光栅化。</summary>
    private void DrawProjected(
        IRenderTarget target,
        in ClipVertex a,
        in ClipVertex b,
        in ClipVertex c,
        Material material,
        ISurfaceShader shader,
        in ShadingContext context)
    {
        ProjectedVertex p0 = ProjectedVertex.FromClip(a, target.Width, target.Height);
        ProjectedVertex p1 = ProjectedVertex.FromClip(b, target.Width, target.Height);
        ProjectedVertex p2 = ProjectedVertex.FromClip(c, target.Width, target.Height);

        _rasterizer.DrawTriangle(target, p0, p1, p2, material, shader, context);
    }

    /// <inheritdoc />
    public override string ToString() =>
        $"Renderer(frames={FrameCount}) {{ {LastStatistics} }}";
}
