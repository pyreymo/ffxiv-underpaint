# 保留的渲染研究结论

本文只保留重建 Underpaint 所需的结论。实验实现细节继续由封存源码和 Git 历史保存。

## 原生提交边界

- 原生 material 展开必须运行在游戏 render thread，并使用当前 `ModelRenderer`、graphics
  context、view、subview 和 frame allocator 状态。
- 固定 `.mtrl` 可以通过 `ResourceManager` 独立加载并持有，不需要由场景物体持有。
- 已验证的 helper 顺序为：初始化 shader selection、调用 `OnRenderMaterial`、调用
  `ApplyMaterial`、解析最终 selection、安装资源、调用原生 pass builder。
- 周围原生状态有效时，pass builder 可以从 Underpaint 自己的顶点和索引缓冲生成主 view
  和辅助 view command。
- 同步 builder 调用结束后，必须恢复提交过程中修改过的全部 graphics context 字段。

## 所有权目标

固定 donor material 最终只应提供它的运行时 `Material*`、`charactertransparency.shpk` 和两个
material helper 的行为。Underpaint 必须提供 geometry、vertex declaration、current/previous
world、color 和 alpha、model/instance constants、material constants、中性纹理，以及必需的
中性 color table 资源。

封存的 prototype 已经开始替换这些输入，但其中的 material constant 内容和 color table 仍然
来自 donor，并且尚未证明 helper 安装的所有纹理绑定均为中性资源。这些部分必须重新建立，
不能把旧实现直接视为已完成行为。

## 从运行时研究保留的约束

- 不要从场景物体复制 shader selection 输出、pass flags、constant buffers、texture bindings、
  shader descriptor、展开后的 command 或未知 callback 结果。
- 不要从任意 Dalamud callback 调用 pass builder，也不要跨帧持有 frame arena pointer。
- `DrawIndexed` 调用栈只能识别 executor，不能证明 command producer 或其所有权规则。
- 旧的手写半透明 G-buffer/composite 实现属于近似路径，不进入新 backend。
- 函数签名、原生偏移、view index 和 shader layout 都与游戏版本相关；已验证的契约失效时必须
  明确停止提交。

## 需要重新验证的固定 prototype 输入

- Donor material：
  `chara/equipment/e0907/material/v0001/mt_c0101e0907_top_b.mtrl`
- 预期 shader package：`charactertransparency.shpk`
- 中性纹理候选：`chara/common/texture/white.tex`

新实现必须在提交前明确验证 donor 的 shader package。封存的 prototype 加载了 material，但
没有强制执行这一检查。

首次重新验证时，旧 donor `e0378/v0002/mt_c0101e0378_top_a.mtrl` 实际返回
`characterlegacy.shpk`，因此已明确弃用。当前改用
`e0907/v0001/mt_c0101e0907_top_b.mtrl`；加载后仍必须由运行时检查确认它使用
`charactertransparency.shpk`。

## 两条 stream 顶点布局的原生捕获

2026-07-22 在 `ModelRenderer.OnRenderMaterial` 中捕获到一个使用
`charactertransparency.shpk` 的两条 stream draw。这个样本用于验证固定三角形的资源创建参数
和 vertex declaration；不用于复制 shader selection、pass flags、constant buffer、纹理绑定或
其他 callback 输出。

最初用 `Model.BoneCount == 0` 寻找非蒙皮 draw 的假设不成立：703 次目标 SHPK 调用中没有一次
满足该条件，而最终匹配的两条 stream draw 的 `BoneCount` 为 3。`BoneCount` 是 model 级信息，
不能作为当前 draw 顶点布局的筛选条件。改为观察实际绑定后，15 次目标调用中有 1 次使用两条
stream，另外 14 次使用三条 stream。

捕获结果如下：

- vertex buffer：flags 为 `0x804`，第四个创建参数为 `7`，创建后初始化一次；
- index buffer：flags 为 `0x804`，第三个创建参数为 `1`，第四个创建参数为 `0`，创建后初始化
  一次；后两个参数的正式含义尚未确认；
- 两条 stream 共用一个 vertex buffer，通过不同 byte offset 绑定；
- stream 0 stride 为 20，stream 1 stride 为 24；
- vertex declaration 有七条四字节记录，与 Underpaint 当前声明逐字节一致：

```text
stream  offset  format  attribute
0       0       0x13    0
0       12      0x3C    1
0       16      0x3C    7
1       0       0x1C    2
1       8       0x24    15
1       12      0x24    3
1       16      0x1C    8
```

因此，第一版可以把 `0x804`、两个 stride 和七条 declaration 记录视为已经从兼容的原生
两条 stream `charactertransparency` 路径验证过的固定输入。各 flag bit、format 和 attribute 的
通用官方语义仍然未知；这不影响固定路径的字节级一致性。

本次捕获没有读取原生 vertex buffer 内容，所以 `Attribute1`、`Attribute2`、`Attribute3`、
`Attribute7`、`Attribute8` 和 `Attribute15` 的默认 packed value 尚未由这个实验验证。它们仍需
通过只改变 Position、UV 和疑似颜色输入的独立实机实验确认。
