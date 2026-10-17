using System.Diagnostics;
using System.Numerics;
using Marigold.Geometry;
using Marigold.Imaging;
using Marigold.Mathematics;
using Marigold.Rendering;
using Marigold.SceneGraph;

// =============================================================================
// Mairigold.NET 通用渲染引擎示例
// 演示：透视投影、正交投影、半透明混合、多方向光
// =============================================================================

string outputDir = Path.Combine(AppContext.BaseDirectory, "output");
Directory.CreateDirectory(outputDir);

// ---------------------------------------------------------------------------
// 1. 透视投影 + 多光源：一束立方体环 + 两盏方向光（暖色主光 + 冷色辅光）
// ---------------------------------------------------------------------------
Console.WriteLine("=== 1. 透视投影 + 多光源 ===");
{
    Scene scene = new("PerspectiveMultiLight");
    Mesh cube = PrimitiveMeshes.Box(new Vector3(2f, 2f, 2f));
    Mesh ground = PrimitiveMeshes.Plane(40f, 8);

    Material red = Material.Lambert(new RgbColor(0.8f, 0.25f, 0.25f), "red");
    Material grey = Material.Lambert(new RgbColor(0.4f, 0.4f, 0.45f), "grey");

    scene.Root.Add(new MeshInstance("ground", ground, grey));

    for (int i = 0; i < 24; i++)
    {
        var node = new MeshInstance("cube" + i, cube, red);
        float a = i * (MathF.PI * 2 / 24);
        float r = 6f + (i % 3);

        Transform3D t = node.Transform;
        t.Position = new Vector3(MathF.Cos(a) * r, 1f, MathF.Sin(a) * r);
        t.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, a);
        node.Transform = t;
        scene.Root.Add(node);
    }

    RenderSettings settings = RenderSettings.CreateDefault();
    // 暖色主光（右上前方）
    settings.Lighting.SunColor = new RgbColor(1.0f, 0.9f, 0.75f);
    settings.Lighting.SunIntensity = 0.8f;
    settings.Lighting.SunDirection = Vector3.Normalize(new Vector3(-0.5f, -0.7f, -0.5f));
    // 冷色辅光（左后方）
    settings.Lighting.AddLight(
        new RgbColor(0.5f, 0.65f, 1.0f), 0.45f,
        new Vector3(0.6f, -0.4f, 0.6f));

    Renderer renderer = new(settings);
    Camera cam = Camera.FromLookAt(new Vector3(0, 6, -16), new Vector3(0, 1.5f, 0));
    FrameBuffer fb = new(640, 360);

    renderer.Render(scene, cam, fb);
    Console.WriteLine($"  {renderer.LastStatistics}");
    ImageWriter.Write(fb, Path.Combine(outputDir, "01_perspective_multilight.png"), 1.0f / 2.2f);
    Console.WriteLine("  -> output/01_perspective_multilight.png");
}

// ---------------------------------------------------------------------------
// 2. 正交投影：俯视等距视角，无透视收缩
// ---------------------------------------------------------------------------
Console.WriteLine("=== 2. 正交投影（俯视）===");
{
    Scene scene = new("Orthographic");
    Mesh cube = PrimitiveMeshes.Box(new Vector3(2f, 2f, 2f));
    Material mat = Material.Lambert(new RgbColor(0.3f, 0.7f, 0.4f), "green");

    // 网格状立方体阵列
    for (int x = -3; x <= 3; x++)
    {
        for (int z = -3; z <= 3; z++)
        {
            if (x == 0 && z == 0) continue;
            var node = new MeshInstance($"c{x}_{z}", cube, mat);
            Transform3D t = node.Transform;
            t.Position = new Vector3(x * 4f, 1f, z * 4f);
            t.Scale = new Vector3(1f, 1f + (((x + z + 6) % 4) * 0.5f), 1f);
            node.Transform = t;
            scene.Root.Add(node);
        }
    }

    RenderSettings settings = RenderSettings.CreateDefault();
    Renderer renderer = new(settings);

    Camera cam = new()
    {
        Projection = ProjectionMode.Orthographic,
        OrthographicSize = 18f,
        Position = new Vector3(20, 20, -20),
        NearPlane = 0.1f,
        FarPlane = 200f,
    };
    cam.LookAt(Vector3.Zero);

    FrameBuffer fb = new(640, 360);
    renderer.Render(scene, cam, fb);
    Console.WriteLine($"  {renderer.LastStatistics}");
    ImageWriter.Write(fb, Path.Combine(outputDir, "02_orthographic.png"), 1.0f / 2.2f);
    Console.WriteLine("  -> output/02_orthographic.png");
}

