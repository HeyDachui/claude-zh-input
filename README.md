# Claude 中文输入法

**输入中文，整句输出英文。** 一个在 Windows 本地运行的中译英输入法，面向日常英文对话和 Claude Code 使用场景。

中文拼音输入、整句英文模式、常用词句库和本地翻译模型组合使用。命中常用表达时直接返回英文；新的句子交给本地模型，译文写回正在输入的位置。

## 能做什么

- 普通中文 / 整句英文两种模式，中英混输
- 常用词句优先、严格句型匹配，未命中时使用本地模型
- Claude Code 常用指令检索，选中后回填输入框
- 纸笺、夜航、云朵三套皮肤
- 本地运行，提供安装、退出、卸载与回滚入口

## 工作原理

`拼音输入 → Rime 候选 → 中文组合文本 → 词句库 / 严格句型 → 本地模型兜底 → 英文上屏`

输入层使用 Windows TSF 与 Rime，主界面为 .NET 8 WPF。自由句翻译使用 Qwen3.5-4B 的 Q5_K_M GGUF 与 llama.cpp CPU 运行时，无需显卡或云端 API。

## 源码目录

| 目录 | 内容 |
| --- | --- |
| `src/` | WPF 界面、组合文本、词句路由、语包与设置 |
| `native-cpp/` | Windows TSF 原生输入组件 |
| `rime/` | Rime C# 互操作与会话 |
| `commands/` | Claude Code 指令库与选择面板 |
| `translation-native/` | llama.cpp 私有标准输入输出宿主 |
| `worker.py`、`translation_text.py` | 翻译调度与代码、路径、数字保护 |
| `setup/`、`release-tools/` | 安装维护、依赖准备与打包脚本 |
| `tests/` | 词句路由、模板与翻译契约测试 |
| `licenses/` | 第三方许可及依赖来源 |

## 构建与试用

当前公开的是 **1.4.4 源码快照**。仓库不附约 3.1 GB 的模型、第三方运行库、个人词库、聊天语料或本机使用记录。

构建环境：Windows x64、PowerShell 7、.NET 8 SDK、Visual Studio 2022 C++ Build Tools（v143 / Windows 11 SDK）、Python 3.11。

```powershell
dotnet build .\src\ClaudeZhInput.csproj -c Release
dotnet run --project .\tests\CoreTests.csproj -c Release
python -m unittest discover -s tests -p "test_phrase_*.py"
python -m unittest discover -s tests -p "test_translation_contract.py"
```

上面的构建和测试只验证托管源码与不依赖模型的规则。`test_translation.py` 是实际模型集成测试，需要先备齐翻译宿主和模型。完整输入法还需要原生 TSF DLL、Rime 运行库和词典、翻译宿主、模型及安装组包。

模型和 llama.cpp 固定来源、版本与 SHA-256 见 [`TRANSLATION-MODEL-NOTICE.txt`](licenses/redistribution/TRANSLATION-MODEL-NOTICE.txt)。Rime 依赖说明见 [`rime/README.md`](rime/README.md)，原生组件构建见 [`native-cpp/README.md`](native-cpp/README.md)，完整组包见 [`release-tools/README.md`](release-tools/README.md) 与 [`setup/README.md`](setup/README.md)。

建议先在无敏感内容的临时文档试用。模型首次加载可能需要等待；翻译结果受句子与设备影响。详细操作见 [`使用说明.txt`](使用说明.txt)。

## 版本说明

1.4.4 加入严格句型和程序 / 模型分包；原生输入组件版本为 1.4.0。源码快照与本机已安装版本可能不同，仓库不代表已在所有应用或设备上完成使用验证。

项目源代码的单独许可尚未指定；第三方组件遵循各自原有许可，见 `licenses/` 与相应文件头。

由 [HeyDachui](https://github.com/HeyDachui) 分享
