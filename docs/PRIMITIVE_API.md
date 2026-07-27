# Retained high-level primitive API 设计

状态：phase 1 已定稿，进入实施。

本文定义 Underpaint 对 Event Horizon 暴露的 retained primitive API。运行时证据和当前实现状态仍分别记录在
`RESEARCH.md` 与 `STATUS.md`；本文只描述正式接口、所有权和验收边界。

## Phase 1 范围

首阶段只提供两个高层 drawable：

- triangle；
- rectangle。

不在首阶段加入 regular/general polygon、polyhedron 扩展或 bar/strip。现有 icosahedron 实验不要求迁入新公开
API。

Event Horizon 不注册 mesh，不持有 mesh handle、VB、IB 或 GPU resource ID，也不提交 vertices、indices、
previous transform、dither options、texture、shader 或 material。外部自定义 mesh 输入明确不提供，也不是本设计
的后续路线图。

## Geometry contract

### Triangle

triangle 是单位边长的等边三角形：

- 三个顶点位于 local XY 平面；
- 几何中心位于 local origin；
- front normal 为 `+Z`；
- winding 从 front 观察为逆时针；
- 尺寸变化由 caller 的 current transform 表达。

若边长为 1、高为 `sqrt(3) / 2`，建议 local 顶点为：

```text
(-1/2, -sqrt(3)/6, 0)
( 1/2, -sqrt(3)/6, 0)
(   0,  sqrt(3)/3, 0)
```

### Rectangle

rectangle 由 retained drawable 的 width 和 height 定义：

- 底层共享一个 `1 × 1` unit rectangle mesh；
- unit mesh 位于 local XY 平面，范围为 `[-0.5, 0.5] × [-0.5, 0.5]`；
- 几何中心位于 local origin；
- front normal 为 `+Z`；
- winding 从 front 观察为逆时针；
- width 和 height 必须为有限正数；
- width 等于 height 时就是正方形。

Underpaint 在记录 Draw 时把 width/height 安全地组合到实例 current transform。Event Horizon 只操作高层尺寸，不接触
mesh 或 vertex 数据。rectangle 尺寸改变属于 transform history 的一部分。

所有 phase-1 无纹理 vertex 固定使用 `TexCoord0 = (0.5, 0.5)`。

## 建议的公开 API

```csharp
public sealed class Renderer : IDisposable
{
    public TriangleDrawable CreateTriangle();

    public RectangleDrawable CreateRectangle(float width, float height);

    public PrimitiveFrame BeginFrame();
}

public sealed class TriangleDrawable : IDisposable
{
}

public sealed class RectangleDrawable : IDisposable
{
    public float Width { get; }

    public float Height { get; }

    public void Resize(float width, float height);
}

public sealed class PrimitiveFrame : IDisposable
{
    public void DrawTriangle(
        TriangleDrawable drawable,
        Matrix4x4 transform,
        Vector3 color,
        float alpha = 1f
    );

    public void DrawRectangle(
        RectangleDrawable drawable,
        Matrix4x4 transform,
        Vector3 color,
        float alpha = 1f
    );

    public void Publish();
}
```

典型调用：

```csharp
private readonly TriangleDrawable triangle = renderer.CreateTriangle();
private readonly RectangleDrawable rectangle = renderer.CreateRectangle(2f, 1f);

public void SubmitScene()
{
    using var frame = renderer.BeginFrame();
    frame.DrawTriangle(triangle, triangleWorld, triangleColor, triangleAlpha);
    frame.DrawRectangle(rectangle, rectangleWorld, rectangleColor, rectangleAlpha);
    frame.Publish();
}
```

drawable 对象本身表达稳定逻辑身份。公开 API 不出现 caller 分配的整数 ID。Underpaint 可以私有分配内部 identity，但
该值不是 mesh key 或 GPU resource ID，也不能跨出程序集边界。

## Drawable 生命周期

1. caller 通过所属 `Renderer` 创建 drawable；
2. caller 在多个已发布 frame 中重复 Draw 同一个对象；
3. caller 不再需要实例时 dispose drawable；
4. disposed drawable 不能再次 Draw；
5. dispose `Renderer` 会使其创建的全部 drawable 和未发布 frame 失效；
6. frame 拒绝其他 renderer 创建的 drawable，也拒绝同一个 drawable 在一帧内 Draw 两次。

triangle 的 geometry 不可变。rectangle 的 width/height 可通过 `Resize` 修改，但仍共享同一个 unit rectangle mesh；
resize 不创建新的公开 identity。

drawable dispose 后，Underpaint 在 native submission 所属线程安全回收其 instance resources。共享 mesh 资源由
Underpaint 独立管理，不随单个 drawable dispose。

## Frame publish 与 consumption

`PrimitiveFrame` 收集一份完整的 scene snapshot：

