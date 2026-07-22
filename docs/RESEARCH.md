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

## Material helper 状态差分

在游戏自然执行 `charactertransparency` 路径时，分别比较 `OnRenderMaterial` 和
`ApplyMaterial` 调用前后的当前 graphics context、调用参数和 shader selection：

- `OnRenderMaterial` 只改变 sampler 62，并写入 material 参数的 `0x40-0x41`；
- `ApplyMaterial` 只改变 constant 25、sampler 6 和 shader selection 的 `0x18-0x1C`；
- 两个 helper 都没有改变被观察的 shader、geometry、stream 或 rasterizer context 字段；
- 其他 constant 和目标 SHPK 声明的其他 sampler 均未改变。

因此固定路径调用两个 helper 时，只保存并恢复 constant 25、sampler 6 和 sampler 62。调用参数
和 shader selection 使用 Underpaint 自己的临时内存，其输出会继续用于 selection 解析，不属于
需要恢复的外部 context 状态。该结论只适用于当前固定 donor 和 `charactertransparency.shpk`，
不能推广为通用 material helper 契约。

## Material constant 绑定捕获

2026-07-22 在自然 `charactertransparency` draw 的 `ApplyMaterial` 返回后读取当前 graphics
context，并将 SHPK package constant 表与运行时绑定逐项对应。捕获只接受 view 30、subview 11，
没有复制 constant 内容。

结果如下：

- `MaterialParameterCBuffer` 大小为 416 bytes；其原生创建参数为 flags `0x4`、最后一个参数
  `0`；
- package constant CRC `0x20A30B34` 对应运行时 ID 34、size 11、slot 1；Apply 后 ID 34
  已绑定一个 constant buffer；
- package constant CRC `0x4E0A5472` 对应运行时 ID 35、size 1、slot 1；Apply 后 ID 35
  已绑定一个 constant buffer；
- CRC `0x5B0F708C` 不存在于当前 `charactertransparency.shpk` 的
  `ShaderPackage.ConstantsSpan`。这只否定“它是 package constant”这一假设，不能证明它不会出现在
  某个最终 shader 的 stage-specific resource 表中；
- `ModelRenderer.ConstantSamplerIds[1]` 的值为 5，但 Apply 返回时 context ID 5 仍为空。这个时点
  不能用于验证 world constant；world 输入必须在后续 model/pass-builder 边界单独处理；
- ID 34 和 35 指向的 buffer 在安装调查 hook 前已经创建，因此本次没有得到它们的创建 flags，
  也没有读取其内容。

这次结果证明不能把所有旧 prototype CRC 当成目标 SHPK 的 package constants，也不能把 renderer
表中的 ID 直接当作 material helper 已完成的绑定。第一版只使用当前固定 SHPK 实际声明的 ID 34
和 35；若后续最终 shader descriptor 还要求 stage-specific constant，必须从解析后的 descriptor
单独验证，不能在 package 表中硬编码查找。

## 自有 constant buffer 创建约定

自然 material constant 的创建捕获只证明了 flags `0x4`、最后一个参数 `0`，没有证明该模式
支持 Underpaint 所需的 `LoadSourcePointer` 写入。封存的 native submission prototype 使用 flags
`0x2`、最后一个参数 `0` 创建 world、color、instance、model 和 material constant buffers；这些
buffer 的 `LoadSourcePointer` 返回可写存储，并已实际用于原生 draw submission。

第一版因此使用 `0x2` 创建 Underpaint 自有、需要由 CPU 明确写入的 constant buffers。代码中只将
它命名为 `WritableConstantBufferFlags`，不猜测各 bit 的官方枚举含义。`0x4` 继续作为自然 material
资源的观察值保留，但不要求自有 buffer 复刻它。

该决定的验证边界是：创建成功、`LoadSourcePointer` 非空、写入内容能用于原生 command、每帧更新
能反映到画面、卸载时资源正常释放。完成这些验证即可继续第一版，不需要先逆向每个 flag bit。

首次实现错误地在 Dalamud 插件构造线程创建并调用 `LoadSourcePointer`。2026-07-22 的热加载和
进程重启各产生一次相同的 `C0000005`：`CreateConstantBuffer` 返回非空，但第一只 128-byte world
buffer 的 `LoadSourcePointer` 在 native graphics context 为空时解引用失败。封存 prototype 中同一
调用实际发生在 render rendezvous 内，因此 flags `0x2` 的验证条件必须包含有效 render thread 和
graphics context。

修正后，几何仍在 `NativeResources` 构造时创建；四个 constant buffers 延迟到首次经过已验证的
view 30、subview 11 pass-builder rendezvous 时创建并清零。正常世界渲染会持续经过该边界；标题、
角色选择或加载场景暂时没有该 view 时，资源保持未初始化且不提交，进入正常世界后再完成一次性
初始化。初始化失败只记录一次并停止后续提交，不在 render hook 中重试。

