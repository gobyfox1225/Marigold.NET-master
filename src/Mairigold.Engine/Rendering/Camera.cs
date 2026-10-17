using System.Numerics;
using Marigold.Mathematics;

namespace Marigold.Rendering;

/// <summary>相机投影模式。</summary>
public enum ProjectionMode
{
    /// <summary>透视投影：近大远小，适合 3D 场景观察。</summary>
    Perspective = 0,

    /// <summary>正交投影：无透视收缩，适合 UI、2D、等距与工程视图。</summary>
    Orthographic = 1,
}

/// <summary>
/// 相机：把世界空间变换到裁剪空间，支持透视与正交两种投影。
/// </summary>
/// <remarks>
/// <para>
/// <b>观察变换</b>：本类采用标准的视图矩阵与投影矩阵：
/// </para>
/// <list type="bullet">
/// <item><description>
/// 视图矩阵用观察方向的正交基构造，相机可位于任意位置、朝向任意方向，
/// 不再要求旋转轴必须经过世界原点。
/// </description></item>
/// <item><description>
/// 投影矩阵用垂直视场角（FOV）描述透视强度。
/// “焦距”若以像素单位表示，换算关系为
/// <c>焦距(像素) = 0.5 × 视口高度 / tan(FOV / 2)</c>；
/// 默认的 350 像素焦距在 540 高的视口下约等于 <b>75.3°</b> 垂直视场角
/// （半角 37.6°）。
/// </description></item>
/// <item><description>
/// 深度被投影到 [-1, 1] 的 NDC 区间（OpenGL 约定），近平面为 -1、远平面为 +1，
/// 配合“更近者胜”的深度测试，才能正确实现遮挡。
/// </description></item>
/// </list>
/// <para>
/// <b>矩阵约定</b>：行向量。裁剪坐标写作 <c>world × 视图 × 投影</c>。
/// </para>
/// </remarks>
public sealed class Camera
{
    /// <summary>相机在世界空间中的位置（即眼睛位置）。</summary>
    public Vector3 Position { get; set; } = Vector3.Zero;

    /// <summary>水平朝向（偏航角），单位弧度。0 表示朝向 -Z。</summary>
    public float Yaw { get; set; }

    /// <summary>垂直朝向（俯仰角），单位弧度。正值表示抬头。</summary>
    public float Pitch { get; set; }

    /// <summary>
    /// 垂直视场角（角度制）。
    /// </summary>
    /// <remarks>
    /// 默认按垂直方向解释视场角，取 78°。
    /// </remarks>
    public float FieldOfViewDegrees { get; set; } = 78.0f;

    /// <summary>近平面距离，必须为正。</summary>
    public float NearPlane { get; set; } = 0.05f;

    /// <summary>远平面距离，默认 400。</summary>
    public float FarPlane { get; set; } = 400.0f;

    /// <summary>
    /// 投影模式。默认 <see cref="ProjectionMode.Perspective"/>；
    /// 设为 <see cref="ProjectionMode.Orthographic"/> 后使用 <see cref="OrthographicSize"/> 构造正交投影。
    /// </summary>
    public ProjectionMode Projection { get; set; } = ProjectionMode.Perspective;

    /// <summary>
    /// 正交投影的垂直半高（世界单位）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 正交投影没有视场角，改用「世界空间中可见区域的垂直半高」描述视野。
    /// 例如 <c>OrthographicSize = 10</c> 表示垂直方向可见 20 个世界单位（-10 到 +10），
    /// 水平方向按视口宽高比缩放。
    /// </para>
    /// <para>
    /// 仅在 <see cref="Projection"/> 为 <see cref="ProjectionMode.Orthographic"/> 时生效。
    /// </para>
    /// </remarks>
    public float OrthographicSize { get; set; } = 10.0f;

    /// <summary>相机的前方单位向量（局部 -Z 轴）。</summary>
    public Vector3 Forward
    {
        get
        {
            float cosPitch = MathF.Cos(Pitch);
            return new Vector3(
                -MathF.Sin(Yaw) * cosPitch,
                MathF.Sin(Pitch),
                -MathF.Cos(Yaw) * cosPitch);
        }
    }

    /// <summary>相机的右方单位向量（局部 +X 轴，始终水平）。</summary>
    public Vector3 Right => new(MathF.Cos(Yaw), 0.0f, -MathF.Sin(Yaw));

    /// <summary>相机的上方单位向量（局部 +Y 轴）。</summary>
    public Vector3 Up
    {
        get
        {
            float sinPitch = MathF.Sin(Pitch);
            return new Vector3(
                sinPitch * MathF.Sin(Yaw),
                MathF.Cos(Pitch),
                sinPitch * MathF.Cos(Yaw));
        }
    }

