# Native C++ TSF candidate

This directory is an isolated native TIP candidate. It does not replace the
managed implementation in `native/` and the build does not register a profile.

The fixed test identity is:

- CLSID `{B763D5F3-0F9E-47D2-8CD8-7CCBFA6C3A41}`
- language profile `{56E8B5B8-99E6-4C0B-AF16-33C5805FC3D2}`
- display name `Claude 中文输入法（Rime 测试）`

The installed 1.1.2 package uses this native input layer with the formal
`ClaudeZhInput` pipe. Selecting its language profile requests a hidden user-level
backend start when needed. Closing the assistant leaves it closed; switching to
another profile and back requests a new start. There is no polling watchdog.
Isolated pipe names never start the installed backend. Existing hosts may cache
an older DLL until they are reopened.

The activation callback uses Microsoft's
[active profile notification](https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itfactivelanguageprofilenotifysink-onactivated).
Launches from elevated hosts use the same user's non-elevated desktop process as
the parent, following the documented
[parent process attribute](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-updateprocthreadattribute).
`tests/ServiceStartupTests.cpp` tests selection transitions, process reuse,
explicit close/reselect, elevation handling, and the real PipeClient's hello-before-key order
without changing the desktop's active input profile.

Build from a Developer PowerShell or from the installed Build Tools:

```powershell
& .\build-native-candidate.ps1
```

The output is `bin\x64\Release\ClaudeZhInput.NativeCandidate.dll`. To test it,
the owner must explicitly run `Register-NativeCandidate.ps1 -DllPath ...`
from an elevated PowerShell. Registration follows the Microsoft desktop
SampleIME layout: the candidate's COM key is written under HKLM and only its
own TSF profile/category is added. The script never changes the default
profile. Unregister with the same elevated script and `-Unregister`.

The DLL connects as a v2 Rime client to the named pipe
`ClaudeZhInput.NativeCandidate`. Put a one-line file named
`ClaudeZhInput.NativeCandidate.pipe` beside the DLL to select a different
BridgeServer pipe, or set `CLAUDE_ZH_NATIVE_PIPE` before the TSF is activated.
The service sends `hello`, `engine-key`, `engine-page`, `engine-select`,
`engine-cancel`, and `commands` messages and consumes `engine-state`,
`set-mode`, `command`, and `translated` messages. The state queue is ordered;
an old state cannot overwrite an already committed later state, while a stale
translation is rejected by sequence/request matching.

The TSF wake window is message-only, with class
`ClaudeZhInput.NativeCandidate.Wake.<pid>.<serial>` and title
`ClaudeZhInput NativeCandidate wake`. `WM_APP+0x51` returns `1` only when
`ITfKeystrokeMgr::GetForeground` is this CLSID. `WM_APP+0x52` wakes pending
engine states, `WM_APP+0x53` selects a visible candidate index, and
`WM_APP+0x54` applies a command-picker fill. No input text is logged.

For a focused activation diagnosis only, create a UTF-8 text file named
`ClaudeZhInput.NativeCandidate.diagnostics` beside the DLL containing `1`.
The resulting `.diagnostics.log` contains only numeric key codes, focus flags,
and compartment flags; diagnostics are disabled when the flag file is absent.

The non-registering COM smoke check is:

```powershell
& "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" `
  -NoProfile -ExecutionPolicy Bypass `
  -File .\diagnostics\ComFactorySmoke.ps1
```

The current x64 Release DLL was built with MSVC 14.44 and Windows SDK
10.0.26100.0. Its SHA-256 is
`CF71C48EBB7AFA10D519D3FDC6672E01BDCA0179C555A1804394C45840705BB9`.

`third_party/json.hpp` is nlohmann/json v3.11.3 under its MIT license; the
license text is kept next to the header.


## 1.1.4 异常路径与写入确认

`PipeClient` 只在明确的两秒启动窗口或引擎就绪后接收输入；握手先发，收到 `engineReady:true` 后才发送排队按键。出站队列最多 128 条，非阻塞管道避免后台停止读取时卡住输入线程。断线清空出站队列；主动关闭助手仍只在切回本输入法后唤醒。

原生层在键盘事件允许的同步写锁内保存文字预览，再处理异步引擎结果。Windows 排队成功不视为已上屏；回调重新核对 context、代次、焦点和禁用标记。最终写入成功才更新序号；较新的预览与已确认前缀一起写入，避免部分提交失败删掉后续文字。关闭旧组合只操作捕获的旧对象，不清除新组合。

输入框切换、输入法切换、断线及超时保留已有文字并重置后台。翻译期间的 Esc 使待写译文失效。后台额外返回 `translationStatus`，UI 区分带提醒的英文和实际中文回退。

依据：
- https://learn.microsoft.com/en-us/windows/win32/tsf/predefined-compartments
- https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itfcontext-requesteditsession
- https://learn.microsoft.com/en-us/windows/win32/api/ioapiset/nf-ioapiset-cancelsynchronousio

专项检查为 `tests/NativeRecoveryTests.cpp`（可控 TSF COM 故障）及工程根 `tests/RecoveryRegression`（真实管道、Rime、worker 和故障状态提示）。它们不操作桌面输入、系统焦点或正式输入服务。当前成品证据见根 CURRENT，不把可控上下文结果等同于全部真实宿主验收。