修正版本实机输出 `Native constants and material helpers verified`，证明四个 flags `0x2` buffer 在
view 30、subview 11 的 render context 中能够创建、取得可写存储并与两个 material helper 共存。

封存 prototype 对最终 vertex shader 的反汇编和运行时值捕获还确认：package constant CRC
`0x4E0A5472` 是单个 `float4` model 输入，当前 variant 只读取其 `x`，自然路径的值为 `1`。第一版
因此将自有 16-byte model constant 明确初始化为 `(1, 0, 0, 0)`，并在运行时再次校验固定 SHPK
仍将该 CRC 声明为一个 register。此时尚未调用 pass builder，所以本步骤不安装 context binding；
绑定与实际 builder 消费必须在同一后续提交中完成。

同一封存实验确认 CRC `0x20A30B34` 是 11 个 `float4` 的 per-instance 输入。第一版的中性值
不是从当前角色复制：register 0 到 3 为全白乘色，register 4 为 `(0, 2, 0, 1)` 的无 wetness
范围默认值，register 10 为 `(0, 1, 0, 0)` 的默认 head-up，其余 register 清零。这组值已经在
封存 prototype 中替换角色 instance constant 并用于原生 command；各 register 的完整通用语义仍
未建立，因此代码保留 register 编号，不为每个分量猜测字段名。

当前最小实现据此写入自有 176-byte instance constant，并在运行时校验固定 SHPK 仍将该 CRC
声明为 11 个 registers。与 model constant 相同，本步骤只建立自有内容和 ID 映射；context 安装
留到有实际 pass-builder 消费的提交中。

固定 `ShaderPackage` 自身提供完整的 `MaterialElementDefaultsSpan`，其长度由
`MaterialConstantBufferSize` 指定。第一版使用这份 SHPK canonical defaults 初始化 Underpaint
自有 material constant，而不是复制 donor `MaterialParameterCBuffer`。当前固定 SHPK 的大小必须
仍为实机捕获的 416 bytes，否则初始化明确失败。

自然 `ApplyMaterial` 状态差分已经定位运行时 material constant ID 25。当前实现进一步在 helper
调用后验证 context ID 25 确实等于固定 material 的 `MaterialParameterCBuffer`，同时验证自有 buffer
大小与 SHPK 一致。自有内容和运行时 ID 至此都已确定，但在实际 builder 接入前仍不替换该 binding。

FFCS 将 `ModelRenderer.ConstantSamplerIds[1]` 明确标为 `g_WorldViewMatrix`；本次固定路径实测该
运行时 ID 为 5。封存 prototype 已验证主 view 30 的 transform camera 位于 subview 12，虽然提交
rendezvous 本身发生在 subview 11。二者属于同一主 view 的不同职责，不能用当前 rendezvous
subview 直接索引 camera。

`Render.Camera.ViewMatrix` 物理上是 64-byte `Matrix4x4`，但原生 affine 路径只写入和读取 3x4
payload；第四列的四个存储槽可能未初始化。托管乘法前必须明确设为 `(0, 0, 0, 1)`。固定三角形
的初始 world 为单位矩阵，128-byte world constant 写入转置后的 `world * view` 两次，使首帧
current 与 previous 完全相同。该数据完全由 Underpaint world 和当前公共 camera/view 状态构造，
不读取现场模型的 world constant。

2026-07-22 实机再次确认当前固定 donor 的完整映射：material constant ID 25、instance constant
ID 34、model constant ID 35、world constant ID 5。四项均来自同一次 view 30、subview 11 的
`e0907 + charactertransparency.shpk` 初始化，不再只是封存 prototype 的历史值。

## 最终 shader descriptor 的资源输入

2026-07-22 对当前 `e0907 + charactertransparency.shpk` 的解析结果做了一次只读、单次 Debug
记录。probe 只读取最终 descriptor 中各 `PVShader` 的 resource entry，没有读取资源内容、修改
graphics context 或调用 pass builder；取得结果后已从代码删除。

解析出的 shader family 对应 pass 1、4、8、11。slot 小于 256 的 sampler entry 是纹理输入；
`S256` 及以上的记录属于同一表中的 sampler-state entry，不能误认为额外纹理。包含自有
instance/model 输入的 pass 4、8、11 均使用以下三项固定材质纹理：

- CRC `0x0C5EC1F1`：normal，运行时 ID 5，material sampler class 2；
- CRC `0x565F8FD8`：index，运行时 ID 6，material sampler class 2；
- CRC `0x2005679F`：table，运行时 ID 62，sampler class 1。

