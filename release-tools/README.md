# 本地构建与组包

本仓库提供开发源码。模型、Rime 运行库与词典、llama.cpp CPU 二进制和头文件需要按许可另行取得；详见 `../licenses/redistribution/` 的来源与版本记录。

## 翻译运行时

`stage-translation-runtime.ps1` 校验固定模型与 llama.cpp CPU 包，再编译 `../translation-native` 的私有标准输入输出宿主。脚本参数与默认路径见其 `param` 块。对应输入放在 `vendor-build/translation-models`、`vendor-build/llama-b11580-cpu.zip` 和 `vendor-build/llama-b11580-sdk`。

## 完整输入法

先按 `../native-cpp/README.md` 构建 TSF 组件，并按 `../rime/README.md` 准备 Rime 运行文件。`protect-build.ps1` 负责编译与组包；本地验证构建请传 `-SkipSmoke`，避免启动桌面输入法。Python worker 的源码或短语库改变时，必须重新编译，不能复用不匹配的旧二进制。

完整安装包由 `../setup/assemble-release.ps1` 组装，`verify-package.ps1` 与 `audit-release.py` 可检查文件清单和发布内容，再用 `package-release.ps1` 归档。需要的第三方构建工具、缓存和已生成文件均未纳入源码仓库。

托管层构建和规则测试见仓库首页；完整打包依赖齐备后才可执行。构建成功不代表输入法已经在所有应用上完成使用验证。
