using System.Numerics;

namespace Marigold.Mathematics;

/// <summary>
/// 三维空间的平移 / 旋转 / 缩放组合，是场景图中每个节点的基础变换。
/// </summary>
/// <remarks>
/// <para>
/// 旋转用四元数存储，避免欧拉角万向节问题；构造时提供欧拉角入口，
/// 采用 <b>YXZ</b> 旋转顺序。
/// </para>
/// <para>
/// 矩阵约定：本引擎统一使用 <b>行向量</b>约定（与 <see cref="System.Numerics.Matrix4x4"/> 一致），
/// 即 <c>world = local * scale * rotation * translation</c>，
/// 变换一个点时写作 <see cref="Vector3.Transform(Vector3, Matrix4x4)"/>。
/// </para>
/// </remarks>
public struct Transform3D : IEquatable<Transform3D>
{
    /// <summary>世界坐标位置。</summary>
    public Vector3 Position;

    /// <summary>旋转四元数。</summary>
    public Quaternion Rotation;

    /// <summary>逐轴缩放。</summary>
    public Vector3 Scale;

    /// <summary>单位变换。</summary>
    public static readonly Transform3D Identity = new(Vector3.Zero, Quaternion.Identity, Vector3.One);

    /// <summary>构造一个变换。</summary>
    public Transform3D(Vector3 position, Quaternion rotation, Vector3 scale)
    {
        Position = position;
        Rotation = rotation;
        Scale = scale;
    }

    /// <summary>构造一个只含平移的变换。</summary>
    public static Transform3D FromPosition(Vector3 position) => new(position, Quaternion.Identity, Vector3.One);

    /// <summary>构造一个只含平移与缩放的变换。</summary>
    public static Transform3D FromPositionScale(Vector3 position, Vector3 scale) => new(position, Quaternion.Identity, scale);

    /// <summary>
    /// 以欧拉角（角度制，YXZ 顺序）构造变换。
    /// </summary>
    /// <param name="position">位置。</param>
    /// <param name="pitchDegrees">绕 X 轴旋转（抬头 / 低头）。</param>
    /// <param name="yawDegrees">绕 Y 轴旋转（左右转头）。</param>
    /// <param name="rollDegrees">绕 Z 轴旋转（侧倾）。</param>
    /// <param name="scale">缩放，省略则为 1。</param>
    public static Transform3D FromEulerDegrees(Vector3 position, float pitchDegrees, float yawDegrees, float rollDegrees, Vector3? scale = null)
    {
        Vector3 euler = new(pitchDegrees * MathUtil.DegToRad, yawDegrees * MathUtil.DegToRad, rollDegrees * MathUtil.DegToRad);
        return new Transform3D(position, Quaternion.CreateFromYawPitchRoll(euler.Y, euler.X, euler.Z), scale ?? Vector3.One);
    }

    /// <summary>以欧拉角（弧度制，YXZ 顺序）构造变换。</summary>
    public static Transform3D FromEulerRadians(Vector3 position, float pitch, float yaw, float roll, Vector3? scale = null)
        => new(position, Quaternion.CreateFromYawPitchRoll(yaw, pitch, roll), scale ?? Vector3.One);

    /// <summary>把本变换展开为 4x4 矩阵（行向量约定）。</summary>
    public readonly Matrix4x4 ToMatrix() =>
        Matrix4x4.CreateScale(Scale) *
        Matrix4x4.CreateFromQuaternion(Rotation) *
        Matrix4x4.CreateTranslation(Position);

    /// <summary>本变换的逆变换（假设缩放非零）。</summary>
    public readonly Transform3D Inverse()
    {
        Quaternion invRotation = Quaternion.Conjugate(Rotation);
        Vector3 invScale = new(
            MathF.Abs(Scale.X) < MathUtil.Epsilon ? 0.0f : 1.0f / Scale.X,
            MathF.Abs(Scale.Y) < MathUtil.Epsilon ? 0.0f : 1.0f / Scale.Y,
            MathF.Abs(Scale.Z) < MathUtil.Epsilon ? 0.0f : 1.0f / Scale.Z);

        // 先反向缩放，再反向旋转，最后平移回原点
        Vector3 scaled = new(Position.X * invScale.X, Position.Y * invScale.Y, Position.Z * invScale.Z);
        Vector3 invPosition = Vector3.Transform(-scaled, Matrix4x4.CreateFromQuaternion(invRotation));
        return new Transform3D(invPosition, invRotation, invScale);
    }

    /// <summary>本变换的正向方向（局部 -Z 轴）。</summary>
    public readonly Vector3 Forward => Vector3.Transform(-Vector3.UnitZ, Matrix4x4.CreateFromQuaternion(Rotation));

    /// <summary>本变换的右向方向（局部 +X 轴）。</summary>
    public readonly Vector3 Right => Vector3.Transform(Vector3.UnitX, Matrix4x4.CreateFromQuaternion(Rotation));

    /// <summary>本变换的上向方向（局部 +Y 轴）。</summary>
    public readonly Vector3 Up => Vector3.Transform(Vector3.UnitY, Matrix4x4.CreateFromQuaternion(Rotation));

    /// <inheritdoc />
    public readonly bool Equals(Transform3D other) =>
        Position.Equals(other.Position) && Rotation.Equals(other.Rotation) && Scale.Equals(other.Scale);

    /// <inheritdoc />
    public override readonly bool Equals(object? obj) => obj is Transform3D other && Equals(other);

    /// <inheritdoc />
    public override readonly int GetHashCode() => HashCode.Combine(Position, Rotation, Scale);

    /// <summary>相等运算符。</summary>
    public static bool operator ==(Transform3D left, Transform3D right) => left.Equals(right);

    /// <summary>不等运算符。</summary>
    public static bool operator !=(Transform3D left, Transform3D right) => !left.Equals(right);

    /// <inheritdoc />
    public override readonly string ToString() =>
        $"Transform3D(pos={Position}, rot={Rotation}, scale={Scale})";
}
