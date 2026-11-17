# ClaudeZhInput.Rime

这是一个独立的 .NET 8 Windows x64 librime 服务层，不接触 `src/`、`native/`、`setup/` 或 `dist/`。它加载 `runtime/rime.dll`，由一个进程级 `RimeEngine` 管理一次全局初始化，并允许多个 `RimeSession` 并行存在；所有 librime 调用在 engine 锁内串行化。

## 构建与使用

```powershell
dotnet build .\rime\ClaudeZhInput.Rime.csproj -c Release
```

项目会把 `runtime/` 复制到输出目录。调用方将输出目录中的 `runtime` 作为 `RuntimeDirectory`，为每个可编辑上下文创建一个 session：

```csharp
using ClaudeZhInput.Rime;

using var engine = RimeEngine.Open(new RimeEngineOptions
{
    RuntimeDirectory = Path.Combine(AppContext.BaseDirectory, "runtime"),
    UserDataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClaudeZhInput", "rime"),
});
using var session = engine.CreateSession();

RimeSessionState state = session.ProcessKey(rimeKeycode, rimeModifiers);
// state.Commit -> one copied UTF-16 commit string
// state.Preedit/CaretUtf16 -> TSF composition
// state.Candidates/PageNo/PageSize/PageCount/IsLastPage -> candidate UI
// state.IsAsciiMode -> current ascii_mode option
```

`SelectCandidate(index)`、`ChangePage(backward)`、`ClearComposition()`、
`CommitComposition()` 和 `SetOption("ascii_mode", value)` 都返回新的完整状态。`Commit` 已在 native context 释放前复制；调用方不需要保存任何 librime 指针。

`RimeSessionState.CaretUtf16`、`SelectionStartUtf16` 和 `SelectionEndUtf16` 将 Rime 的 UTF-8 composition offset 转成 UTF-16 code-unit offset，正好对应 .NET/TSF 字符串索引。`PageCount` 是 nullable：在 Rime 报告最后一页前为 `null`，最后一页时才可由 `PageNo + 1` 得到精确页数。

官方 Windows DLL 没有导出可依赖的 `RimeGetKeycodeByName` helper；包装层按官方 `X11/keysym.h` 的值映射 ASCII、退格、回车、方向键和翻页键，modifier 使用官方 `key_table.h` 位值。Windows VK 到这些值的转换仍由上层 TSF 适配器负责。

## 数据与许可

`runtime/data/build/` 是用官方 `rime_deployer.exe --compile` 预生成的只读数据，因此按键回调不会部署或写入包目录。`runtime/data/claude_pinyin.schema.yaml` 删除了上游 pinyin-simp 的 `stroke`、反查和外部 preset 依赖；`pinyin_simp.dict.yaml` 保持上游文件。`runtime/licenses/` 保存 librime、pinyin-simp、Boost、glog、LevelDB、marisa-trie、OpenCC、yaml-cpp 和 darts-clone 声明。

`rime.dll` 和词典属于第三方，保持原样，不参与项目自有代码混淆。自有 .NET wrapper 可以由父任务按现有保护流程单独处理。

## 已完成的离线验证

独立 smoke 项目位于 `artifacts/reference-rime/rime-smoke/`，结果保存在 `artifacts/reference-rime/rime-smoke.json`。验证覆盖：

- `nihao` 产生 `你好` 候选，空格提交 `你好`；
- 连续 `nihaozhongwen` 后空格提交 `你好中文`；
- 两个 session 同时存在且组合状态独立；
- 直接 `CommitComposition` 和 `ClearComposition`；
- `shi` 的 9 候选分页，`PageNo` 从 0 到 1，第二页选词提交 `室`；
- librime 版本 `1.17.0`、UTF-16 caret 和 `IsAsciiMode` 状态正常。