    /// <summary>视场角对应的像素焦距（以 <paramref name="viewportHeight"/> 为基准）。</summary>
    /// <remarks>
    /// 像素焦距的计算公式：
    /// <c>focal = 0.5 × height / tan(fov / 2)</c>。
    /// </remarks>
    public float GetFocalLengthInPixels(int viewportHeight)
    {
        float halfFov = MathUtil.ToRadians(FieldOfViewDegrees) * 0.5f;
        float tan = MathF.Tan(halfFov);
        return tan <= MathUtil.Epsilon ? 0.0f : viewportHeight * 0.5f / tan;
    }

    /// <summary>按像素焦距反推垂直视场角，是 <see cref="GetFocalLengthInPixels"/> 的逆运算。</summary>
    public static float FocalLengthToFieldOfView(float focalLengthInPixels, int viewportHeight)
    {
        if (focalLengthInPixels <= MathUtil.Epsilon || viewportHeight <= 0)
        {
            return 60.0f;
        }

        return MathUtil.ToDegrees(2.0f * MathF.Atan(viewportHeight * 0.5f / focalLengthInPixels));
    }

    /// <summary>构造视图矩阵（世界空间 → 视图空间）。</summary>
    public Matrix4x4 GetViewMatrix()
    {
        Vector3 forward = Forward;
        Vector3 right = Right;

        // 用叉积重算 up，而不是直接用 Pitch 三角式：
        // 这样无论如何修改俯仰逻辑，基向量都严格正交，视图矩阵不会悄悄退化成剪切矩阵
        Vector3 up = Vector3.Cross(right, forward);

        return new Matrix4x4(
            right.X, up.X, -forward.X, 0.0f,
            right.Y, up.Y, -forward.Y, 0.0f,
            right.Z, up.Z, -forward.Z, 0.0f,
            -Vector3.Dot(right, Position),
            -Vector3.Dot(up, Position),
            Vector3.Dot(forward, Position),
            1.0f);
    }

    /// <summary>构造投影矩阵（视图空间 → 裁剪空间），深度区间为 [-1, 1]。</summary>
    /// <param name="aspectRatio">视口宽高比。</param>
    /// <remarks>
    /// 按 <see cref="Projection"/> 选择透视或正交投影矩阵。
    /// </remarks>
    public Matrix4x4 GetProjectionMatrix(float aspectRatio)
    {
        if (aspectRatio <= MathUtil.Epsilon)
        {
            aspectRatio = 1.0f;
        }

        float near = MathF.Max(NearPlane, 1e-4f);
        float far = MathF.Max(FarPlane, near + 1e-3f);

        if (Projection == ProjectionMode.Orthographic)
        {
            return GetOrthographicMatrix(aspectRatio, near, far);
        }

        return GetPerspectiveMatrix(aspectRatio, near, far);
    }

    /// <summary>构造透视投影矩阵。</summary>
    private Matrix4x4 GetPerspectiveMatrix(float aspectRatio, float near, float far)
    {
        float f = 1.0f / MathF.Tan(MathUtil.ToRadians(FieldOfViewDegrees) * 0.5f);

        return new Matrix4x4(
            f / aspectRatio, 0.0f, 0.0f, 0.0f,
            0.0f, f, 0.0f, 0.0f,
            0.0f, 0.0f, (far + near) / (near - far), -1.0f,
            0.0f, 0.0f, 2.0f * far * near / (near - far), 0.0f);
    }

    /// <summary>
    /// 构造正交投影矩阵。
    /// </summary>
    /// <remarks>
    /// 深度区间仍映射到 [-1, 1]，与透视投影一致，因此深度测试逻辑无需区分模式。
    /// 垂直范围由 <see cref="OrthographicSize"/> 决定（半高），水平范围按宽高比缩放。
    /// </remarks>
    private Matrix4x4 GetOrthographicMatrix(float aspectRatio, float near, float far)
    {
        float size = MathF.Max(OrthographicSize, 1e-3f);
        float halfHeight = size;
        float halfWidth = halfHeight * aspectRatio;

        // 行向量约定的正交矩阵：无透视除法，W 分量恒为 1
        float invWidth = 1.0f / halfWidth;
        float invHeight = 1.0f / halfHeight;
        float invDepth = 2.0f / (near - far);

        return new Matrix4x4(
            invWidth, 0.0f, 0.0f, 0.0f,
            0.0f, invHeight, 0.0f, 0.0f,
            0.0f, 0.0f, invDepth, 0.0f,
            0.0f, 0.0f, (far + near) / (near - far), 1.0f);
    }

    /// <summary>构造视图 × 投影矩阵。</summary>
    public Matrix4x4 GetViewProjectionMatrix(float aspectRatio) =>
        GetViewMatrix() * GetProjectionMatrix(aspectRatio);

