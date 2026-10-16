# relay-tool · 东方Project接力归档工具

半自动的接力归档流水线：**采集 → 解析交叉校验 → 人工确认 → 渲染归档**。
.NET 10 / Windows x64 / 零第三方依赖（NuGet 缓存里没有 Spectre.Console，TUI 用原生 ANSI 自绘）。

> 📖 **第一次用请先读 [`docs/USER-GUIDE.md`](docs/USER-GUIDE.md)** —— 从首次自动收集、TUI 纠错到保存归档的完整手册。
> 想知道"为什么这么设计 / 哪些做法已经试过并失败"请看 [`docs/CORPUS-FINDINGS.md`](docs/CORPUS-FINDINGS.md)。

## 为什么是"半自动"

`ai_guild.md` 的原始要求是"谨慎、反复验证"，而实测语料证明了这一点：

| 实测现象 | 数据 |
|---|---|
| 作品类型是自由词表而非枚举 | 683 行里出现 23 种写法（插画 436 / 插图 39 / 图片 29 混用） |
| 棒号/时段标注经常出错 | 有整条动态晚发 193 分钟；有 `第18棒 14:30` 却发布在 17:42 |
| 创作者常没被正确 @ 出来 | B站不渲染成 `<a>`，如 `第18棒 19:00 作者：条（咖方代发）` |
| 一条棒发两条动态 | 同人同棒号成对出现（作品条 + 说明条） |
| 棒数异常很常见 | 经多种方法仍无法确定棒号 → 按时间排序 + 标 `N/A`，交人工 |

因此工具**拒绝在未人工确认时产出最终归档**（可用 `--allow-unconfirmed` 显式跳过）。

## 命令

```powershell
# 需要网络。若在受限沙箱内，需放开对 Schannel 凭证目录的写入，
# 否则报 "安全包中没有可用的凭证"（SEC_E_NO_CREDENTIALS）。
jieli --pipeline <topic_id|话题链接>      # 采集 → 解析 → 写 work/<id>/review.jsonl
jieli --review   <topic_id>               # TUI 人工确认（唯一合法确认入口）
jieli --render   <topic_id>               # 出 CSV + 三段式 md + 只读报告
jieli --backfill [--apply] [--fix-header] # 旧归档零风险回填（默认干跑）

# 全部离线
jieli --parse  <topic_id>                 # 交叉校验与棒表（读已采集数据）
jieli --roster <topic_id>                 # 只抽"总结名册"（收集方法优先级 1）
jieli --selftest-review <topic_id>        # 用合成按键自检人工确认状态机
jieli                                     # 只读审阅已有归档（22 个目录）
```

常用开关：`--root <路径>`（默认自动向上查找 `ai_guild.md`）、`--out <目录>`（默认 `relay-tool/work`）、
`--archive-root <目录>`（渲染输出根）、`--allow-unconfirmed`、`--plain`（纯文本，便于脚本化验收）。

## 产物契约

严格复刻 `legacy/csv_md.py`（用户既有 Excel 与阅读习惯的基准）：

**CSV** `<slug>.csv` —— `棒数,时间,作者,地址,类型,地址`，**UTF-8 with BOM**（Windows 中文区 Excel 双击不乱码）
- 第 4 列 = 作者空间链接，第 6 列 = 作品链接（表头历史上都叫"地址"，沿用不改）
- 棒数异常的行（无法确定棒号）按时间排序**附加在末尾**，棒号列写 `N/A`

**Markdown** `<slug>.md` —— `## 宣传` → `## 整点接力` → `## ex棒`（+ 有异常时的 `## 棒数异常`）
每节：`### 第N棒 时段` / `创作者 [@名](链接)` / `类型 [查看](链接)`，以 `---` 分隔

**只读报告** `work/<id>/report.md` —— 顶部写死只读横幅；含警告、同棒多动态取舍、时段偏差、被忽略条目、逐棒数据来源

## 真值源与闸门

- `work/<id>/meta.yml` 是**宣传信息的唯一真值源**（主催 + 一宣/二宣/终宣/总结 + 可选显式棒位定义）。
  机器抓取结果绝不直接进产物；文件不存在时只生成带注释的草稿并拒绝渲染。
- `work/<id>/review.jsonl` 是采集与自动判定的候选记录，**人工确认只能在 TUI 里做**。
  它含原始正文（`merged_text`）便于核对，改完写回并立即重跑断言。
- `work/` 与 `publish/` 不进版本控制（`.gitignore`）。

## 解析能力（覆盖实测语料里的写法）

- 棒号：`第27棒` / `第 5棒` / `第六棒`（中文数字）/ `第02棒` / `第×棒`
- 时段：`第27棒 | 20:30` / `第10棒8：00` / `23:00点棒` / 标题 `【…24h·16：00】` / `HH:MM |@名字`
- 上下棒：`上一棒：@A` / `下一棒 |@B` / `前一棒@C`（含 `[にお/nio]` 这类包裹写法）
- 代发：`X（@Y 代发）`（只认紧跟上下棒人名之后，避免把"别人的代发"记到本条头上）、
  `第18棒 19:00 @C1cada_3301 （咖方代发）`、`本作为代发，画师：X`
- 异常归类：创作者未被正确 @ 出、标注了上下棒但没 @ 出对方、棒号无法确定

**名册反演**（对应 `ai_guild.md` 的"收集方法优先级 1"）：总结动态常一次性列出
`0:00 |@创作者` + 作品链接，是可靠性最高的数据源。实测 `topic 1348289` 的逐棒正文
既无棒号也无时段，只靠名册对齐才拿到 29/30 根棒。三级匹配：
① 作品链接 opus id ↔ 卡片 id ② 创作者昵称唯一命中 ③ 时段唯一命中；槽位唯一认领。

## 构建与发布

```powershell
# 开发构建（DOTNET_CLI_HOME 重定向到工作区，因为沙箱不允许写 ~/.templateengine）
$env:DOTNET_CLI_HOME = "D:\东方接力收集\relay-tool\.dotnet"
dotnet build relay-tool\src\JieLi.Tui\JieLi.Tui.csproj

# 自包含单文件发布 → relay-tool\publish\jieli.exe（约 70 MB，无需安装 .NET）
pwsh -File relay-tool\publish.ps1
```

## 目录

```
relay-tool/
├─ docs/CORPUS-FINDINGS.md  # ★ 实测语料规律与反模式（4 个真实话题 / 已失败的尝试 / 正则踩坑清单）
├─ src/JieLi.Tui/        # 源码
│   ├─ Text/             # 终端显示宽度（CJK/emoji 按 2 列对齐）
│   ├─ Model/            # 数据模型（BarRecord / TopicModels / ReviewItem）
│   ├─ Data/             # 采集 / 解析 / 校验 / 渲染 / 回填
│   └─ Tui/              # 无依赖 ANSI 渲染 + 只读审阅 + 人工确认
├─ legacy/csv_md.py      # 既有脚本，原样保留（render 不复用它，但产物契约对齐）
├─ fixtures/res/*.mhtml  # 两个真实页面样本，作解析器回归夹具
├─ work/                 # 过程产物（gitignore）：原始响应 / review.jsonl / meta.yml / report.md
└─ publish/              # 发布产物（gitignore）
```
