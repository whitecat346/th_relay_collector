# TUI UI/UX 改造计划

> 2026-10-19 经 `/grill-me` 逐轮确认后定稿。本文是实施期的唯一决策记录：改动与本文冲突时，
> 先改本文再改代码。

## 1. 要解决的问题

用户实测（m00001）：

1. review 中查看证据链（C 键）时渲染错乱：新内容叠加在旧内容上；
2. review 任意操作时底部操作说明重复打印；
3. 可读性差、输出未结构化、不随终端大小自适应、缺少彩色表达。

第 1、2 条是**同一个 bug**：视图算出的帧高超过终端高度 → 终端滚动 → 原来的
`\u001b[H` 绝对归位落到视口上方 → 上一帧残留在下方、`\u001b[J` 也清不掉 → 新旧帧同屏。

## 2. 已确认的决策

| # | 决策 |
|---|---|
| 范围 | 先修 P0 渲染正确性，同期把渲染收敛到统一层，再做设计系统与自适应；不重写业务逻辑 |
| 基座 | 引入 NuGet；选 Spectre.Console 0.57.2（排版/样式/能力检测/备用屏/TestConsole）+ Wcwidth 4.0.1（CJK 宽度） |
| 备用屏 | review / evidence-chain 交互视图切备用屏全帧刷新；一次性命令输出（扫描结果、导出）保持内联 |
| 兼容 | 一级 Windows Terminal；二级 VS 调试控制台与旧 conhost（16 色、关备用屏）；非 TTY 自动退化纯文本；最低 80×24，<80 列降级单栏 |
| 颜色 | 语义色板 + 自动降级 truecolor→256→16→无，遵循 `NO_COLOR` 与 `TERM=dumb`；v1 不做主题切换 |
| 结构 | 块语法（面包屑/标题/分隔线/正文/状态栏）+ 表格；多面板只在 review 内部（左列表右详情） |
| 优先级 | P0 两个渲染 bug → P1 自适应+彩色 → P2 结构化/可读性 |
| 输入模型 | 短 ASCII 字段（棒号/时段/类型…）帧内输入；备注 N 保留 `Console.ReadLine`（中文 IME 最稳） |
| 导航 | Esc 只返回上一级，Q 退出应用；有未保存改动时退出前拦一次确认 |
| 80 列取舍 | chain 保留 棒/时段/创作者/方向，来源与置信挪到 Enter 节点详情，@反指仅在 ≥120 列显示；review 保留 棒/创作者/状态，时段压到 6 列，依据吃剩余宽度 |
| 长文本 | 列表截断；详情折行 2 行；Enter 打开只读全文查看器（可滚动，Esc 关）；不做横向滚动 |
| 底部两行 | 第 1 行 toast（无内容也占位），第 2 行键位；窄窗只显示 4~6 个核心键 + `?` 展开帮助；行数由渲染器自报，禁止 `reserved = 8` 这类硬编码 |
| 滚动 | 滚动窗口（↑↓/PgUp/PgDn/Home/End）+ 右上 `3/27` 位置指示，不分页 |
| 命令行 | 新增 `--width/--height`、`--no-alt-screen`、`--no-color`；`--dump/--plain` 只作 AI 测试验收通道，不视为兼容契约 |
| 测试 | `tests/JieLi.Tui.Tests`（xunit + Spectre TestConsole），把各视图帧与 `--dump` 在 80/100/140/200 列 × 有/无颜色下渲染成纯文本快照；关键画面截图存 `docs/screens/` |
| v1 不做 | 鼠标 |

## 3. 目标架构

```
src/JieLi.Tui/
  Tui/Rendering/
    Screen.cs       备用屏进出；相对光标回退；resize；非 TTY 顺序输出
    FrameBuilder.cs 声明式固定行（header/subheader/rule/status/keys）+ body 吃剩余高度；自报行数
    Theme.cs        语义角色（Title/Accent/Success/Warning/Error/Muted/Selected/Link）→ Spectre Style，
                    按 Capabilities 降级
    TableBuilder.cs 列优先级 + 最小宽 + 总宽恒等于 width
  Text/TextWidth.cs Wcwidth 驱动（已落地）
```

视图层（`TuiApp` / `ReviewView` / `ChainView`）保留自己的数据与键位，只把「怎么画」交给上面四个组件。

## 4. 实施步骤

