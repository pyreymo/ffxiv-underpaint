# Retained high-level primitive API 设计

状态：正式设计，尚未实施。

本文定义 Underpaint 下一阶段对 Event Horizon 暴露的 primitive API 边界。运行时证据和当前实现状态仍分别记录在
`RESEARCH.md` 与 `STATUS.md`；本文只描述目标接口、所有权和可验收的迁移步骤。

## 已决定的边界

Event Horizon 面对的是语义 drawable，而不是渲染资源：

- regular polygon 由边数定义；
- polyhedron 由 Underpaint 支持的形状类型定义；
- drawable 对象本身表达跨帧逻辑身份；
- 每帧只提交 current transform、color 和 alpha。

Event Horizon 不会：

- 注册 mesh；
- 持有 mesh handle、VB、IB 或 GPU resource ID；
- 提交 vertices 或 indices；
- 选择 shader、texture 或 material；
- 计算或提交 previous transform。

Underpaint 私有承担：

- 统一 mesh representation 下的 topology 构造与共享缓存；
- native VB、IB、vertex stream、constant 和 delayed release 的完整生命周期；
- 无纹理 primitive 固定使用 `TexCoord0 = (0.5, 0.5)` 的 UV 策略；
- native pass submission；
- drawable 的 current/previous transform 和 pending frame temporal history。

首个实现范围只迁移现有 triangle、quad 和 icosahedron。bar/strip 的几何语义尚未决定，不进入第一阶段接口。

## 建议的公开 API

以下代码是接口草图，不要求文档阶段立即确定所有命名细节：

```csharp
public enum PolyhedronKind
{
    Icosahedron,
}

public sealed class Renderer : IDisposable
{
    public RegularPolygonDrawable CreateRegularPolygon(int sideCount);

    public PolyhedronDrawable CreatePolyhedron(PolyhedronKind kind);

    public PrimitiveFrame BeginFrame();
}

public sealed class RegularPolygonDrawable : IDisposable
{
    public int SideCount { get; }
}

public sealed class PolyhedronDrawable : IDisposable
{
    public PolyhedronKind Kind { get; }
}

public sealed class PrimitiveFrame : IDisposable
{
    public void DrawPolygon(
        RegularPolygonDrawable drawable,
        Matrix4x4 transform,
        Vector3 color,
        float alpha = 1f
    );

    public void DrawPolyhedron(
        PolyhedronDrawable drawable,
        Matrix4x4 transform,
        Vector3 color,
        float alpha = 1f
    );

    public void Publish();
}
```

典型调用：

```csharp
private readonly RegularPolygonDrawable triangle = renderer.CreateRegularPolygon(3);
private readonly RegularPolygonDrawable quad = renderer.CreateRegularPolygon(4);
private readonly PolyhedronDrawable icosahedron =
    renderer.CreatePolyhedron(PolyhedronKind.Icosahedron);

public void SubmitScene()
{
    using var frame = renderer.BeginFrame();
    frame.DrawPolygon(triangle, triangleWorld, triangleColor, triangleAlpha);
    frame.DrawPolygon(quad, quadWorld, quadColor, quadAlpha);
    frame.DrawPolyhedron(icosahedron, icosahedronWorld, icosahedronColor, 0.75f);
    frame.Publish();
}
```

`RegularPolygonDrawable` 和 `PolyhedronDrawable` 是高层逻辑对象。它们不是 mesh handle，也不能用于查询、导出或替换
任何 GPU 资源。多个 drawable 可以共享同一份私有 topology cache，但仍各自拥有独立的 temporal identity。

第一阶段 `CreateRegularPolygon` 只接受 `sideCount` 3 和 4。接口保留 regular polygon 的语义，后续只有在 topology
生成、边数上限和实机行为通过单独验收后才扩大边数范围。`CreatePolyhedron` 第一阶段只支持 `Icosahedron`。

## Drawable 生命周期

drawable 是 retained 对象，不应每帧创建：

1. caller 通过所属 `Renderer` 创建 drawable；
2. caller 在多个已发布帧中重复提交同一个对象；
3. drawable 的形状描述在其生命周期内不可变；
4. caller 不再需要该逻辑图元时 dispose drawable；
5. dispose 后的 drawable 不能再加入 frame；
6. dispose `Renderer` 会使其创建的全部 drawable 和未发布 frame 失效，并释放其私有 native 状态。

公开对象身份只在创建它的 `Renderer` 内有效。frame 必须拒绝其他 renderer 创建的 drawable，也必须拒绝同一个
drawable 在一个 frame 中被 Draw 两次。这样 identity 不需要公开整数 ID，也不会与 mesh cache key 混为一谈。

