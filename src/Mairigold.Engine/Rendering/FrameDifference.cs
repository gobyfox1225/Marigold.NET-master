namespace Marigold.Rendering;

/// <summary>
/// 两张帧缓冲之间的差异统计。
/// </summary>
/// <param name="MeanAbsoluteError">所有像素、所有通道的平均绝对误差（0 表示完全相同）。</param>
/// <param name="MaxAbsoluteError">单通道最大绝对误差。</param>
/// <param name="DifferingPixelCount">超过容差的像素数量。</param>
/// <param name="TotalPixelCount">参与比较的像素总数。</param>
public readonly record struct FrameDifference(
    float MeanAbsoluteError,
    float MaxAbsoluteError,
    int DifferingPixelCount,
    int TotalPixelCount)
{
    /// <summary>不同像素的占比。</summary>
    public float DifferingRatio =>
        TotalPixelCount == 0 ? 0.0f : DifferingPixelCount / (float)TotalPixelCount;

    /// <summary>在给定容差内是否可视为同一张图。</summary>
    public bool IsEffectivelyIdentical(float meanTolerance = 1.0f / 255.0f) =>
        MeanAbsoluteError <= meanTolerance;

    /// <inheritdoc />
    public override string ToString() =>
        $"平均误差 {MeanAbsoluteError * 255.0f:0.000}/255, 最大误差 {MaxAbsoluteError * 255.0f:0.000}/255, " +
        $"不同像素 {DifferingPixelCount}/{TotalPixelCount} ({DifferingRatio * 100.0f:0.000}%)";
}