// ---------------------------------------------------------------------------
// 3. 半透明混合：玻璃立方体在实心立方体前方
// ---------------------------------------------------------------------------
Console.WriteLine("=== 3. 半透明混合 ===");
{
    Scene scene = new("AlphaBlend");
    Mesh cube = PrimitiveMeshes.Box(new Vector3(3f, 3f, 3f));

    // 不透明背景立方体
    Material opaque = Material.Lambert(new RgbColor(0.85f, 0.6f, 0.2f), "orange");
    scene.Root.Add(new MeshInstance("opaque", cube, opaque));

    // 半透明玻璃立方体（在右前方）
    Material glass = Material.Lambert(new RgbColor(0.4f, 0.7f, 1.0f), "glass");
    glass.BlendMode = BlendMode.AlphaBlend;
    glass.Opacity = 0.5f;

    var glassNode = new MeshInstance("glass", cube, glass);
    Transform3D gt = glassNode.Transform;
    gt.Position = new Vector3(2.5f, 0f, -2.5f);
    glassNode.Transform = gt;
    scene.Root.Add(glassNode);

    // 第二块半透明（红色，更不透明）
    Material redTint = Material.Lambert(new RgbColor(1.0f, 0.3f, 0.3f), "redGlass");
    redTint.BlendMode = BlendMode.AlphaBlend;
    redTint.Opacity = 0.35f;

    var redNode = new MeshInstance("redGlass", cube, redTint);
    Transform3D rt = redNode.Transform;
    rt.Position = new Vector3(-2.5f, 0f, -2.5f);
    redNode.Transform = rt;
    scene.Root.Add(redNode);

    RenderSettings settings = RenderSettings.CreateDefault();
    settings.BackgroundColor = new RgbColor(0.1f, 0.1f, 0.12f);
    Renderer renderer = new(settings);

    Camera cam = Camera.FromLookAt(new Vector3(8, 4, -12), new Vector3(0, 0, 0));
    FrameBuffer fb = new(640, 360);
    renderer.Render(scene, cam, fb);
    Console.WriteLine($"  {renderer.LastStatistics}");
    ImageWriter.Write(fb, Path.Combine(outputDir, "03_alpha_blend.png"), 1.0f / 2.2f);
    Console.WriteLine("  -> output/03_alpha_blend.png");
}