dispose drawable 表示逻辑图元生命周期结束。Underpaint 随后按安全的 delayed-release 规则回收该 drawable 的
私有 native 资源；共享 topology 只在没有其他使用者且缓存策略允许时回收。

## Frame publish 规则

`PrimitiveFrame` 收集一份完整的 scene snapshot：

- `BeginFrame` 创建尚未发布的可变 builder；
- `DrawPolygon` 和 `DrawPolyhedron` 只记录语义 drawable、current transform 和外观；
- `Publish` 冻结 snapshot，并原子替换尚未被 native renderer 消费的 pending frame；
- 一个 frame 只能成功发布一次；
- dispose 未发布的 frame 只丢弃它，不改变当前 pending 或 rendering frame；
- 发布空 frame 表示清空所有 drawable；
- 已发布 frame 不再借用 caller 持有的可变数据。

显式 `Publish` 比“dispose 即提交”更容易区分成功提交与异常路径中的半成品。Underpaint 仍保持 latest-wins：producer
可以在 native renderer 消费前发布多次，但 pending frame 的替换不能破坏 temporal predecessor。

## Temporal history

caller 不提供 previous transform。Underpaint 以 retained drawable 对象为 identity，私有维护：

- 最近一次实际进入 native rendering 的 transform；
- 尚未被消费的 pending transform；
- 主 view 的 current/previous 状态；
- current/previous world-view constant。

正常连续绘制时，第 N 帧的 previous transform 来自同一 drawable 在前一个实际渲染帧中的 current transform。

若多个 producer frame 在 native renderer 消费前互相替换，Underpaint 必须保留最早 pending frame 的 predecessor，
并将它与最后发布的 current transform 配对。中间未消费 frame 不得错误地变成 previous。现有 pending-history
修复表达的正是这个时序不变量；其视觉效果仍未确认，不能仅凭接口迁移宣称 motion artifact 已解决。

以下情况重置 drawable history，并令下一次 Draw 使用 `previous = current`：

- drawable 第一次出现；
- drawable 未出现在一份已发布的完整 frame 中，之后再次出现；
- drawable 被 dispose；
- 所属 `Renderer` 重建或 dispose。

第一阶段 drawable 的 shape 不可变，因此 shape change 应通过 dispose 旧对象并创建新对象表达。只改变 color 或 alpha
不会重置 transform history。未调用 `Publish` 的 frame 对 history 没有影响。

## 内外责任分界

### Event Horizon

- 按业务语义长期持有 drawable；
- 选择当前 transform、color 和 alpha；
- 每个更新周期发布完整 drawable 集合；
- 在逻辑对象消失时 dispose drawable；
- 保证同一个 drawable 在一帧内只 Draw 一次。

### Underpaint

- 校验 shape 参数、transform、color 和 alpha；
- 把 semantic Draw 降低为内部 render command；
- 通过统一的内部 mesh representation 生成并缓存单位 topology，计算 winding、indices 和 normals；
- 对所有无纹理 vertex 写入固定中心 UV；
- 将 shape 尺寸和 current transform 组合为私有 world 输入；
- 管理每 drawable 的 temporal state 和 native instance 状态；
- 管理共享 mesh、native buffer、constant、material donor、hook 和 delayed release；
- 在 native pass 中提交并在异常、替换和 unload 路径恢复或释放状态。

任何内部 mesh cache key、native pointer 或资源槽位都不能穿过公开 API。

## 内部统一 mesh 机制

“Event Horizon 不接触通用 mesh”不等于“Underpaint 内部不应有通用 mesh 系统”。相反，Underpaint 应优先复用或建立
统一的私有 mesh representation/cache，让 semantic shape 只负责产生 mesh 数据：

- triangle、quad 和 icosahedron 是三份不同的 topology/attribute 数据；
- 它们经过同一条 mesh 创建、缓存、native lifetime 和 submission 路径；
- retained drawable identity 与 mesh cache identity 分离；
- 多个同形 drawable 共享 mesh，同时保留独立的 transform、appearance 和 temporal history；
- Event Horizon 看不到内部 mesh definition、cache key、native buffer 或 release protocol。

当前仓库已经有可优先演进的基础：

- `NativeMesh` 统一保存 stream 0、IB 和 vertex/index count；
- `CreateMesh` 与 `ReleaseMesh` 已经是三种固定形状共用的创建和释放函数；
- `NativeBackend` 在提交前通过 `GetMesh` 取得 mesh。

当前结构还不是完整的统一 cache：triangle、quad、icosahedron 分别占用字段，`GetMesh`、`GetVertexCount` 和
`WriteStream1` 仍按 `PrimitiveType` 分支。实施前应先评估如何把这些现有部件收敛为 descriptor-keyed 的内部
mesh definition/cache，而不是绕开它们再建立一套系统。最低目标是让 position、index、normal 和固定 UV 都来自
统一 mesh 数据，并让三种形状走相同的 native resource owner 与 submission path。

