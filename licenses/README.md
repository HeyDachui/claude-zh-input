# 分享包许可文件

保护构建会把运行时实际使用的第三方许可复制到分享包的 `licenses/` 目录，并生成版本清单。这里不把公开模型或第三方组件宣称为本项目私有实现。

当前主模型是 Qwen3.5-4B 的 Unsloth GGUF 量化文件，按 Apache-2.0 原样分发。具体量化类型、固定版本和摘要写入 `redistribution/TRANSLATION-MODEL-NOTICE.txt` 与 `BUILD-INFO.json`。CPU 推理运行库 llama.cpp 使用 MIT 许可，LLVM OpenMP 保留其原许可。1.3.1 起不再打包旧 Argos/CTranslate2/NumPy/SentencePiece 运行资源。

`phrases.dat` 是本项目默认短语的运行时封装，不包含用户个人短语；用户短语仍保存于本机应用数据目录，不进入构建输入或分享包。

Rime 输入引擎是独立的第三方运行时：官方 librime 1.17.0 Windows MSVC x64 `rime.dll` 保持原样，许可证与静态依赖声明见本目录的 `librime-BSD-3-Clause.txt`、`THIRD-PARTY-NOTICES.md` 及对应组件文件；`rime-pinyin-simp` 词典按 Apache-2.0 分发。保护构建只混淆本项目的 `ClaudeZhInput.Rime` 管理封装，并保留其公开状态契约和 native interop 布局，不把 librime、词典或其依赖宣称为本项目私有实现。
