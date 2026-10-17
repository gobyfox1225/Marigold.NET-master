# Mairigold.NET

通用型纯 CPU 软件光栅渲染引擎。零第三方依赖，`net8.0`，跨平台。

## 特性

- **完整软件光栅化管线**：视锥剔除、齐次裁剪、透视校正插值、深度缓冲、背面剔除
- **双投影模式**：透视（FOV）与正交（OrthographicSize）
- **多方向光**：主方向光 + 附加方向光列表，朗伯漫反射累加
- **透明混合**：Opaque / AlphaBlend（over 运算）/ AlphaTest（硬边镂空）
- **可插拔着色器**：两段式 `ISurfaceShader`（Prepare 每三角形 + Shade 每像素）
- **渲染目标抽象**：`IRenderTarget` 接口，可直出 WPF / GDI+ / 自定义缓冲
- **自包含图像编解码**：PNG / BMP / PPM 编码 + PNG 解码，仅依赖 .NET 内置 `ZLibStream`
- **行带并行光栅化**：按屏幕行切分多线程，无锁深度测试
- **平面快路径**：检测三角形三点法线一致后，光照与法线归一化提到循环外

## 项目结构

```
Mairigold.NET-master/
├── Mairigold.NET.sln
├── Directory.Build.props
├── src/
│   └── Mairigold.Engine/          # 引擎本体（Library）
│       ├── Mathematics/            # Transform3D, MathUtil
│       ├── Imaging/                # RgbColor(RGBA), Texture2D, PNG编解码, ImageWriter
│       ├── Geometry/               # Mesh, MeshBuilder, PrimitiveMeshes, PolygonExtruder
│       ├── Rendering/              # Renderer, Rasterizer, Camera, FrameBuffer, IRenderTarget
│       │   └── Shading/            # ISurfaceShader, LambertShader, UnlitShader
│       └── SceneGraph/             # Scene, SceneNode, MeshInstance, Material
└── samples/
    └── Mairigold.Samples/          # 示例程序（Exe）
```

## 快速开始

```csharp
using Marigold.Geometry;
using Marigold.Imaging;
using Marigold.Mathematics;
using Marigold.Rendering;
using Marigold.SceneGraph;
using System.Numerics;

// 1. 构建场景
Scene scene = new("Demo");
Mesh cube = PrimitiveMeshes.Box(new Vector3(2, 2, 2));
Material mat = Material.Lambert(new RgbColor(0.8f, 0.3f, 0.3f));
scene.Root.Add(new MeshInstance("cube", cube, mat));

// 2. 渲染器与相机（透视）
Renderer renderer = new(RenderSettings.CreateDefault());
Camera cam = Camera.FromLookAt(new Vector3(0, 3, -10), Vector3.Zero);

// 3. 渲染一帧并保存
FrameBuffer fb = new(640, 360);
renderer.Render(scene, cam, fb);
ImageWriter.Write(fb, "output.png", 1.0f / 2.2f);
```

## 关键 API

### 投影模式

```csharp
// 透视（默认）
Camera cam = new() { FieldOfViewDegrees = 78f };

// 正交
Camera cam = new()
{
    Projection = ProjectionMode.Orthographic,
    OrthographicSize = 10f,  // 垂直半高（世界单位）
};
```

### 多光源

```csharp
RenderSettings settings = RenderSettings.CreateDefault();
settings.Lighting.SunColor = new RgbColor(1.0f, 0.9f, 0.75f);
settings.Lighting.SunIntensity = 0.8f;
settings.Lighting.AddLight(new RgbColor(0.5f, 0.65f, 1.0f), 0.45f, new Vector3(0.6f, -0.4f, 0.6f));
```

### 透明混合

```csharp
Material glass = Material.Lambert(new RgbColor(0.4f, 0.7f, 1.0f));
glass.BlendMode = BlendMode.AlphaBlend;
glass.Opacity = 0.5f;

Material fence = Material.Lambert(RgbColor.White);
fence.BlendMode = BlendMode.AlphaTest;
fence.AlphaCutoff = 0.5f;
```

### 自定义渲染目标

```csharp
// 实现 IRenderTarget，把渲染结果写入任意载体
class MyTarget : IRenderTarget { /* ... */ }

MyTarget target = new(640, 360);
renderer.Render(scene, cam, target);  // 不再要求 FrameBuffer
```

### 自定义着色器

```csharp
public sealed class ToonShader : ISurfaceShader
{
    public string Name => "Toon";

    public ShadingConstants Prepare(in SurfaceFragment sample) => ShadingConstants.None;

    public RgbColor Shade(in SurfaceFragment fragment, in ShadingConstants constants)
    {
        Vector3 normal = SurfaceShading.FaceForward(fragment);
        float nDotL = Vector3.Dot(normal, fragment.Context.Lighting.DirectionToSun);
        // 阶梯化光照
        float band = nDotL > 0.5f ? 1.0f : nDotL > 0.0f ? 0.6f : 0.3f;
        return fragment.Albedo * fragment.Context.Lighting.AmbientTerm * band;
    }
}

renderer.SetShader(MaterialShadingModel.Lambert, new ToonShader());
```

## 性能特性

- Debug 下引擎本体强制 `Optimize=true`（光栅化热路径依赖内联）
- 平面三角形快路径：检测三点法线一致后，光照提到循环外
- 行带并行：`RasterThreads > 1` 时按屏幕行切分，无锁深度测试
- 零堆分配的三角形裁剪（stackalloc）
- 连续内存帧缓冲（一维数组 + 行主序索引）

## 编译

```bash
dotnet build Mairigold.NET.sln -c Release
dotnet run --project samples/Mairigold.Samples -c Release
```

## 许可证

MIT
