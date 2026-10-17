using System.Numerics;
using Marigold.Geometry;
using Marigold.Mathematics;

namespace Marigold.SceneGraph;

/// <summary>
/// 场景图节点：具有父子层级、局部变换与可见性的通用容器。
/// </summary>
/// <remarks>
/// <para>
/// 层级化的意义在 渲染管线中体现得非常直接：
/// 一扇门被拆成“门框 + 左右门板”多个子节点，只要让它们挂在同一个房间节点下，
/// 搬运整个房间时就只需要改父节点的位置，
/// 而门板自身的开合动画仍写在局部坐标里，两者互不干扰。
/// </para>
/// <para>
/// <b>世界矩阵缓存</b>：子节点的世界矩阵 = 父世界矩阵 × 本节点局部矩阵。
/// 该值在 <see cref="UpdateWorldTransforms"/> 中自顶向下一次性刷新，
/// 渲染前调用一次即可，避免在每个三角形里重复连乘矩阵链。
/// </para>
/// </remarks>
public class SceneNode
{
    private readonly List<SceneNode> _children = [];
    private bool _worldMatrixDirty = true;

    /// <summary>构造节点。</summary>
    /// <param name="name">节点名称。</param>
    public SceneNode(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        Transform = Transform3D.Identity;
        WorldMatrix = Matrix4x4.Identity;
    }

    /// <summary>节点名称。</summary>
    public string Name { get; set; }

    /// <summary>父节点，根节点为 <c>null</c>。</summary>
    public SceneNode? Parent { get; private set; }

    /// <summary>子节点列表（只读视图）。</summary>
    public IReadOnlyList<SceneNode> Children => _children;

    /// <summary>局部变换。修改后会自动把本节点与所有子节点的世界矩阵标记为脏。</summary>
    public Transform3D Transform
    {
        get => _transform;
        set
        {
            _transform = value;
            MarkWorldMatrixDirty();
        }
    }

    private Transform3D _transform;

    /// <summary>由 <see cref="UpdateWorldTransforms"/> 维护的世界矩阵缓存。</summary>
    public Matrix4x4 WorldMatrix { get; private set; }

    /// <summary>是否可见。不可见节点及其子树会被渲染与剔除逻辑整体跳过。</summary>
    public bool Visible { get; set; } = true;

    /// <summary>世界矩阵是否与局部变换不同步。</summary>
    public bool IsWorldMatrixDirty => _worldMatrixDirty;

    /// <summary>本节点自身的局部包围盒；由派生类（如 <see cref="MeshInstance"/>）重写。</summary>
    /// <remarks>返回值为局部空间，默认是位于原点的零体积盒。</remarks>
    public virtual BoundingBox LocalBounds => new(Vector3.Zero, Vector3.Zero);

    /// <summary>添加一个子节点。</summary>
    /// <exception cref="InvalidOperationException">该节点已有父节点。</exception>
    public SceneNode Add(SceneNode child)
    {
        ArgumentNullException.ThrowIfNull(child);
        if (child == this)
        {
            throw new InvalidOperationException("节点不能把自己添加为自己的子节点。");
        }

        if (child.Parent is not null)
        {
            throw new InvalidOperationException(
                $"节点 '{child.Name}' 已经挂在 '{child.Parent.Name}' 下，请先移除。");
        }

        // 防止把祖先节点挂到自己的后代，形成环
        for (SceneNode? ancestor = Parent; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (ReferenceEquals(ancestor, child))
            {
                throw new InvalidOperationException("该操作会形成循环的场景图。");
            }
        }

        child.Parent = this;
        _children.Add(child);
        child.MarkWorldMatrixDirty();
        return child;
    }

    /// <summary>批量添加子节点。</summary>
    public SceneNode AddRange(IEnumerable<SceneNode> children)
    {
        ArgumentNullException.ThrowIfNull(children);
        foreach (SceneNode child in children)
        {
            Add(child);
        }

        return this;
    }

    /// <summary>移除一个子节点。</summary>
    public bool Remove(SceneNode child)
    {
        ArgumentNullException.ThrowIfNull(child);
        if (_children.Remove(child))
        {
            child.Parent = null;
            return true;
        }

        return false;
    }

    /// <summary>把本节点及全部后代的世界矩阵标记为需要重算。</summary>
    public void MarkWorldMatrixDirty()
    {
        _worldMatrixDirty = true;
        foreach (SceneNode child in _children)
        {
            child.MarkWorldMatrixDirty();
        }
    }

    /// <summary>
    /// 自顶向下刷新整棵子树的世界矩阵。
    /// </summary>
    /// <param name="parentWorld">父节点的世界矩阵，根节点传 <see cref="Matrix4x4.Identity"/>。</param>
    /// <param name="parentVisible">父节点是否可见。</param>
    public void UpdateWorldTransforms(Matrix4x4 parentWorld, bool parentVisible)
    {
        WorldMatrix = Transform.ToMatrix() * parentWorld;
        _worldMatrixDirty = false;

        bool visible = parentVisible && Visible;
        foreach (SceneNode child in _children)
        {
            child.UpdateWorldTransforms(WorldMatrix, visible);
        }
    }

    /// <summary>按深度优先顺序遍历本节点及全部后代（含自身）。</summary>
    public IEnumerable<SceneNode> Traverse()
    {
        yield return this;
        foreach (SceneNode child in _children)
        {
            foreach (SceneNode node in child.Traverse())
            {
                yield return node;
            }
        }
    }

    /// <summary>统计本节点子树的节点数量。</summary>
    public int CountNodes()
    {
        int count = 1;
        foreach (SceneNode child in _children)
        {
            count += child.CountNodes();
        }

        return count;
    }

    /// <summary>把整棵子树的世界空间包围盒取出来（含不可见节点，纯几何查询）。</summary>
    public BoundingBox GetWorldBounds(bool includeChildren = true)
    {
        BoundingBox bounds = TransformWorldBounds(LocalBounds);
        if (!includeChildren)
        {
            return bounds;
        }

        foreach (SceneNode child in _children)
        {
            bounds = bounds.Union(child.GetWorldBounds(true));
        }

        return bounds;
    }

    /// <summary>把局部包围盒变换到世界空间。</summary>
    protected BoundingBox TransformWorldBounds(BoundingBox localBounds) => localBounds.Transform(WorldMatrix);

    /// <inheritdoc />
    public override string ToString() => $"{GetType().Name}('{Name}')";
}