- [x] **Step 0（不引库的最小修复，可独立回滚）**：`ConsoleHost` 改相对回退 + 高度裁剪 + chrome 自报；
      三个视图的 `reserved` 修正；`Console.ReadLine` 后调用 `InvalidateFrame()`。
- [x] **Step 1（基座）**：引入 Spectre.Console + Wcwidth；`TextWidth` 改为 Wcwidth + 字素簇 + ANSI 感知；
      建 `tests/JieLi.Tui.Tests`（23 个用例：宽度语义、帧不变量、裁剪）。
- [x] **Step 2（设计系统）**：`Theme` + `FrameBuilder`；底部固定两行；头部统一为面包屑/标题/统计/分隔线；
      表格改列优先级实现。
- [x] **Step 3（终端适配）**：备用屏 + `--no-alt-screen` 内联回退；`--width/--height` 固定视口；resize 由每帧探测+整屏重绘覆盖；`--no-color`/NO_COLOR 降级；`--ascii` 把 ★ ⚠ → ← ↔ · … ─ 降级成 ASCII（字体缺字/ambiguous 算 2 列时用）。
- [x] **Step 4（交互）**：Esc/Q 语义、滚动窗口 + 位置指示、`V` 全文查看器、链条面板 `↑↓` 选节点 + `Enter` 节点详情（复用查看器）、`?` 帮助、短字段帧内输入（备注 N 仍走 ReadLine 以保 IME）。
- [x] **Step 5（验收）**：快照矩阵（80/100/140/200 列 × 有/无颜色）、`docs/screens/`、手动 checklist。

## 5. 验收

- 帧不变量（自动化）：每帧行数 ≤ 终端高度、每行显示宽度 ≤ 终端宽度、不发 `\u001b[H`、帧尾 `\u001b[J`。
- 快照矩阵（自动化）：同一视图在 80/100/140/200 列 × 有/无颜色下与 golden 文本一致。
- 手动 checklist：各视图在上述宽度下目视检查，配截图存档；不接受「跑一次没崩」。
- 回归：`--dump` / `--plain` 输出内容语义不变（布局可不同）。

## 6. 风险与回滚

- Spectre 标记语言里 `[` 与 `]` 必须 `Markup.Escape`，否则用户正文会把帧打崩 → 统一走 `Escape()` 帮助方法。
- East Asian Ambiguous（★ ⚠ → ·）：Windows Terminal 算 1 列，部分中文 conhost 算 2 列 → 当前按 Wcwidth 默认 1 列；若目标终端算 2 列，右缘会撕裂，后续可加 `--ambiguous-wide` 开关（未实现）。
- 每个 Step 独立可发布；`--no-alt-screen` / `--no-color` / `--width` / `--height` 可退回或固定旧行为做对照。

## 7. 实施记录（2026-10-19）

改动文件：

- `src/JieLi.Tui/Text/TextWidth.cs`：Wcwidth 驱动（字素簇、ANSI 感知、tab=4、`Wrap`）；
- `src/JieLi.Tui/Tui/ConsoleHost.cs`：相对光标回退 `ComposeFrame`（滚动安全）、`ClipLine`、备用屏进出、`--width/--height` 强制尺寸、非 TTY 纯文本；
- `src/JieLi.Tui/Tui/Rendering/{Theme,FrameBuilder,TableBuilder}.cs`：语义色板 + 能力降级、声明式帧（底部恒两行）、列优先级表；
- `src/JieLi.Tui/Tui/{TuiApp,ReviewView,ChainView}.cs`：迁移到 FrameBuilder/TableBuilder；Esc/Q 语义、`?` 帮助、`V` 全文查看器、链条节点选择 + `Enter` 详情、帧内输入、滚动与 `行 n/m` 指示；
- `src/JieLi.Tui/Program.cs`：`--no-color` / `--no-alt-screen` / `--ascii` / `--width` / `--height`，交互入口进备用屏；
- `tests/JieLi.Tui.Tests/`：63 个用例（宽度语义、帧不变量、布局、快照矩阵、备用屏开关、ASCII 降级）；
- `docs/screens/`：真实数据的帧落盘（review/gaps/chain/help/reader × 80/140 × plain/ansi16）。

### 7.1 2026-10-19 追加修复：按键后整帧错位/不刷新

用户实测「重设作者、重设棒数、查看证据链等任意操作后页面没有正确刷新」，截图里表现为
旧帧的键位行残留、切换面板后新帧画在旧帧下方。定位到两条叠加的根因：

