#pragma once
#include <windows.h>
#include <atomic>
#include <filesystem>
#include <string>
#include <vector>

#pragma comment(lib, "advapi32.lib")
#pragma comment(lib, "user32.lib")

namespace ServiceStartup
{
struct SelectionWake
{
    bool selected = false;
    bool Change(bool ours, bool activated)
    {
        if (!ours && !activated) return false;
        const bool next = ours && activated;
        const bool wake = next && !selected;
        selected = next;
        return wake;
    }
};

struct Handle
{
    HANDLE value = nullptr;
    ~Handle() { if (value && value != INVALID_HANDLE_VALUE) CloseHandle(value); }
};

inline bool IsRunning(const wchar_t* name)
{
    Handle mutex{OpenMutexW(SYNCHRONIZE, FALSE, name)};
    return mutex.value != nullptr;
}

inline std::filesystem::path FindInstalledBackend(std::filesystem::path directory)
{
    std::error_code error;
    for (int level = 0; level < 6 && !directory.empty(); ++level)
    {
        auto executable = directory / L"app" / L"ClaudeZhInput.exe";
        if (std::filesystem::is_regular_file(directory / L".claude-zh-input.install.json", error) &&
            std::filesystem::is_regular_file(executable, error)) return executable;
        auto parent = directory.parent_path();
        if (parent == directory) break;
        directory = parent;
    }
    return {};
}

inline bool SameUser(HANDLE left, HANDLE right)
{
    DWORD leftSize = 0, rightSize = 0;
    GetTokenInformation(left, TokenUser, nullptr, 0, &leftSize);
    GetTokenInformation(right, TokenUser, nullptr, 0, &rightSize);
    if (!leftSize || !rightSize) return false;
    std::vector<BYTE> a(leftSize), b(rightSize);
    return GetTokenInformation(left, TokenUser, a.data(), leftSize, &leftSize) &&
        GetTokenInformation(right, TokenUser, b.data(), rightSize, &rightSize) &&
        EqualSid(reinterpret_cast<TOKEN_USER*>(a.data())->User.Sid,
                 reinterpret_cast<TOKEN_USER*>(b.data())->User.Sid);
}

inline bool CreateHiddenUserProcess(const std::filesystem::path& executable,
                                    const std::wstring& arguments, PROCESS_INFORMATION& process)
{
    std::wstring command = L"\"" + executable.wstring() + L"\" " + arguments;
    auto workingDirectory = executable.parent_path().parent_path().wstring();
    STARTUPINFOW startup{sizeof(startup)};
    startup.dwFlags = STARTF_USESHOWWINDOW;
    startup.wShowWindow = SW_HIDE;
    Handle current;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &current.value)) return false;
    TOKEN_ELEVATION elevation{};
    DWORD size = 0;
    if (!GetTokenInformation(current.value, TokenElevation, &elevation, sizeof(elevation), &size)) return false;
    if (!elevation.TokenIsElevated)
        return CreateProcessW(executable.c_str(), command.data(), nullptr, nullptr, FALSE,
            CREATE_NO_WINDOW, nullptr, workingDirectory.c_str(), &startup, &process) != FALSE;

    // Use this user's desktop process as parent so the child inherits its normal token.
    DWORD shellPid = 0;
    GetWindowThreadProcessId(GetShellWindow(), &shellPid);
    Handle shell{OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_CREATE_PROCESS, FALSE, shellPid)};
    Handle shellToken;
    TOKEN_ELEVATION shellElevation{};
    if (!shell.value || !OpenProcessToken(shell.value, TOKEN_QUERY, &shellToken.value) ||
        !SameUser(current.value, shellToken.value) ||
        !GetTokenInformation(shellToken.value, TokenElevation, &shellElevation, sizeof(shellElevation), &size) ||
        shellElevation.TokenIsElevated) return false;
    SIZE_T attributeBytes = 0;
    InitializeProcThreadAttributeList(nullptr, 1, 0, &attributeBytes);
    std::vector<BYTE> storage(attributeBytes);
    auto attributes = reinterpret_cast<LPPROC_THREAD_ATTRIBUTE_LIST>(storage.data());
    if (!InitializeProcThreadAttributeList(attributes, 1, 0, &attributeBytes)) return false;
    struct DeleteAttributes { LPPROC_THREAD_ATTRIBUTE_LIST value; ~DeleteAttributes() { DeleteProcThreadAttributeList(value); } } cleanup{attributes};
    if (!UpdateProcThreadAttribute(attributes, 0, PROC_THREAD_ATTRIBUTE_PARENT_PROCESS,
            &shell.value, sizeof(shell.value), nullptr, nullptr)) return false;
    STARTUPINFOEXW extended{};
    extended.StartupInfo = startup;
    extended.StartupInfo.cb = sizeof(extended);
    extended.lpAttributeList = attributes;
    return CreateProcessW(executable.c_str(), command.data(), nullptr, nullptr, FALSE,
        CREATE_NO_WINDOW | EXTENDED_STARTUPINFO_PRESENT, nullptr, workingDirectory.c_str(),
        &extended.StartupInfo, &process) != FALSE;
}

enum class Result { Running, Started, Busy, Missing, Failed, Cancelled };

inline Result TryStart(const std::filesystem::path& executable, const std::atomic<bool>& stop,
                       const wchar_t* serviceMutex = L"Local\\ClaudeZhInput.UiService",
                       const wchar_t* launchMutex = L"Local\\ClaudeZhInput.ServiceLaunch",
                       const std::wstring& arguments = L"--ime-service --auto-start")
{
    if (stop) return Result::Cancelled;
    if (IsRunning(serviceMutex)) return Result::Running;
    std::error_code error;
    if (executable.empty() || !std::filesystem::is_regular_file(executable, error)) return Result::Missing;
    Handle gate{CreateMutexW(nullptr, FALSE, launchMutex)};
    if (!gate.value) return Result::Failed;
    DWORD locked = WaitForSingleObject(gate.value, 0);
    if (locked != WAIT_OBJECT_0 && locked != WAIT_ABANDONED) return Result::Busy;
    struct Unlock { HANDLE value; ~Unlock() { ReleaseMutex(value); } } unlock{gate.value};
    if (stop) return Result::Cancelled;
    if (IsRunning(serviceMutex)) return Result::Running;
    PROCESS_INFORMATION process{};
    if (!CreateHiddenUserProcess(executable, arguments, process)) return Result::Failed;
    Handle child{process.hProcess}, thread{process.hThread};
    // Keep simultaneous hosts from starting duplicates while .NET initializes.
    // This runs on the pipe thread and cancellation bounds TSF deactivation to 50 ms.
    const auto deadline = GetTickCount64() + 4000;
    while (!stop && !IsRunning(serviceMutex) && GetTickCount64() < deadline)
        if (WaitForSingleObject(child.value, 50) == WAIT_OBJECT_0) break;
    return Result::Started;
}
}
