using System.Numerics;
using Marigold.Geometry;

namespace Marigold.SceneGraph;

/// <summary>场景的几何统计信息，便于在控制台或 HUD 中显示。</summary>
/// <param name="NodeCount">节点总数。</param>
/// <param name="MeshInstanceCount">可渲染实例总数。</param>
/// <param name="VisibleMeshInstanceCount">可见的可渲染实例数。</param>
/// <param name="TriangleCount">三角形总数。</param>
/// <param name="VertexCount">顶点总数（含共享顶点，故按实例累加）。</param>
/// <param name="DistinctMeshCount">按引用去重后的不同网格数量。</param>
public readonly record struct SceneStatistics(
    int NodeCount,
    int MeshInstanceCount,
    int VisibleMeshInstanceCount,
    int TriangleCount,
    int VertexCount,
    int DistinctMeshCount)
{
    /// <inheritdoc />
    public override string ToString() =>
        $"节点 {NodeCount} / 可渲染实例 {MeshInstanceCount}（可见 {VisibleMeshInstanceCount}）" +
        $" / 三角形 {TriangleCount} / 顶点 {VertexCount} / 独立网格 {DistinctMeshCount}";
}

/// <summary>
/// 场景：由一棵 <see cref="SceneNode"/> 树构成的待渲染对象集合。
/// </summary>
/// <remarks>
/// 本引擎只关心渲染所需的视觉场景，
/// 游戏逻辑（控制器、碰撞、门动画）由游戏层持有并驱动。
/// </remarks>
public sealed class Scene
{
    /// <summary>构造场景。</summary>
    /// <param name="name">场景名称。</param>
    public Scene(string name = "Scene")
    {
        Name = name;
        Root = new SceneNode(name);
    }

    /// <summary>场景名称。</summary>
    public string Name { get; set; }

    /// <summary>场景根节点。</summary>
    public SceneNode Root { get; }

    /// <summary>环境背景色（清屏色与雾色通常取同一个值，以营造“无边界”的观感）。</summary>
    public Imaging.RgbColor BackgroundColor { get; set; } = Imaging.RgbColor.White;

    /// <summary>
    /// 刷新整棵场景树的世界矩阵。
    /// </summary>
    /// <remarks>每帧渲染前调用一次。若没有任何节点的局部变换发生改变，则该调用是廉价的。</remarks>
    public void UpdateWorldTransforms() => Root.UpdateWorldTransforms(Matrix4x4.Identity, true);

    /// <summary>枚举所有参与渲染的实例，按深度优先顺序。</summary>
    /// <param name="requireVisible">是否只返回可见链路上的实例。</param>
    public IEnumerable<MeshInstance> CollectMeshInstances(bool requireVisible = true)
    {
        foreach (SceneNode node in Root.Traverse())
        {
            if (node is not MeshInstance instance)
            {
                continue;
            }

            if (!instance.Renderable)
            {
                continue;
            }

            if (requireVisible && !IsEffectivelyVisible(instance))
            {
                continue;
            }

            yield return instance;
        }
    }

    /// <summary>
    /// 把可见的网格实例收集进调用方提供的列表，避免每帧分配新的迭代器与列表。
    /// </summary>
    /// <param name="destination">接收实例的列表，调用前无需清空，本方法会先清空。</param>
    /// <param name="requireVisible">是否只返回可见链路上的实例。</param>
    public void CollectMeshInstances(List<MeshInstance> destination, bool requireVisible = true)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.Clear();
        CollectInto(Root, destination, requireVisible);
    }

    private static void CollectInto(SceneNode node, List<MeshInstance> destination, bool requireVisible)
    {
        if (node is MeshInstance instance && instance.Renderable &&
            (!requireVisible || IsEffectivelyVisible(instance)))
        {
            destination.Add(instance);
        }

        foreach (SceneNode child in node.Children)
        {
            CollectInto(child, destination, requireVisible);
        }
    }

    /// <summary>判断从根到该节点的整条链上是否都可见。</summary>
    public static bool IsEffectivelyVisible(SceneNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        for (SceneNode? current = node; current is not null; current = current.Parent)
        {
            if (!current.Visible)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>收集场景统计信息。</summary>
    public SceneStatistics GetStatistics()
    {
        int nodeCount = 0;
        int instanceCount = 0;
        int visibleCount = 0;
        int triangles = 0;
        int vertices = 0;
        HashSet<Mesh> distinct = [];

        foreach (SceneNode node in Root.Traverse())
        {
            nodeCount++;
            if (node is not MeshInstance instance || !instance.Renderable)
            {
                continue;
            }

            instanceCount++;
            triangles += instance.Mesh.TriangleCount;
            vertices += instance.Mesh.VertexCount;
            distinct.Add(instance.Mesh);

            if (IsEffectivelyVisible(instance))
            {
                visibleCount++;
            }
        }

        return new SceneStatistics(nodeCount, instanceCount, visibleCount, triangles, vertices, distinct.Count);
    }

    /// <summary>整棵场景树的世界空间包围盒。</summary>
    public BoundingBox GetWorldBounds() => Root.GetWorldBounds();

    /// <inheritdoc />
    public override string ToString() => $"Scene('{Name}') {{ {GetStatistics()} }}";
}