    /// <summary>
    /// 把一个世界坐标点投影为屏幕像素坐标（左上角为原点，Y 轴向下）。
    /// </summary>
    /// <returns>
    /// 屏幕坐标；若点位于相机<b>后方或正落在相机平面上</b>（齐次坐标 W ≤ 0），
    /// 返回 <see cref="float.NaN"/>。
    /// </returns>
    /// <remarks>
    /// 相机后方的点做透视除法会得到“镜像”后的合法数值，仅凭结果无法分辨它是否在前方。
    /// 因此这里主动返回非数值，把“该点不在可见半空间”这件事明确表达出来，
    /// 调用方（例如调试拾取）必须显式处理这种情况。
    /// 渲染管线不使用本方法做批量投影，而是先在齐次空间裁剪，因此不受影响。
    /// </remarks>
    public Vector2 WorldToScreen(Vector3 world, int viewportWidth, int viewportHeight)
    {
        float aspect = viewportHeight <= 0 ? 1.0f : viewportWidth / (float)viewportHeight;
        Vector4 clip = Vector4.Transform(new Vector4(world, 1.0f), GetViewProjectionMatrix(aspect));
        if (clip.W <= MathUtil.Epsilon)
        {
            return new Vector2(float.NaN, float.NaN);
        }

        float invW = 1.0f / clip.W;
        float ndcX = clip.X * invW;
        float ndcY = clip.Y * invW;

        return new Vector2(
            ((ndcX * 0.5f) + 0.5f) * viewportWidth,
            (0.5f - (ndcY * 0.5f)) * viewportHeight);
    }

    /// <summary>
    /// 从屏幕像素坐标反向构造一条世界空间射线，可用于拾取（点击选中物体）。
    /// </summary>
    public void ScreenPointToRay(Vector2 screenPoint, int viewportWidth, int viewportHeight, out Vector3 origin, out Vector3 direction)
    {
        float aspect = viewportHeight <= 0 ? 1.0f : viewportWidth / (float)viewportHeight;
        float f = 1.0f / MathF.Tan(MathUtil.ToRadians(FieldOfViewDegrees) * 0.5f);

        float ndcX = ((screenPoint.X / viewportWidth) * 2.0f) - 1.0f;
        float ndcY = 1.0f - ((screenPoint.Y / viewportHeight) * 2.0f);

        Vector3 viewDirection = Vector3.Normalize(new Vector3(ndcX * aspect / f, ndcY / f, -1.0f));
        Vector3 right = Right;
        Vector3 up = Vector3.Cross(Forward, right);

        origin = Position;
        direction = Vector3.Normalize(
            (right * viewDirection.X) + (up * viewDirection.Y) + (Forward * -viewDirection.Z));
    }

    /// <summary>让相机朝向目标点（保持位置不变，不会引入侧倾）。</summary>
    public void LookAt(Vector3 target)
    {
        Vector3 delta = target - Position;
        float horizontalLength = MathF.Sqrt((delta.X * delta.X) + (delta.Z * delta.Z));
        if (horizontalLength <= MathUtil.Epsilon && MathF.Abs(delta.Y) <= MathUtil.Epsilon)
        {
            return;
        }

        Yaw = MathF.Atan2(-delta.X, -delta.Z);
        Pitch = MathF.Atan2(delta.Y, horizontalLength);
    }

    /// <summary>用“视点 + 目标点”构造一个相机。</summary>
    public static Camera FromLookAt(Vector3 position, Vector3 target, float fieldOfViewDegrees = 78.0f)
    {
        Camera camera = new() { Position = position, FieldOfViewDegrees = fieldOfViewDegrees };
        camera.LookAt(target);
        return camera;
    }

    /// <summary>
    /// 构造一个用于回归对照的相机。
    /// </summary>
    /// <remarks>
    /// 该相机位于原点沿 +Z 后退 <paramref name="depthOffset"/> 处、朝向 +Z。
    /// 主要用于让单元测试做数值比对，不建议用于正式渲染。
    /// </remarks>
    public static Camera CreateLegacyEquivalent(
        float rotationXDegrees,
        float rotationYDegrees,
        int viewportHeight,
        float focalDistance = LegacyProjection.DefaultFocalDistance,
        float depthOffset = LegacyProjection.DefaultDepthOffset)
    {
        Camera camera = new()
        {
            // 旧式投影: screen = center + p_rot * focal / (focal + z_rot + offset)
            // 等价于相机位于 -Z 方向 offset + focal 处、焦距为 focal 的透视投影
            Position = new Vector3(0.0f, 0.0f, -(depthOffset + focalDistance)),
            FieldOfViewDegrees = FocalLengthToFieldOfView(focalDistance, viewportHeight),
            NearPlane = 0.05f,
            FarPlane = 10000.0f,
        };

        // 旧式投影先绕 Y 再绕 X 旋转“世界”，等价于相机反向旋转
        camera.Yaw = MathUtil.ToRadians(rotationYDegrees);
        camera.Pitch = MathUtil.ToRadians(rotationXDegrees);
        return camera;
    }

    /// <inheritdoc />
    public override string ToString() =>
        $"Camera(pos={Position}, yaw={MathUtil.ToDegrees(Yaw):0.##}°, pitch={MathUtil.ToDegrees(Pitch):0.##}°, fov={FieldOfViewDegrees:0.##}°)";
}