pass 4 还使用 ID 49 / CRC `0x800BE99B`；pass 8、11 继续增加灯光和 view 相关 constant 与纹理。
这些 class 1 输入随 shader family 扩展，当前只把它们视为 system/scene 候选，正式名称和所有权
必须在实际 pass-builder family 确定后再验证，不能用白纹理覆盖。

当前先由 Underpaint 独立加载并持有 `chara/common/texture/white.tex`，同时验证上述三项 sampler
CRC、ID 和 class 仍与固定 SHPK 一致。该资源在本步骤尚未安装；`WhiteTexture` 只描述其实际
内容，不预先声称它对 normal、index 和 table 三种 shader 语义都是正确中性值。下一步应通过
最小实际提交分别验证这些绑定，而不是把同一白纹理一次性覆盖所有未知 sampler。

首次实际提交在 sampler ID 5 读取纹理对象时崩溃。崩溃前对照固定白纹理 handle 与 donor 的四个
自然纹理 handle 后确认：自然 `TextureResourceHandle` 的 `Texture*` 均位于 FFCS 声明的 `+0x128`，
而白纹理对象的同一区域是连续的打包数值，不是指针。原因不是结构偏移，而是加载时把 TEX resource
type 误写成了 `0x00786574`；游戏使用的 `tex` 值是 `0x00746578`。错误 type 返回了另一种已加载
handle，强制转换和非空检查因此没有发现类型错误。修正后继续使用 `TextureResourceHandle+0x128`
的 `Kernel::Texture*`，临时 handle 内存 dump 已删除。

在调用 pass builder 前，context 状态恢复被拆成单独验证步骤。geometry 字段 `0x888/0x890`、
四条 stream binding 起点 `0x8C0`、constant 表起点 `0x940` 和 sampler 表起点 `0x1140` 均来自
封存 prototype 中已经实际生成原生 command 的 context 布局；FFCS 当前没有公开这些字段。

当前实现只保存并修改明确归 Underpaint 所有的两条 geometry stream、IB、vertex declaration、
world/material/instance/model 四个 constant 槽，以及 normal/index/table 三个纹理槽。安装时清空未用
的 stream 2 和 3；纹理槽写入白纹理并使用 prototype 已验证的零 unknown/flags。随后不调用
builder，立即逐字段核对安装结果、恢复原值并再次逐字段核对。这个实验只能验证状态边界和恢复，
不能证明白纹理的视觉语义；视觉验证仍留给下一次最小 draw。

实机确认 `ContextRestore=verified` 后，逐字段安装/恢复核对已删除。保存和恢复本身保留为实际提交
边界。shader selection、`Model`、model 参数和 material 参数现在由顶层提交函数的栈内存持有：
先初始化 selection，再保存 context，随后依次调用 `OnRenderMaterial`、`ApplyMaterial`、解析最终
descriptor、安装自有输入、恢复 context，最后销毁 selection。这样下一步调用 builder 时 selection
能够覆盖完整调用区间，不需要复制现场 selection 或延长 frame pointer 生命周期。

第一次实际 builder 调用只请求主 view：`OnRenderMaterialParams2+0x38` 明确写入封存 prototype
已验证的主提交 gate `0x01000000`，辅助 gate `0x00C00000` 和 `+0x44` view mask 保持为零。
`+0x40` 从零开始并只接受当前固定 material 的 `OnRenderMaterial` 输出，不复制 carrier pass flags。

最终 descriptor 按当前 context byte `0x0B` 的低四位选择 active pass，并解析对应 VS/PS；同一
context scope 保存和恢复 `0x878`、`0x880`、`0x8B8` 的 shader 与 descriptor 字段。随后安装自有
geometry、constants 和三项白纹理，在 selection 与调用参数仍存活时同步调用一次原生 builder，
请求三顶点、起始索引零、三个索引。辅助 view 将在主 view command 单独验证后再打开。

builder 的返回寄存器没有稳定语义，首次运行看到的 `0x300` 不能作为成功或 command 数量。当前
改为读取 FFCS 已公开的 `Context.CommandAllocationBase` 和 `CommandAllocationUsedSize`。原生
`AllocateCommand` 使用 128 KiB 分段：当前段不足时会更换 base，并从较小的 used size 重新开始，
因此不能只要求 used size 单调增加。只有 base 未变且 used 未增加才明确失败；日志记录完整前后二元组。
这只能证明 builder 向当前原生 command arena 分配了 command data，不解释 command 数量，也不恢复
D3D11 draw capture。

`OnRenderModelParams+0x10` 同时明确写入自有 176-byte instance constant。FFCS 当前仍将该字段
标为 private unknown，但自然路径运行时映射和封存 builder 成功样本都将它对应到 ID 34 / CRC
`0x20A30B34`；因此它是当前固定路径必须同时提供的调用参数，不只是一项 context binding。
