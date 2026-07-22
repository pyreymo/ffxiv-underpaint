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
  `chara/equipment/e0378/material/v0002/mt_c0101e0378_top_a.mtrl`
- 预期 shader package：`charactertransparency.shpk`
- 中性纹理候选：`chara/common/texture/white.tex`

新实现必须在提交前明确验证 donor 的 shader package。封存的 prototype 加载了 material，但
没有强制执行这一检查。