- `BeginFrame` 创建尚未发布的 builder；
- Draw 只记录 drawable、current transform、color 和 alpha；
- `Publish` 冻结 snapshot，并原子替换尚未被 native renderer 消费的 pending frame；
- 一个 frame 只能发布一次；
- dispose 未发布 frame 只丢弃 builder，不改变 pending frame；
- 发布空 frame 取消当前 scene；
- 已发布 frame 不借用 caller 的可变数据。

retained 指 drawable identity，而不是自动持续重绘的 scene。Event Horizon 仍在每个 framework update 发布当前完整
drawable 集合。native renderer 每个 game frame 最多消费一个最新 pending snapshot。

## Temporal history

caller 不计算或提交 previous transform。Underpaint 私有维护：

- 每个 retained drawable 最近一次实际消费的 current transform；
- rectangle width/height 合成后的完整实例 transform；
- main view 的 current/previous 状态；
- current/previous world-view constant。

消费 snapshot 时：

- 首次出现使用 `previous = current`；
- 连续出现使用该 drawable 上一次实际消费的 current；
- producer 在 native consumption 前连续发布 B/C/D 时，中间 snapshot 不进入 history；若上次实际消费为 A，则最新
  snapshot 使用 A 作为 previous；
- drawable 缺席任何一份已发布的完整 frame 后，continuity 立即重置；之后重现使用 `previous = current`；
- dispose drawable 或 renderer 会删除其 history；
- color/alpha 改变不重置 transform history；
- rectangle resize 会改变实例 transform，因此参与正常 history，而不是强制 reset。

motion-history 状态机可做确定性测试，但其拖影视觉改善仍必须单独实机确认。

## Underpaint 内部统一 mesh 机制

Underpaint 内部应复用统一的私有 mesh definition/cache：

- unit equilateral triangle 和 unit rectangle 是两份 immutable mesh data；
- mesh definition 同时拥有 position、normal、fixed UV 和 indices；
- 两种 shape 经过同一条 native VB/IB 创建、缓存、lifetime 与 submission 路径；
- drawable identity 与 mesh cache identity 分离；
- 多个同形 drawable 共享 mesh，同时拥有独立 transform、appearance、history 和 instance resources。

当前 `NativeMesh`、`CreateMesh`、`ReleaseMesh` 和 `GetMesh` 是优先演进对象。实现应移除 triangle/rectangle 各自的
vertex-count/attribute 分支，让 stream 0、stream 1 模板和 IB 都由统一 mesh definition 驱动。

运行时可变 topology、dynamic VB 和外部 mesh registration 不在本设计范围内。

## Event Horizon 3D Playground

窗口只保留本次流程需要的交互：

1. 初始没有 drawable；
2. 首次点击 `Generate triangle` 时，以本地玩家当前位置为 anchor 创建 triangle；
3. 首次点击 `Generate rectangle` 时，以本地玩家当前位置为 anchor 创建 rectangle；
4. 创建后显示对应 position 控件；
5. rectangle 额外显示 width 和 height 控件；
6. 每次 framework update 将已创建实例的 current transform、固定 color/alpha 加入完整 frame 并 Publish；
7. window/plugin dispose 时先 dispose drawable，再 dispose renderer。

triangle/rectangle 的 local front 为 `+Z`。Playground 若要把它们平放在玩家脚下，应在 current transform 中加入从
local XY 到 world XZ 的固定旋转；这属于高层放置 transform，不改变 geometry contract。

不恢复旧 group-motion debug slider，不暴露 dither，不要求保留旧 triangle/quad/icosahedron 的实验控件。

## Phase 1 验收

### Geometry

- triangle 三边相等、centroid 为原点、3 vertices/3 indices、winding 和 normal 指向 `+Z`；
- rectangle 为中心原点的 unit square、4 vertices/6 indices、winding 和 normal 指向 `+Z`；
- 所有 UV 严格为 `(0.5, 0.5)`；
- width/height 只改变 rectangle 实例 transform，不创建新 mesh；
- 两种 shape 走同一个内部 mesh cache/resource owner/submission path。

### API 与 history

- Event Horizon 不出现 raw primitive type、整数 ID、vertices/indices、previous transform 或 dither；
- cross-renderer、disposed drawable 和同帧重复 Draw 被拒绝；
- 未 Publish frame 无影响，空 Publish 取消 scene；
- first、continuous、pending overwrite、absence/reappearance 和 dispose history 行为有确定性验证；
- drawable native resources 只在安全 native 路径创建和回收。

### Runtime

- 两个按钮首次点击时在本地玩家脚下生成对应实例；
- position 控件能独立移动两个实例；
- rectangle width/height 控件能独立改变尺寸；
- triangle/rectangle 保持 native depth、back-face culling、color 和 alpha 行为；
- 固定中心 UV 不重新出现 primitive-local 脏纹；
- 冷启动、登录、角色选择、reload 和 unload 稳定；
- motion-history 视觉结果记录为 confirmed、failed 或 inconclusive，不能以 build 或状态机测试代替。