这套机制仍是固定、不可变 topology 的内部系统。运行时可变 topology 和 dynamic VB 不在本设计范围内。

## Bar/strip 的开放问题

bar 暂不提供接口。至少需要用户先选择以下几何语义：

- 使用 `length + width + transform`，还是 local/world 的 `start + end + width`；
- 沿局部轴居中，还是从 local origin 向前延伸；
- 固定平面 rectangle，还是始终面向相机的 ribbon；
- 端点是平头、方头还是圆头；
- 后续是否需要折线 join，或第一版只允许单段。

这些选择会改变 transform、history 和 topology 的含义。在决定前预留 `DrawBar`、通用 strip descriptor 或 cap/join
options 都属于猜测，因此明确推迟。

## 非目标

本设计明确不向 Event Horizon 提供：

- 任意 vertices/indices 或 general mesh 输入；
- mesh registration、mesh handle 或 resource ID protocol；
- dynamic vertex buffer 更新；
- texture、sampler、shader 或 material 选择；
- outline、wireframe、concave polygon、带孔 polygon 或任意 triangulation；
- 为未来可能性预留的万能 options 包。

外部自定义 mesh 不是延后项目或路线图候选，而是当前明确不提供的能力。这里的限制不否定上一节要求的 Underpaint
内部统一 mesh representation/cache；本设计的范围只有该内部机制和 Event Horizon 的高层 semantic drawable API。

## 分阶段实施

### 阶段 1：迁移现有形状

- 先评估并复用现有 `NativeMesh`、`CreateMesh`、`ReleaseMesh` 和 `GetMesh` 基础；
- 将三个 shape-specific mesh 字段和分支收敛到统一的内部 mesh definition/cache 与 resource owner；
- 引入 retained drawable 和 explicit frame publish；
- triangle 映射为 `CreateRegularPolygon(3)`；
- quad 映射为 `CreateRegularPolygon(4)`；
- icosahedron 映射为 `CreatePolyhedron(Icosahedron)`；
- 从 Event Horizon 调用点删除 raw `PrimitiveType`、整数 ID 和 previous transform；
- raw command、mesh lookup 和 native resource identity 全部降为 Underpaint internal。

阶段 1 不增加新的可见形状，也不实现 bar。

### 阶段 2：扩大 regular polygon

只有阶段 1 验收完成后，再决定：

- local origin、front axis 和首顶点方向；
- 支持的最小/最大边数；
- unit radius 的定义；
- topology cache 的容量与回收规则。

随后按一个明确边数范围实现并验证 regular polygon topology。

### 阶段 3：单独设计 bar

用户先选择 bar/strip 几何语义，再形成独立的小范围接口和验收标准。不得把 arbitrary mesh 或 dynamic vertex system
作为 bar 的前置条件。

## 可验证验收

### API 与所有权

- Event Horizon 的普通调用不出现 `Primitive`、`PrimitiveType`、previous transform、整数资源 ID、vertices 或 indices；
- 同一 drawable 可跨多个已发布 frame 使用；
- 不同 drawable 即使形状相同也具有独立 history；
- topology cache 以 shape descriptor 共享，而不是以 drawable identity 重复创建；
- triangle、quad 和 icosahedron 只提供不同 mesh 数据，走同一条内部创建、缓存、lifetime 和 submission 路径；
- cross-renderer、disposed drawable 和同帧重复 Draw 被明确拒绝；
- dispose drawable/renderer 后 native 资源按既有安全顺序释放。

### Geometry

- triangle、quad 和 icosahedron 的 vertex/index count、winding 和 normal 符合约定；
- 所有无纹理 vertex 的 UV 都严格为 `(0.5, 0.5)`；
- 三个现有 playground 图元可通过新 API 重现 transform、color 和 alpha 行为；
- 没有每帧重建共享 mesh。

### Frame 与 history

- 未发布 frame 不改变可见 snapshot 或 history；
- 空发布清空 scene；
- 第一帧使用 `previous = current`；
- 连续消费帧使用上一实际渲染 transform；
- 多次 pending replacement 保留最早 predecessor 和最新 current；
- drawable 从完整发布帧缺席后重现时 history 已重置；
- color/alpha 改变不重置 transform history。

### Runtime

- Event Horizon 3D Playground 冷启动、登录、角色选择、显示、隐藏和 unload 保持稳定；
- triangle、quad 和 icosahedron 保持原生深度遮挡、背面裁剪、颜色和 alpha 行为；
- 固定中心 UV 不重新出现局部脏纹理；
- motion-history 视觉结果单独记录为 confirmed、failed 或 inconclusive，不以“构建通过”替代实机判定。