1. **编辑走了 Console.ReadLine**：`EditField`（N 备注）与 `AddByLink`（A 补录）先
   `ConsoleHost.Restore()` —— 那会 `ESC[?1049l` 退出备用屏并放出光标 —— 之后只
   `InvalidateFrame()` 就继续渲染。后续帧既不在备用屏，又依赖「上一帧行数」的相对上移，
   锚点已经错了，于是整帧画偏/叠加。现在**所有可编辑字段都在帧内输入**，会话中途不再
   退出备用屏（`ReadLine` 路径整段删除，`EditField`/`HideCursor` 一并移除）。
2. **帧高会随视图变化**（列表面板 → 缺口/链条面板 → 查看器），相对上移只要差一行，
   后续所有帧就固定错一行。备用屏内改用**绝对归位** `ESC[H`（`ComposeFrameAlt`），
   不再依赖上一帧行数；同时进入备用屏时发 `ESC[?7l` 关闭自动换行、退出时 `ESC[?7h`，
   行宽差 1 列也不会把帧折成两行撑破视口。

回归网：`tests/JieLi.Tui.Tests/ReviewViewInputTests.cs` 用文本宿主跑完整
`Render → HandleKey → Render` 循环，锁住「改字段/忽略/撤销/开面板/查看器之后下一帧仍完整」、
「会话中从未出现 `ESC[?1049l` / `ESC[?25h`」、「输错不置脏、成功提交才置脏」、
「长输入显示尾部而不是头部」；`FrameModelTests` 增补 `ComposeFrameAlt` 与备用屏进出用例。
临时脚手架另跑 25 个连续按键 × 真实数据，逐帧断言「绝对归位、无相对上移、行宽行高不越界」。

已知缺口（有意留到下一轮）：

- 证据链面板的「缺口行」不可单独选中（选中单位是节点；缺口在节点详情里逐条展开）；
- Ambiguous 宽度固定按 1 列，无自动探测（用 `--ascii` 或换字体）；
- 鼠标未做（v1 明确不做）。

手动 checklist（真实终端，建议 Windows Terminal）：

| # | 场景 | 期望 |
|---|------|------|
| 1 | `jieli --review <slug>` 80×24，连按 Enter/X/↓/G/C | 底部说明不再重复打印，`C` 不再叠在旧帧上 |
| 2 | 窗口从 140 拖到 80 再拖回 | 每帧重排，无残行；帧高始终等于窗口高 |
| 3 | 按 `C` / `G` / `V` / `?` 后 Esc | 只退一层，列表状态不丢 |
| 4 | 按 `T`/`B`/`U`/`P`/`K`/`N`/`A` 输入并回车 | 全程帧内输入、无跳出重画；每帧完整、toast 是本次提交结果 |
| 5 | `--no-color` / `NO_COLOR=1` / `TERM=dumb` | 全无色，布局不变 |
| 6 | 输出重定向（`> out.txt`） | 纯文本、无 ANSI、无备用屏序列 |
| 7 | 退出后 | 正常回到原终端缓冲区（不进备用屏残留） |

## 7.2 残留叠字（2026-10-19 二次修复）

用户复测后仍报「输入删除后光标不清、操作提示不清、链条页叠加、保存提示不消失」。
根因不是备用屏/相对回退，而是 **整帧重绘只清了光标以下**：

- 帧尾的 `ESC[J`（ED）只从光标往下清屏，**光标以上的行**不会被清；
- 新帧某行比上一帧同一行短时（输入框退格、toast 换文案、表格列变窄），
  旧行尾巴就留在右边——表现就是叠字、`teS||||`、`● 未保存` 压住标题尾巴。

修法：

1. `ComposeFrame` / `ComposeFrameAlt` 每行收尾追加 `ESC[K`（EL，清到行尾），行间用 `CRLF`；
2. 提示（toast）只保留一帧：`ReviewView.HandleKey` 入口先清空 `_toast`；
3. 链条视图图例从表格上方移到页头（`BodyHeight` 自动收缩），表格多出 1 行；
4. 未保存标记缩短为 `● 未保存（S）`，不再挤掉目录名。

回归护栏：`tests/JieLi.Tui.Tests/ScreenSimulationTests.cs` 用一个**虚拟屏幕**
（解析 `H/A/K/J` 与换行、宽字符占两格）回放每一帧，断言「屏幕逐行等于最后一帧」；
另有反向对照 `WithoutPerLineEl_TailsRemain` 证明该断言确实能抓到残留。
