using System.Text;
using Marigold.Mathematics;
using Marigold.Rendering;

namespace Marigold.Imaging;

/// <summary>
/// 用字符画预览渲染结果。
/// </summary>
/// <remarks>
/// <para>
/// 对软件渲染器来说，这个看似玩具的功能其实很实用：无需打开图像文件、
/// 无需图形界面，就能在纯终端环境（CI 日志、SSH 会话）里确认
/// “相机朝向对不对、几何有没有渲染出来、画面是不是全黑或全白”。
/// </para>
/// <para>
/// 实现上先把帧缓冲按网格降采样求平均亮度，再映射到灰度字符阶梯。
/// 由于 渲染器的画面几乎是纯白一片，字符画的对比度会很低——
/// 这本身就是对渲染结果的一种有效判断：如果出现大面积空白或乱码，
/// 说明渲染设置有误（例如环境光过暗、雾浓度过高、相机朝向相反）。
/// </para>
/// </remarks>
public static class AsciiPreview
{
    /// <summary>从暗到亮的灰度字符阶梯。</summary>
    public const string DefaultRamp = " .:-=+*#%@";

    /// <summary>生成字符画文本。</summary>
    /// <param name="buffer">源帧缓冲。</param>
    /// <param name="columns">输出列数。</param>
    /// <param name="rows">输出行数。</param>
    /// <param name="ramp">灰度阶梯，从最暗到最亮。</param>
    /// <param name="gamma">亮度伽马，大于 1 会压暗暗部，从而提升画面反差。</param>
    /// <param name="minLuminance">
    /// 亮度窗口下界。低于该值的像素一律视为最暗。
    /// </param>
    /// <param name="maxLuminance">亮度窗口上界。高于该值的像素一律视为最亮。</param>
    /// <remarks>
    /// <para>
    /// <paramref name="minLuminance"/> 与 <paramref name="maxLuminance"/> 是为
    /// 低反差画面这类<b>低反差画面</b>准备的：整张图的亮度都落在 0.78 到 1.0 之间，
    /// 若按默认的 [0, 1] 区间映射，所有字符会挤在阶梯最亮的两三格里，看不出结构。
    /// 把窗口收窄到 [0.75, 1.0]，门框、门板与墙面的明度差就能在字符画里清楚分开。
    /// </para>
    /// </remarks>
    public static string Render(
        FrameBuffer buffer,
        int columns = 100,
        int rows = 32,
        string ramp = DefaultRamp,
        float gamma = 1.0f,
        float minLuminance = 0.0f,
        float maxLuminance = 1.0f)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentException.ThrowIfNullOrEmpty(ramp);

        columns = Math.Max(1, columns);
        rows = Math.Max(1, rows);

        StringBuilder builder = new((columns + 1) * rows);

        for (int row = 0; row < rows; row++)
        {
            // 终端字符通常是高的，宽高比约 1:2，因此纵向采样步长按两倍计算
            int y0 = row * buffer.Height / rows;
            int y1 = Math.Max(y0 + 1, (row + 1) * buffer.Height / rows);

            for (int column = 0; column < columns; column++)
            {
                int x0 = column * buffer.Width / columns;
                int x1 = Math.Max(x0 + 1, (column + 1) * buffer.Width / columns);

                builder.Append(ToCharacter(buffer, x0, y0, x1, y1, ramp, gamma, minLuminance, maxLuminance));
            }

            builder.Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>把一个矩形区域的平均亮度映射为字符。</summary>
    private static char ToCharacter(
        FrameBuffer buffer,
        int x0,
        int y0,
        int x1,
        int y1,
        string ramp,
        float gamma,
        float minLuminance,
        float maxLuminance)
    {
        float sum = 0.0f;
        int samples = 0;

        for (int y = y0; y < y1 && y < buffer.Height; y++)
        {
            for (int x = x0; x < x1 && x < buffer.Width; x++)
            {
                sum += buffer.GetPixel(x, y).Luminance;
                samples++;
            }
        }

        if (samples == 0)
        {
            return ramp[0];
        }

        float luminance = sum / samples;

        // 先把亮度重映射到窗口区间，再套用伽马
        float span = maxLuminance - minLuminance;
        luminance = span > MathUtil.Epsilon
            ? MathUtil.Clamp01((luminance - minLuminance) / span)
            : MathUtil.Clamp01(luminance);

        if (gamma > 0.0f && MathF.Abs(gamma - 1.0f) > MathUtil.Epsilon)
        {
            luminance = MathF.Pow(luminance, gamma);
        }

        int index = MathUtil.Clamp((int)(luminance * (ramp.Length - 1) + 0.5f), 0, ramp.Length - 1);
        return ramp[index];
    }
}
