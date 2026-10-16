#nullable enable
using System.Text;
using JieLi.Tui.Data;
using JieLi.Tui.Tui;

namespace JieLi.Tui;

public static class Program
{
    public static int Main(string[] args)
    {
        try { Console.OutputEncoding = new UTF8Encoding(false); } catch { /* 重定向时可能不支持 */ }

        string? root = null;
        string? jumpSlug = null;
        bool forcePlain = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--root":
                    if (i + 1 < args.Length) root = args[++i];
                    break;
                case "--slug":
                    if (i + 1 < args.Length) jumpSlug = args[++i];
                    break;
                case "--plain":
                case "--dump":
                    forcePlain = true;
                    break;
                case "-h":
                case "--help":
                    PrintHelp();
                    return 0;
            }
        }

        root ??= FindWorkspaceRoot();
        if (root is null || !Directory.Exists(root))
        {
            Console.Error.WriteLine("找不到工作目录。请用 --root <路径> 显式指定。");
            return 2;
        }

        var scanner = new Scanner(root);
        var activities = scanner.ScanAll();

        var host = new ConsoleHost(forcePlain);
        var app = new TuiApp(host, activities, root);
        return app.Run(jumpSlug);
    }

    /// <summary>从 exe 位置向上找到含活动目录的工作区根（以 ai_guild.md 或 relay-tool 目录为标记）。</summary>
    private static string? FindWorkspaceRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir is not null; i++)
        {
            var parent = Directory.GetParent(dir)?.FullName;
            if (parent is null) break;
            if (File.Exists(Path.Combine(parent, "ai_guild.md"))) return parent;
            if (Directory.Exists(Path.Combine(parent, "relay-tool")) &&
                !string.Equals(Path.GetFileName(parent), "relay-tool", StringComparison.OrdinalIgnoreCase))
                return parent;
            dir = parent;
        }
        var cwd = Directory.GetCurrentDirectory();
        return Directory.Exists(cwd) ? cwd : null;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("jieli — 东方Project接力归档工具（M1 只读审阅器）");
        Console.WriteLine();
        Console.WriteLine("用法:");
        Console.WriteLine("  jieli [--root <工作区路径>] [--slug <活动目录名>] [--plain]");
        Console.WriteLine();
        Console.WriteLine("选项:");
        Console.WriteLine("  --root   工作区根目录（含各活动归档目录）。默认自动向上查找 ai_guild.md。");
        Console.WriteLine("  --slug   直接进入指定活动的棒链表视图。");
        Console.WriteLine("  --plain  纯文本模式（不重绘、不读键盘），用于脚本化验收与管道输出。");
        Console.WriteLine();
        Console.WriteLine("按键（交互模式）:");
        Console.WriteLine("  ↑/↓ 选择    Enter 进入棒表    PgUp/PgDn 翻页    Home/End 首尾");
        Console.WriteLine("  Esc 返回    Q 退出");
    }
}
