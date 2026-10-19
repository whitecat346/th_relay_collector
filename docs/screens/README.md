# 屏幕样本（渲染落盘）

这里的文件不是截图，而是**把真实数据渲染出来的完整一帧原样落盘**：

- `*-plain.txt`：无颜色输出（`Theme.None`），可直接 `cat` 或用 git diff 比结构；
- `*-ansi16.ansi`：16 色 ANSI 输出，`cat` 会带颜色；用支持 ANSI 的终端打开最直观。
- `*-ascii.txt`：`--ascii` 降级输出（★ ⚠ → ← ↔ · … ─ 全变 ASCII），用于字体缺字或 ambiguous 宽度不一致的终端。

生成方式（临时脚手架渲染 `src/JieLi.Tui` 的 `ReviewView`）：

```bash
# 参数：view 宽度 高度 [plain|ansi]
dotnet run --no-build -- review 80 24 plain
```

覆盖的视图 × 尺寸：`review / gaps / chain / help / reader` × `80×24`、`140×40` × `plain / ansi16`。

另外 `review-80-ascii.txt` 由真实 CLI 生成（不需要脚手架）：

```bash
dotnet run --project src/JieLi.Tui -- --review "2026_8月15日魅魔日接力1.5_08-15" --plain --width 80 --ascii
```

## 为什么不是 PNG

终端截图依赖字体、DPI、终端主题，且本机没有可靠的自动截图通道。更有用的证据是：

1. 上方这些**逐字节可 diff** 的帧样本；
2. `tests/JieLi.Tui.Tests/Snapshots/` 里的合成内容黄金快照（80/100/140/200 列 × 无颜色/16 色），CI 能直接卡住布局回归；
3. 真实终端里的手动 checklist（见 `docs/UI-UX-PLAN.md` 末尾）。

如果要补 PNG，请在 Windows Terminal 里按 checklist 跑一遍并截取对应画面，命名 `<view>-<width>-<theme>.png` 放本目录。
