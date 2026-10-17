namespace Marigold.Mathematics;

/// <summary>
/// 通用数学工具。集中放置角度换算、插值与缓动函数，
/// 避免在渲染与游戏逻辑里散落魔法数字。
/// </summary>
public static class MathUtil
{
    /// <summary>圆周率（单精度）。</summary>
    public const float Pi = MathF.PI;

    /// <summary>圆周率的两倍。</summary>
    public const float TwoPi = MathF.PI * 2.0f;

    /// <summary>圆周率的一半（90°）。</summary>
    public const float HalfPi = MathF.PI * 0.5f;

    /// <summary>圆周率的四分之一（45°）。</summary>
    public const float QuarterPi = MathF.PI * 0.25f;

    /// <summary>角度制转弧度制。</summary>
    public const float DegToRad = MathF.PI / 180.0f;

    /// <summary>弧度制转角度制。</summary>
    public const float RadToDeg = 180.0f / MathF.PI;

    /// <summary>浮点比较默认容差。</summary>
    public const float Epsilon = 1e-6f;

    /// <summary>把角度换算为弧度。</summary>
    public static float ToRadians(float degrees) => degrees * DegToRad;

    /// <summary>把弧度换算为角度。</summary>
    public static float ToDegrees(float radians) => radians * RadToDeg;

    /// <summary>把值限制在 [min, max] 区间内。</summary>
    public static float Clamp(float value, float min, float max) => value < min ? min : value > max ? max : value;

    /// <summary>把值限制在 [0, 1] 区间内。</summary>
    public static float Clamp01(float value) => Clamp(value, 0.0f, 1.0f);

    /// <summary>把整数限制在 [min, max] 区间内。</summary>
    public static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;

    /// <summary>线性插值。</summary>
    public static float Lerp(float a, float b, float t) => a + ((b - a) * t);

    /// <summary>把值从 [inMin, inMax] 线性映射到 [outMin, outMax]，并把结果限制在输出区间内。</summary>
    public static float Remap(float value, float inMin, float inMax, float outMin, float outMax)
    {
        if (MathF.Abs(inMax - inMin) < Epsilon)
        {
            return outMin;
        }

        float t = Clamp01((value - inMin) / (inMax - inMin));
        return Lerp(outMin, outMax, t);
    }

    /// <summary>
    /// 二次缓入缓出。
    /// </summary>
    public static float EaseInOutQuad(float t)
    {
        t = Clamp01(t);
        return t < 0.5f
            ? 2.0f * t * t
            : 1.0f - (2.0f * (1.0f - t) * (1.0f - t));
    }

    /// <summary>判断两个浮点数在指定容差内是否相等。</summary>
    public static bool NearlyEqual(float a, float b, float tolerance = Epsilon) => MathF.Abs(a - b) <= tolerance;

    /// <summary>判断一个浮点数是否为可用数值（非 NaN、非无穷）。</summary>
    public static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

    /// <summary>判断一个向量是否为可用数值。</summary>
    public static bool IsFinite(System.Numerics.Vector3 value) =>
        IsFinite(value.X) && IsFinite(value.Y) && IsFinite(value.Z);
}
