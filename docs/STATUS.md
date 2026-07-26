# Underpaint 当前进展

更新时间：2026-07-26

## 当前范围

第一版只实现固定的 `charactertransparency.shpk` 半透明路径。它不是通用模型渲染器，也不支持调用者提供
material、shader、纹理或 GPU resource。

当前公开输入是每帧一组 `Primitive`：

- `Triangle` 和 `Quad`；
- 稳定 ID；
- current/previous transform；
- RGB；
- smooth alpha；
- 实验性的 `DitherFade`。

`alpha = 1` 仍然只是半透明路径中的完全不透明，不是真正的 opaque material profile。

## 已验证的执行路径

顶层提交可以按以下顺序阅读：

1. `Renderer.SubmitFrame` 发布完整图元集合；
2. `NativeBackend` 等待 view 30 / subview 11 的原生 pass-builder rendezvous；
3. 固定 donor 由 ResourceManager 独立加载并持有；
4. 调用参数、model 和 shader selection 从清零状态初始化；
5. 调用原生 `OnRenderMaterial` 和 `ApplyMaterial`；
6. 只在当前 active pass 存在最终 VS/PS 时继续；
7. 从 view 30 / subview 12 读取主 render camera；
8. 安装 Underpaint 自己的 geometry、declaration、world、instance/model/material constants 和固定纹理；
9. 每个图元调用一次原生 pass builder；
10. 无论成功或失败都恢复修改过的 graphics context 状态。

实机已经确认：

- 固定 donor 是 `chara/equipment/e0907/material/v0001/mt_c0101e0907_top_b.mtrl`；
- SHPK 是 `charactertransparency.shpk`；
- 三角形和四边形可见，并固定在世界位置；
- 两个图元可同时使用不同颜色和 alpha；
- 原生深度遮挡、背面裁剪和透明 pass 生效；
- context restore 已验证；
- 插件冷启动、登录和角色选择界面稳定。

## Underpaint 已经拥有的输入

- triangle/quad 的 stream 0、IB 和 vertex declaration；
- 每个稳定 ID 独立的 stream 1；
- current/previous world-view constant；
- 每个 ID 独立的 instance constant；
- model constant 和 material constant；
- RGB、smooth alpha 和 dither fade；
- normal/index/table 当前使用的固定 `white.tex` binding。

stream 0、stream 1 和 IB 都使用从原生静态刚性几何捕获的 `0x804` 创建参数。alpha 量化到
`Color0.a`；只有量化后的 byte 改变时才重建该 ID 的静态 stream 1，并通过游戏的 delayed-release
生命周期释放旧资源。

## donor 仍然提供的内容

donor 只保留：

- 运行时 `Material*`；
- `OnRenderMaterial` / `ApplyMaterial` 所需的合法 material 输入；
- 固定 shader package 的 selection/helper 行为。

当前仍未完全解释：

- selected variant 中所有 material key 和 pass flag 的语义；
- sampler ID 5、6、62 的正式资源语义；
- sampler state 中除 `Texture*` 外的字段；
- 局部材质脏痕所对应的 shader 输入。

代码不会复制场景角色的 shader selection、constant buffer、texture binding、descriptor 或 draw command。

## 已定位并删除的不稳定路径

`28c2cfd` 首次引入 `0x801` dynamic stream 1 和手写 Map/Unmap。冷启动二分确认它是 GPU hang 的第一个
坏提交；上一提交 `46db115` 稳定。虽然当前二进制中的 Map/Unmap 槽位可以解释，但没有原生样本证明
该 dynamic buffer 能按当前方式交给 pass builder 延迟消费，因此整条路径已经删除。

以下纹理实验也已回退：

- 自建 `8×32 R16G16B16A16_FLOAT` neutral color table；
- 自建 `4×4 B8G8R8A8_UNORM` 全零 index texture；
- 覆盖未识别 sampler ID 49。

这些实验没有得到纯色结果，也没有形成足够的原生来源证据。当前代码行为恢复到
`658c1a4 Replace unsafe dynamic vertex streams`。

## 当前视觉限制

- 最近的自建 index/table 纹理会产生不需要的图案，现已移除；
- 回退后应恢复原来的基本纯色外观，但此前观察到的局部横向“脏痕”可能仍然存在；
- 镜头运动时边缘有轻微拖影，暂时记录，不在当前阶段增加 velocity pipeline；
- 背面不可见，当前按原生背面裁剪处理。

## 下一步最小任务

先只解决固定 shader variant 的纯色输入，不再创建或替换未知纹理。

建议顺序：

1. 冷启动确认本次回退恢复稳定、无新图案；
2. 对比固定 shader selection 的 material keys 和 416-byte material defaults，找出仍启用颜色表、tile、
   sphere-map 或其他局部材质效果的明确开关；
3. 每次只修改一个有来源的 key 或 material field，验证局部脏痕；
4. 得到纯色后删除 probe，并把固定值的来源和验证结果写入 `RESEARCH.md`。

在纯色三角形稳定前，不继续加入圆盘、圆环、扇形或球体。
