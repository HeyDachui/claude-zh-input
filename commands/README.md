# Claude Code 常用指令库

本目录是给 WPF/IME 宿主消费的独立命令库，不改 `src/` 或 `worker.py`。`claude-commands.json` 保存经官方文档和本机 `claude.exe --help` 核对过的常用斜杠命令，`ClaudeCommandLibrary.cs` 提供 .NET 8 的加载、过滤、触发解析和用户目录发现。

## 宿主接入

将 `ClaudeCommandLibrary.cs` 加入 WPF 项目，或引用本目录的 `ClaudeZhInput.Commands.csproj`。加载 JSON 后，宿主在输入法的组合文本阶段调用：

```csharp
var library = ClaudeCommandLibrary.LoadFromJson(
    Path.Combine(appRoot, "commands", "claude-commands.json"),
    new CommandDiscoveryOptions { WorkingDirectory = Directory.GetCurrentDirectory() });

if (library.Trigger.TryGetQuery(compositionText, ctrlSemicolon, out var query))
{
    IReadOnlyList<ClaudeCommandSuggestion> candidates = library.Suggest(query, limit: 10);
    // 选中后只填入 candidates[i].FillText，例如 "/model"；不要在这里启动 claude.exe。
}
```

`Search` 返回 `IReadOnlyList<ClaudeCommandEntry>`；`Suggest` 返回带分数和匹配类型的候选列表。两者都接受中文、全拼、缩写、英文命令和别名，例如 `上下文`、`sxxw`、`ctx`、`/context`。规范填入文本也可用 `ClaudeCommandLibrary.FillOnly(entry)` 获取。

## WPF 命令选择面板

`wpf/ClaudeZhInput.Commands.Wpf.csproj` 是独立的 `net8.0-windows`、`UseWPF` 项目，引用上面的通用库，不把 WPF 类型带入通用项目。调用方可以直接取得填入文本：

```csharp
var picked = CommandPickerWindow.ShowDialog(owner, library, initialQuery: "上下文");
if (picked is not null)
{
    // picked 例如 "/context"；这里只把它填回输入框，不发送 Enter。
}
```

`CommandPickerWindow` 的上下键移动选中项，Enter 只设置 `PickedCommand` 并关闭窗口，Esc/取消返回 `null`。窗口默认暗色高对比，中文字体优先使用 Noto Sans SC；调用方可以传入 `ResourceDictionary` 或直接覆盖窗口资源中的 `CommandPickerBackgroundBrush`、`CommandPickerForegroundBrush`、`CommandPickerAccentBrush`、`CommandPickerCornerRadius` 等键，适配不同皮肤和圆角。

## 真实系统输入法宿主测试

`testhost/ClaudeZhInput.Commands.TestHost.csproj` 是只用于测试的 `net8.0-windows` WPF 可执行项目，不被产品项目引用。它只创建自己的窗口和一个多行 `TextBox`：启动后先用 Windows 的输入法切换键选择目标 TSF/IME，再在文本框中输入；每次 `TextChanged` 都会把完整文本、光标、选择区和当时的输入语言写入临时 JSON。默认路径为 `%TEMP%\ClaudeZhInput\testhost-<pid>.json`，也可用 `CLAUDE_ZH_INPUT_TEST_OUTPUT` 指定测试输出路径。窗口不连接外网、不调用 Claude、不注入用户已有窗口。

```powershell
dotnet run --project .\commands\testhost\ClaudeZhInput.Commands.TestHost.csproj
```

先在自己的测试窗口内切换到目标系统输入法，再输入中文并关闭窗口；读取 JSON 中的 `textChanges` 检查真实宿主收到的文本。`inputLanguageChanges` 记录测试期间的输入语言切换，便于确认测试时选中的系统输入法。

默认触发是 `;;` 和 `Ctrl+;`：`;;` 只有在当前组合文本开头出现时才打开命令菜单，所以正文中输入的分号不被拦截。触发配置标记了 `candidateOnly: true`；库不包含执行器，不启动进程、不发送回车、不写入 Claude 配置，也不调用收费 API。

## 用户自己的命令与 skills

传入 `CommandDiscoveryOptions` 后，库会在当前目录及祖先目录的 `.claude/commands/**/*.md`、用户目录 `.claude/commands/**/*.md`、对应 `.claude/skills/**/SKILL.md` 和 `AdditionalRoots` 中发现可用条目。发现器只读取 Markdown YAML frontmatter 的 `name` 与 `description`，并从路径生成命令名；不读取正文为新指令，不执行文件。用户条目会带 `IsUserDefined=true`、`SourcePath` 和 `SourceKind`，其实际行为标为未知，供宿主提示用户自行确认。

## 数据核对范围

JSON 的 `verification` 记录了本次核对时间、本机 Claude Code 版本 `2.1.292` 和帮助命令。官方斜杠命令以 [命令参考](https://code.claude.com/docs/zh-CN/commands) 为准，用户 skill/旧式 commands 的发现规则参考 [skills](https://code.claude.com/docs/zh-CN/skills)，CLI 启动选项和顶层子命令参考 [CLI 参考](https://code.claude.com/docs/zh-CN/cli-reference)。命令的 `changeEffect`、`impact`、`conditions` 是给输入法候选 UI 的告知信息，不代表宿主已经执行过命令。