// ---------------------------------------------------------------------------
// 4. Alpha 测试：栅栏状立方体（硬边镂空）
// ---------------------------------------------------------------------------
Console.WriteLine("=== 4. Alpha 测试（硬边镂空）===");
{
    Scene scene = new("AlphaTest");
    Mesh cube = PrimitiveMeshes.Box(new Vector3(2f, 2f, 2f));
    Mesh ground = PrimitiveMeshes.Plane(30f, 4);
    scene.Root.Add(new MeshInstance("ground", ground, Material.Lambert(new RgbColor(0.3f, 0.3f, 0.32f), "g")));

    // 用顶点色 alpha 制造棋盘格镂空：偶数实例 alpha=1，奇数 alpha=0
    for (int x = -4; x <= 4; x++)
    {
        int row = x + 4;
        var node = new MeshInstance("fence" + x, cube, Material.Lambert(RgbColor.White, "fence"));
        Transform3D t = node.Transform;
        t.Position = new Vector3(x * 2f, 1f, 0f);
        t.Scale = new Vector3(0.4f, 1f, 2f);
        node.Transform = t;

        // 偶数柱保留，奇数柱通过材质 alpha 测试丢弃
        bool visible = (row % 2) == 0;
        node.Material.BlendMode = BlendMode.AlphaTest;
        node.Material.AlphaCutoff = 0.5f;
        node.Material.Opacity = visible ? 1.0f : 0.0f;

        scene.Root.Add(node);
    }

    RenderSettings settings = RenderSettings.CreateDefault();
    Renderer renderer = new(settings);
    Camera cam = Camera.FromLookAt(new Vector3(0, 3, -14), new Vector3(0, 1, 0));
    FrameBuffer fb = new(640, 360);
    renderer.Render(scene, cam, fb);
    Console.WriteLine($"  {renderer.LastStatistics}");
    ImageWriter.Write(fb, Path.Combine(outputDir, "04_alpha_test.png"), 1.0f / 2.2f);
    Console.WriteLine("  -> output/04_alpha_test.png");
}

// ---------------------------------------------------------------------------
// 5. 自定义渲染目标：把渲染结果直接写入外部数组（演示 IRenderTarget）
// ---------------------------------------------------------------------------
Console.WriteLine("=== 5. 自定义渲染目标（IRenderTarget）===");
{
    Scene scene = new("CustomTarget");
    Mesh cube = PrimitiveMeshes.Box(new Vector3(4f, 4f, 4f));
    scene.Root.Add(new MeshInstance("cube", cube, Material.Lambert(new RgbColor(0.9f, 0.5f, 0.1f), "amber")));

    // 自定义渲染目标：直接写入调用方提供的数组，无需 FrameBuffer
    ArrayRenderTarget target = new(320, 180);

    Renderer renderer = new(RenderSettings.CreateDefault());
    Camera cam = Camera.FromLookAt(new Vector3(6, 5, -10), Vector3.Zero);
    renderer.Render(scene, cam, target);

    Console.WriteLine($"  渲染到外部数组: {renderer.LastStatistics}");
    Console.WriteLine($"  目标尺寸: {target.Width}x{target.Height}, 像素数: {target.ExternalPixels.Length}");

    // 把外部数组包装回 FrameBuffer 以便写出
    FrameBuffer wrapped = target.ToFrameBuffer();
    ImageWriter.Write(wrapped, Path.Combine(outputDir, "05_custom_target.png"), 1.0f / 2.2f);
    Console.WriteLine("  -> output/05_custom_target.png");
}

Console.WriteLine();
Console.WriteLine("全部示例完成。输出目录: " + outputDir);

// =============================================================================
// 自定义渲染目标实现：把渲染结果直接写入外部托管数组
// =============================================================================
sealed class ArrayRenderTarget : IRenderTarget
{
    private readonly RgbColor[] _color;
    private readonly float[] _depth;

    public ArrayRenderTarget(int width, int height)
    {
        Width = width;
        Height = height;
        _color = new RgbColor[width * height];
        _depth = new float[width * height];
        ClearDepth();
    }

    public int Width { get; }
    public int Height { get; }
    public float AspectRatio => Width / (float)Height;
    public Span<RgbColor> ColorPixels => _color;
    public Span<float> DepthPixels => _depth;

    public RgbColor[] ExternalPixels => _color;

    public void Clear(RgbColor color)
    {
        _color.AsSpan().Fill(color);
        ClearDepth();
    }

    public void ClearDepth(float depth = 1.0f) => _depth.AsSpan().Fill(depth);
    public void ClearColor(RgbColor color) => _color.AsSpan().Fill(color);

    public FrameBuffer ToFrameBuffer()
    {
        FrameBuffer fb = new(Width, Height);
        _color.AsSpan().CopyTo(fb.ColorPixels);
        return fb;
    }
}
