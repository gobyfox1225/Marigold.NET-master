using System.Numerics;
using Marigold.Imaging;

namespace Marigold.Rendering;

/// <summary>
/// 渲染目标抽象：定义光栅化器写入像素所需的最小契约。
/// </summary>
/// <remarks>
/// <para>
/// 把渲染目标抽成接口，是为了让引擎不再硬绑定具体的 <see cref="FrameBuffer"/> 类型。
/// 调用方可以实现本接口，把渲染结果直接写入任意载体：
/// </para>
/// <list type="bullet">
/// <item><description>WPF 的 <c>WriteableBitmap</c> 后备缓冲；</description></item>
/// <item><description>GDI+ 的 <c>Bitmap</c> 或裸 <c>byte[]</c>；</description></item>
/// <item><description>自定义的浮点 HDR 缓冲或分块缓冲。</description></item>
/// </list>
/// <para>
/// 实现约定：
/// </para>
/// <list type="bullet">
/// <item><description><see cref="ColorPixels"/> 与 <see cref="DepthPixels"/> 返回的 <c>Span</c> 在单帧渲染期间必须保持稳定（同一块内存），否则光栅化器的行带并行会读到陈旧数据。</description></item>
/// <item><description>多个线程会并发写入互不重叠的行带，<b>无需</b>实现加锁；但若写目标自身有线程亲和性（如 GDI 句柄），应在实现里做必要隔离。</description></item>
/// <item><description><see cref="Clear"/> 与 <see cref="ClearDepth"/> 由渲染器在帧首调用，实现可自行决定是否在内部并行化。</description></item>
/// </list>
/// </remarks>
public interface IRenderTarget
{
    /// <summary>宽度（像素）。</summary>
    int Width { get; }

    /// <summary>高度（像素）。</summary>
    int Height { get; }

    /// <summary>宽高比。</summary>
    float AspectRatio { get; }

    /// <summary>颜色缓冲的原始数据，行主序，长度为 <see cref="Width"/> × <see cref="Height"/>。</summary>
    Span<RgbColor> ColorPixels { get; }

    /// <summary>深度缓冲的原始数据，长度与颜色缓冲一致，存储 NDC 深度（-1 近 / +1 远）。</summary>
    Span<float> DepthPixels { get; }

    /// <summary>用指定颜色清屏，并把深度缓冲复位到远平面。</summary>
    /// <param name="color">背景色。</param>
    void Clear(RgbColor color);

    /// <summary>只复位深度缓冲。</summary>
    /// <param name="depth">复位到的深度值，默认为远平面 +1。</param>
    void ClearDepth(float depth = 1.0f);

    /// <summary>只清空颜色缓冲。</summary>
    void ClearColor(RgbColor color);
}
