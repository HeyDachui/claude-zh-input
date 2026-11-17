#include "NativeCandidate.h"
#include "KeyRouting.h"
#include "ServiceStartup.h"

#include <windows.h>
#include <objbase.h>
#include <oleauto.h>
#include <shlwapi.h>
#include <strsafe.h>

#include "third_party/json.hpp"

#include <algorithm>
#include <filesystem>
#include <fstream>
#include <sstream>

#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "oleaut32.lib")
#pragma comment(lib, "user32.lib")
#pragma comment(lib, "advapi32.lib")
#pragma comment(lib, "shlwapi.lib")
#pragma comment(lib, "uuid.lib")

using nlohmann::json;

namespace
{
constexpr UINT WM_NATIVE_WAKE = WM_APP + 0x52;
constexpr UINT WM_NATIVE_SELECT = WM_APP + 0x53;
constexpr UINT WM_NATIVE_LOCAL_COMMIT = WM_APP + 0x54;
constexpr UINT WM_NATIVE_PROBE = WM_APP + 0x51;
constexpr UINT WM_NATIVE_SHOW_COMMANDS = WM_APP + 0x55;
constexpr UINT_PTR RETRY_TIMER = 1;
constexpr ULONGLONG RESPONSE_TIMEOUT_MS = 2000;
constexpr UINT TF_IAS_QUERYONLY_VALUE = 0x2;
constexpr DWORD CONTROL_MASK = 1u << 2;
constexpr DWORD SHIFT_MASK = 1u << 0;
constexpr DWORD CAPS_MASK = 1u << 1;
constexpr DWORD ALT_MASK = 1u << 3;

HINSTANCE g_instance = nullptr;
std::atomic<long> g_objects{0};
std::atomic<long> g_locks{0};
std::atomic<unsigned long> g_wakeSerial{0};
std::mutex g_diagnosticGate;

template <typename T>
void SafeRelease(T*& value)
{
    if (value != nullptr)
    {
        value->Release();
        value = nullptr;
    }
}

std::wstring Utf8ToWide(const std::string& value)
{
    if (value.empty()) return {};
    int length = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value.data(),
                                     static_cast<int>(value.size()), nullptr, 0);
    if (length <= 0)
    {
        length = MultiByteToWideChar(CP_UTF8, 0, value.data(),
                                     static_cast<int>(value.size()), nullptr, 0);
    }
    if (length <= 0) return {};
    std::wstring output(static_cast<size_t>(length), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, value.data(), static_cast<int>(value.size()),
                        output.data(), length);
    return output;
}

std::string WideToUtf8(const std::wstring& value)
{
    if (value.empty()) return {};
    int length = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value.data(),
                                     static_cast<int>(value.size()), nullptr, 0,
                                     nullptr, nullptr);
    if (length <= 0)
    {
        length = WideCharToMultiByte(CP_UTF8, 0, value.data(),
                                     static_cast<int>(value.size()), nullptr, 0,
                                     nullptr, nullptr);
    }
    if (length <= 0) return {};
    std::string output(static_cast<size_t>(length), '\0');
    WideCharToMultiByte(CP_UTF8, 0, value.data(), static_cast<int>(value.size()),
                        output.data(), length, nullptr, nullptr);
    return output;
}

std::string GuidText(REFGUID guid)
{
    wchar_t buffer[64]{};
    if (StringFromGUID2(guid, buffer, ARRAYSIZE(buffer)) == 0) return {};
    return WideToUtf8(buffer);
}

std::wstring GuidTextWide(REFGUID guid)
{
    wchar_t buffer[64]{};
    if (StringFromGUID2(guid, buffer, ARRAYSIZE(buffer)) == 0) return {};
    return buffer;
}

bool IsHexLikeGuid(const std::wstring& value)
{
    return value.size() == 38 && value.front() == L'{' && value.back() == L'}';
}

std::wstring Trim(std::wstring value)
{
    const auto isSpace = [](wchar_t c) { return c == L' ' || c == L'\t' || c == L'\r' || c == L'\n'; };
    while (!value.empty() && isSpace(value.front())) value.erase(value.begin());
    while (!value.empty() && isSpace(value.back())) value.pop_back();
    return value;
}

std::string TrimAscii(std::string value)
{
    while (!value.empty() && (value.front() == ' ' || value.front() == '\t' || value.front() == '\r' || value.front() == '\n'))
        value.erase(value.begin());
    while (!value.empty() && (value.back() == ' ' || value.back() == '\t' || value.back() == '\r' || value.back() == '\n'))
        value.pop_back();
    return value;
}

std::string StringValue(const json& object, const char* property, const std::string& fallback = {})
{
    auto it = object.find(property);
    return it != object.end() && it->is_string() ? it->get<std::string>() : fallback;
}

long long Int64Value(const json& object, const char* property, long long fallback = 0)
{
    auto it = object.find(property);
    if (it == object.end()) return fallback;
    if (it->is_number_integer()) return it->get<long long>();
    return fallback;
}

bool BoolValue(const json& object, const char* property, bool fallback = false)
{
    auto it = object.find(property);
    return it != object.end() && it->is_boolean() ? it->get<bool>() : fallback;
}

HRESULT SetRegString(HKEY root, const std::wstring& subKey, const wchar_t* valueName,
                     const std::wstring& value)
{
    HKEY key = nullptr;
    const LSTATUS createResult = RegCreateKeyExW(root, subKey.c_str(), 0, nullptr, 0,
                                                  KEY_WRITE, nullptr, &key, nullptr);
    if (createResult != ERROR_SUCCESS)
        return HRESULT_FROM_WIN32(createResult);
    const LSTATUS setResult = RegSetValueExW(key, valueName, 0, REG_SZ,
                                             reinterpret_cast<const BYTE*>(value.c_str()),
                                             static_cast<DWORD>((value.size() + 1) * sizeof(wchar_t)));
    RegCloseKey(key);
    return setResult == ERROR_SUCCESS ? S_OK : HRESULT_FROM_WIN32(setResult);
}

void DeleteRegTree(HKEY root, const std::wstring& subKey)
{
    RegDeleteTreeW(root, subKey.c_str());
}

bool IsSameUnknown(IUnknown* first, IUnknown* second)
{
    if (first == nullptr || second == nullptr) return false;
    IUnknown* firstUnknown = nullptr;
    IUnknown* secondUnknown = nullptr;
    bool same = false;
    if (SUCCEEDED(first->QueryInterface(IID_IUnknown, reinterpret_cast<void**>(&firstUnknown))) &&
        SUCCEEDED(second->QueryInterface(IID_IUnknown, reinterpret_cast<void**>(&secondUnknown))))
    {
        same = firstUnknown == secondUnknown;
    }
    SafeRelease(firstUnknown);
    SafeRelease(secondUnknown);
    return same;
}
}

// -----------------------------------------------------------------------------
// PipeClient
// -----------------------------------------------------------------------------

PipeClient::PipeClient(NativeCandidate* owner, std::string pipeName)
    : _owner(owner), _pipeName(std::move(pipeName))
{
}

PipeClient::~PipeClient()
{
    Stop();
}

void PipeClient::Start()
{
    if (_thread.joinable()) return;
    _stop = false;
    _thread = std::thread([this] { Run(); });
}

void PipeClient::Stop()
{
    _stop = true;
    _ready = false;
    _startingUntil = 0;
    _wake.notify_all();
    if (_thread.joinable()) CancelSynchronousIo(_thread.native_handle());
    if (_thread.joinable()) _thread.join();
    std::lock_guard lock(_gate);
    _outgoing.clear();
    _incoming.clear();
}

bool PipeClient::Send(std::string line)
{
    if (!AcceptingInput() || line.size() > 65536) return false;
    if (line.empty() || line.back() != '\n') line.push_back('\n');
    {
        std::lock_guard lock(_gate);
        if (_stop || !AcceptingInput() || _outgoing.size() >= 128) return false;
        _outgoing.push_back(std::move(line));
    }
    _wake.notify_one();
    return true;
}

void PipeClient::ResetConnection()
{
    _ready = false;
    _startingUntil = 0;
    _resetRequested = true;
    if (_thread.joinable()) CancelSynchronousIo(_thread.native_handle());
    _wake.notify_one();
}

void PipeClient::RequestServiceStart()
{
    if (_pipeName != "ClaudeZhInput") return; // Isolated test pipes never launch the installed service.
    _startingUntil = GetTickCount64() + RESPONSE_TIMEOUT_MS;
    _startRequested = true;
    _wake.notify_one();
}

void PipeClient::CancelServiceStart() { _startRequested = false; _startingUntil = 0; }

bool PipeClient::Connect()
{
    std::wstring path = L"\\\\.\\pipe\\" + Utf8ToWide(_pipeName);
    if (!WaitNamedPipeW(path.c_str(), 250)) return false;
    HANDLE pipe = CreateFileW(path.c_str(), GENERIC_READ | GENERIC_WRITE,
                              0, nullptr, OPEN_EXISTING, 0, nullptr);
    if (pipe == INVALID_HANDLE_VALUE) return false;
    DWORD mode = PIPE_READMODE_BYTE | PIPE_NOWAIT;
    if (!SetNamedPipeHandleState(pipe, &mode, nullptr, nullptr))
    {
        CloseHandle(pipe);
        return false;
    }
    {
        std::lock_guard lock(_gate);
        if (_stop)
        {
            CloseHandle(pipe);
            return false;
        }
        _pipe = pipe;
        _ready = false;
        // Only the handshake is sent before the engine reports readiness.
        _outgoing.push_front(_owner->HelloLine() + "\n");
        _helloPending = true;
    }
    _wake.notify_one();
    return true;
}

void PipeClient::Disconnect()
{
    std::lock_guard lock(_gate);
    _ready = false;
    if (_pipe != INVALID_HANDLE_VALUE)
    {
        CloseHandle(_pipe);
        _pipe = INVALID_HANDLE_VALUE;
        ++_disconnectVersion;
        if (_owner->_wakeWindow) PostMessageW(_owner->_wakeWindow, WM_NATIVE_WAKE, 0, 0);
    }
    _outgoing.clear(); // Never replay stale keystrokes into a new engine session.
    _helloPending = false;
    _incoming.clear();
}

bool PipeClient::WriteLine(const std::string& line)
{
    HANDLE pipe = INVALID_HANDLE_VALUE;
    {
        std::lock_guard lock(_gate);
        pipe = _pipe;
    }
    if (pipe == INVALID_HANDLE_VALUE) return false;
    size_t offset = 0;
    while (offset < line.size())
    {
        DWORD written = 0;
        const DWORD chunk = static_cast<DWORD>(std::min<size_t>(line.size() - offset, 64 * 1024));
        if (!WriteFile(pipe, line.data() + offset, chunk, &written, nullptr) || written == 0)
            return false;
        offset += written;
    }
    return true;
}

void PipeClient::ConsumeAvailable()
{
    HANDLE pipe = INVALID_HANDLE_VALUE;
    {
        std::lock_guard lock(_gate);
        pipe = _pipe;
    }
    if (pipe == INVALID_HANDLE_VALUE) return;

    DWORD available = 0;
    if (!PeekNamedPipe(pipe, nullptr, 0, nullptr, &available, nullptr))
    {
        Disconnect();
        return;
    }
    while (available > 0)
    {
        char buffer[4096];
        DWORD read = 0;
        DWORD request = std::min<DWORD>(available, sizeof(buffer));
        if (!ReadFile(pipe, buffer, request, &read, nullptr) || read == 0)
        {
            Disconnect();
            return;
        }
        _incoming.append(buffer, buffer + read);
        size_t newline = 0;
        while ((newline = _incoming.find('\n')) != std::string::npos)
        {
            std::string line = _incoming.substr(0, newline);
            _incoming.erase(0, newline + 1);
            if (line.size() <= 65536)
            {
                try
                {
                    const auto message = json::parse(line);
                    if (!_resetRequested && !_stop && StringValue(message, "type") == "engine-state" &&
                        StringValue(message, "sessionId") == _owner->_sessionId)
                    {
                        _ready = BoolValue(message, "engineReady");
                        _startingUntil = 0;
                    }
                }
                catch (const json::exception&) { }
                _owner->OnPipeLine(line);
            }
        }
        if (_incoming.size() > 65536) { Disconnect(); return; }
        available = 0;
        if (!PeekNamedPipe(pipe, nullptr, 0, nullptr, &available, nullptr))
        {
            Disconnect();
            return;
        }
    }
}

void PipeClient::Run()
{
    while (!_stop)
    {
        if (_resetRequested.exchange(false)) Disconnect();
        bool connected = false;
        {
            std::lock_guard lock(_gate);
            connected = _pipe != INVALID_HANDLE_VALUE;
        }
        if (!connected)
        {
            const bool nowConnected = Connect();
            const bool requested = _startRequested.exchange(false);
            if (!nowConnected && requested && !_stop)
                ServiceStartup::TryStart(ServiceStartup::FindInstalledBackend(_owner->ModuleDirectory()), _stop);
            std::unique_lock lock(_gate);
            _wake.wait_for(lock, std::chrono::milliseconds(40), [this] { return _stop || _startRequested || _pipe != INVALID_HANDLE_VALUE; });
            continue;
        }
        _startRequested = false;

        std::string line;
        {
            std::lock_guard lock(_gate);
            if (!_outgoing.empty() && (_ready || _helloPending))
            {
                line = std::move(_outgoing.front());
                _outgoing.pop_front();
                _helloPending = false;
            }
        }
        if (!line.empty() && !WriteLine(line))
        {
            Disconnect();
            continue;
        }
        ConsumeAvailable();
        std::unique_lock lock(_gate);
        _wake.wait_for(lock, std::chrono::milliseconds(5), [this] { return _stop || _resetRequested || (_ready && !_outgoing.empty()) || _pipe == INVALID_HANDLE_VALUE; });
    }
    Disconnect();
}

// -----------------------------------------------------------------------------
// NativeCandidate edit session and COM implementation
// -----------------------------------------------------------------------------

class NativeCandidate::EditSession final : public ITfEditSession
{
public:
    EditSession(NativeCandidate* owner, ITfContext* context,
                std::function<HRESULT(TfEditCookie)> action)
        : _owner(owner), _context(context), _action(std::move(action))
    {
        if (_owner != nullptr) _owner->AddRef();
        if (_context != nullptr) _context->AddRef();
    }

    ~EditSession()
    {
        SafeRelease(_context);
        if (_owner != nullptr) _owner->Release();
    }

    STDMETHODIMP QueryInterface(REFIID riid, void** object) override
    {
        if (object == nullptr) return E_POINTER;
        *object = nullptr;
        if (riid == IID_IUnknown || riid == IID_ITfEditSession)
            *object = static_cast<ITfEditSession*>(this);
        if (*object == nullptr) return E_NOINTERFACE;
        AddRef();
        return S_OK;
    }

    STDMETHODIMP_(ULONG) AddRef() override { return ++_references; }

    STDMETHODIMP_(ULONG) Release() override
    {
        ULONG value = --_references;
        if (value == 0) delete this;
        return value;
    }

    STDMETHODIMP DoEditSession(TfEditCookie editCookie) override
    {
        if (!_action) return E_UNEXPECTED;
        return _action(editCookie);
    }

private:
    std::atomic<ULONG> _references{1};
    NativeCandidate* _owner;
    ITfContext* _context;
    std::function<HRESULT(TfEditCookie)> _action;
};


template <typename T>
static std::shared_ptr<T> RetainCom(T* object)
{
    if (object) object->AddRef();
    return std::shared_ptr<T>(object, [](T* value) { if (value) value->Release(); });
}

struct NativeCandidate::PendingApply
{
    EngineState state;
    std::shared_ptr<ITfContext> context;
    unsigned long long generation = 0;
    ULONGLONG created = GetTickCount64();
    bool inFlight = false;
    bool commitApplied = false;
    bool hostEditApplied = false;
    bool done = false;
    std::shared_ptr<ITfRange> remainder;
};

struct NativeCandidate::RetiredComposition
{
    std::shared_ptr<ITfContext> context;
    std::shared_ptr<ITfComposition> composition;
    bool inFlight = false;
    bool done = false;
};

class NativeCandidate::ClassFactory final : public IClassFactory
{
public:
    ClassFactory() { ++g_objects; }
    ~ClassFactory() { --g_objects; }

    STDMETHODIMP QueryInterface(REFIID riid, void** object) override
    {
        if (object == nullptr) return E_POINTER;
        *object = nullptr;
        if (riid == IID_IUnknown || riid == IID_IClassFactory)
            *object = static_cast<IClassFactory*>(this);
        if (*object == nullptr) return E_NOINTERFACE;
        AddRef();
        return S_OK;
    }

    STDMETHODIMP_(ULONG) AddRef() override { return ++_references; }

    STDMETHODIMP_(ULONG) Release() override
    {
        ULONG value = --_references;
        if (value == 0) delete this;
        return value;
    }

    STDMETHODIMP CreateInstance(IUnknown* outer, REFIID riid, void** object) override
    {
        if (object == nullptr) return E_POINTER;
        *object = nullptr;
        if (outer != nullptr) return CLASS_E_NOAGGREGATION;
        auto* service = new (std::nothrow) NativeCandidate();
        if (service == nullptr) return E_OUTOFMEMORY;
        HRESULT hr = service->QueryInterface(riid, object);
        service->Release();
        return hr;
    }

    STDMETHODIMP LockServer(BOOL lock) override
    {
        if (lock) ++g_locks;
        else --g_locks;
        return S_OK;
    }

private:
    std::atomic<ULONG> _references{1};
};

NativeCandidate::NativeCandidate()
{
    ++g_objects;
    const DWORD pid = GetCurrentProcessId();
    _sessionId = "native-candidate-" + std::to_string(pid) + "-" + std::to_string(GetTickCount64());
}

NativeCandidate::~NativeCandidate()
{
    Deactivate();
    --g_objects;
}

STDMETHODIMP NativeCandidate::QueryInterface(REFIID riid, void** object)
{
    if (object == nullptr) return E_POINTER;
    *object = nullptr;
    if (riid == IID_IUnknown || riid == IID_ITfTextInputProcessor)
        *object = static_cast<ITfTextInputProcessor*>(this);
    else if (riid == IID_ITfTextInputProcessorEx)
        *object = static_cast<ITfTextInputProcessorEx*>(this);
    else if (riid == IID_ITfKeyEventSink)
        *object = static_cast<ITfKeyEventSink*>(this);
    else if (riid == IID_ITfCompositionSink)
        *object = static_cast<ITfCompositionSink*>(this);
    else if (riid == IID_ITfActiveLanguageProfileNotifySink)
        *object = static_cast<ITfActiveLanguageProfileNotifySink*>(this);
    if (*object == nullptr) return E_NOINTERFACE;
    AddRef();
    return S_OK;
}

STDMETHODIMP_(ULONG) NativeCandidate::AddRef()
{
    return ++_references;
}

STDMETHODIMP_(ULONG) NativeCandidate::Release()
{
    ULONG value = --_references;
    if (value == 0) delete this;
    return value;
}

STDMETHODIMP NativeCandidate::Activate(ITfThreadMgr* threadMgr, TfClientId clientId)
{
    return ActivateOnThread(threadMgr, clientId, 0);
}

STDMETHODIMP NativeCandidate::ActivateEx(ITfThreadMgr* threadMgr, TfClientId clientId, DWORD flags)
{
    return ActivateOnThread(threadMgr, clientId, flags);
}

HRESULT NativeCandidate::ActivateOnThread(ITfThreadMgr* threadMgr, TfClientId clientId, DWORD flags)
{
    DiagnosticEvent("activate", flags, clientId, 0);
    if (threadMgr == nullptr) return E_INVALIDARG;
    if (_active) return S_OK;

    _threadMgr = threadMgr;
    _threadMgr->AddRef();
    _clientId = clientId;
    HRESULT hr = _threadMgr->QueryInterface(IID_PPV_ARGS(&_keystrokeMgr));
    if (FAILED(hr))
    {
        ReleaseContext();
        SafeRelease(_threadMgr);
        _clientId = TF_CLIENTID_NULL;
        return hr;
    }

    hr = _keystrokeMgr->AdviseKeyEventSink(_clientId, static_cast<ITfKeyEventSink*>(this), TRUE);
    if (FAILED(hr))
    {
        SafeRelease(_keystrokeMgr);
        SafeRelease(_threadMgr);
        _clientId = TF_CLIENTID_NULL;
        return hr;
    }

    _active = true;
    _focusLost = false;
    _logicalComposition = false;
    _asciiMode = false;
    _shiftDown = false;
    _shiftUsed = false;
    _commandPrefixPending = false;
    _commandActive = false;
    _commandText.clear();
    _commandRequestId.clear();
    _sequence = 0;
    _lastAppliedSequence = 0;
    _contextStartSequence = 0;
    _hasCaret = false;
    _lastAppliedRequest.clear();

    CreateWakeWindow();
    _pipeName = LoadPipeName();
    _pipe = std::make_unique<PipeClient>(this, _pipeName);
    _pipe->Start();
    if (SUCCEEDED(_threadMgr->QueryInterface(IID_PPV_ARGS(&_profileSource))))
        _profileSource->AdviseSink(IID_ITfActiveLanguageProfileNotifySink,
            static_cast<ITfActiveLanguageProfileNotifySink*>(this), &_profileCookie);
    CheckSelectedProfile();
    return S_OK;
}

void NativeCandidate::CheckSelectedProfile()
{
    ITfInputProcessorProfileMgr* manager = nullptr;
    if (SUCCEEDED(CoCreateInstance(CLSID_TF_InputProcessorProfiles, nullptr, CLSCTX_INPROC_SERVER,
                                  IID_PPV_ARGS(&manager))))
    {
        TF_INPUTPROCESSORPROFILE profile{};
        if (manager->GetActiveProfile(GUID_TFCAT_TIP_KEYBOARD, &profile) == S_OK)
            OnActivated(profile.clsid, profile.guidProfile, TRUE);
        manager->Release();
    }
}

STDMETHODIMP NativeCandidate::OnActivated(REFCLSID clsid, REFGUID profile, BOOL activated)
{
    const bool wasSelected = _profileSelection.selected;
    const bool ours = IsEqualCLSID(clsid, CLSID_NativeCandidate) && IsEqualGUID(profile, GUID_NativeCandidateProfile);
    if (_profileSelection.Change(ours, activated != FALSE) && _pipe) _pipe->RequestServiceStart();
    if (!_profileSelection.selected && _pipe)
    {
        _pipe->CancelServiceStart();
        if (wasSelected) AbandonInput(false);
    }
    return S_OK;
}

STDMETHODIMP NativeCandidate::Deactivate()
{
    if (!_active && _threadMgr == nullptr && _pipe == nullptr) return S_OK;
    PreserveComposition();
    _active = false;
    ++_contextGeneration;
    _pendingEdit.reset();
    _previewKeys.clear();
    _awaitingSince = 0;
    _shiftDown = _shiftUsed = false;
    _retiredCompositions.clear();
    _profileSelection.selected = false;
    if (_profileSource)
    {
        if (_profileCookie != TF_INVALID_COOKIE) _profileSource->UnadviseSink(_profileCookie);
        _profileCookie = TF_INVALID_COOKIE;
        SafeRelease(_profileSource);
    }
    {
        std::lock_guard lock(_stateGate);
        _pendingStates.clear();
        _commandFills.clear();
    }
    for (auto& commit : _localCommits) SafeRelease(commit.second);
    _localCommits.clear();

    if (_keystrokeMgr != nullptr)
    {
        _keystrokeMgr->UnadviseKeyEventSink(_clientId);
        SafeRelease(_keystrokeMgr);
    }
    if (_pipe != nullptr)
    {
        _pipe->Stop();
        _pipe.reset();
    }
    DestroyWakeWindow();
    ResetCompositionState();
    ReleaseContext();
    SafeRelease(_threadMgr);
    _clientId = TF_CLIENTID_NULL;
    _focusLost = false;
    _commandPrefixPending = false;
    _commandActive = false;
    _commandText.clear();
    _commandRequestId.clear();
    return S_OK;
}

void NativeCandidate::ResetCompositionState()
{
    SafeRelease(_composition);
    SafeRelease(_compositionContext);
    _logicalComposition = false;
    _displayedText.clear();
    _displayedCaret = 0;
}

void NativeCandidate::ReplaceContext(ITfContext* context)
{
    if (context == _context) { _focusLost = false; return; }
    if (_context != nullptr) AbandonInput(false);
    ++_contextGeneration;
    _hasCaret = false;
    if (context != nullptr) context->AddRef();
    SafeRelease(_context);
    _context = context;
    _focusLost = false;
}

void NativeCandidate::ReleaseContext()
{
    SafeRelease(_context);
}

std::wstring NativeCandidate::ModuleDirectory() const
{
    wchar_t path[MAX_PATH * 4]{};
    DWORD length = GetModuleFileNameW(g_instance, path, ARRAYSIZE(path));
    if (length == 0 || length >= ARRAYSIZE(path)) return L".";
    std::wstring full(path, length);
    const size_t slash = full.find_last_of(L"\\/");
    return slash == std::wstring::npos ? L"." : full.substr(0, slash);
}

std::string NativeCandidate::LoadPipeName() const
{
    wchar_t environment[256]{};
    DWORD length = GetEnvironmentVariableW(L"CLAUDE_ZH_NATIVE_PIPE", environment, ARRAYSIZE(environment));
    if (length > 0 && length < ARRAYSIZE(environment))
    {
        std::string value = TrimAscii(WideToUtf8(environment));
        if (!value.empty()) return value;
    }

    std::filesystem::path path(ModuleDirectory());
    path /= L"ClaudeZhInput.NativeCandidate.pipe";
    std::wifstream file(path);
    if (file)
    {
        std::wstring line;
        std::getline(file, line);
        line = Trim(std::move(line));
        std::string value = TrimAscii(WideToUtf8(line));
        if (!value.empty() && value.size() <= 200) return value;
    }
    return "ClaudeZhInput.NativeCandidate";
}

std::string NativeCandidate::HelloLine() const
{
    json hello = {
        {"type", "hello"},
        {"sessionId", _sessionId},
        {"processId", static_cast<int>(GetCurrentProcessId())},
        {"wakeWindow", reinterpret_cast<intptr_t>(_wakeWindow)},
        {"toolbarCommands", true},
        {"clientVersion", "1.4.0"},
        {"protocol", 2},
        {"engine", "rime"}
    };
    return hello.dump();
}

void NativeCandidate::OnPipeLine(const std::string& line)
{
    json message;
    try
    {
        message = json::parse(line);
    }
    catch (const json::exception&)
    {
        return;
    }

    const std::string type = StringValue(message, "type");
    const std::string op = StringValue(message, "op");
    if (op == "set-mode")
    {
        std::lock_guard lock(_stateGate);
        _mode = StringValue(message, "mode", "normal") == "claude" ? "claude" : "normal";
        return;
    }

    if (op == "show-commands")
    {
        if (_wakeWindow != nullptr) PostMessageW(_wakeWindow, WM_NATIVE_SHOW_COMMANDS, 0, 0);
        return;
    }

    if (type == "engine-state")
    {
        const std::string responseSession = StringValue(message, "sessionId");
        if (!responseSession.empty() && responseSession != _sessionId)
            return;
        _continuousSentences = BoolValue(message, "continuousSentences");
        auto state = std::make_unique<EngineState>();
        state->sequence = Int64Value(message, "sequence");
        state->requestId = StringValue(message, "requestId");
        state->handled = BoolValue(message, "handled");
        state->composition = Utf8ToWide(StringValue(message, "composition"));
        state->caret = static_cast<int>(Int64Value(message, "caret", state->composition.size()));
        state->commit = Utf8ToWide(StringValue(message, "commit"));
        state->commitId = StringValue(message, "commitId");
        state->mode = StringValue(message, "mode", "normal");
        if (state->mode != "claude") state->mode = "normal";
        state->pendingTranslation = BoolValue(message, "pendingTranslation");
        state->asciiMode = BoolValue(message, "asciiMode");
        state->selectedIndex = static_cast<int>(Int64Value(message, "selectedIndex"));
        state->page = static_cast<int>(Int64Value(message, "page"));
        state->pageCount = static_cast<int>(Int64Value(message, "pageCount"));
        state->isLastPage = BoolValue(message, "isLastPage", true);
        auto candidates = message.find("candidates");
        if (candidates != message.end() && candidates->is_array())
        {
            for (const auto& candidate : *candidates)
            {
                if (candidate.is_string()) state->candidates.push_back(Utf8ToWide(candidate.get<std::string>()));
            }
        }
        PostState(state.release());
        return;
    }

    // The UI may be attached to the service side of the same connection.  It
    // can send a visible-page index as op:select; the TIP converts it to the
    // v2 request after the message reaches the TSF thread.  A direct window
    // message is also supported by ReferenceTsfHost and has the same path.
    if (op == "select" || type == "engine-select")
    {
        const auto request = StringValue(message, "requestId");
        {
            std::lock_guard lock(_stateGate);
            if (!request.empty() && request != _lastAppliedRequest) return;
        }
        const int index = static_cast<int>(Int64Value(message, "index", -1));
        if (index >= 0 && _wakeWindow != nullptr)
            PostMessageW(_wakeWindow, WM_NATIVE_SELECT, static_cast<WPARAM>(index), 0);
        return;
    }

    // Command picker replies do not synthesize Enter. They only fill the
    // active edit context, and are accepted only for the currently pending
    // command request.
    if (op == "command")
    {
        const std::string request = StringValue(message, "requestId");
        const std::wstring text = Utf8ToWide(StringValue(message, "text"));
        std::lock_guard lock(_stateGate);
        if (_commandActive && request == _commandRequestId && _wakeWindow != nullptr)
        {
            _commandActive = false;
            _commandPrefixPending = false;
            _commandText.clear();
            _commandRequestId.clear();
            if (!text.empty())
            {
                _commandFills.emplace_back(text);
                PostMessageW(_wakeWindow, WM_NATIVE_LOCAL_COMMIT, 0, 0);
            }
        }
        return;
    }

    // Legacy translation responses are accepted as a compatibility bridge.
    // Protocol v2 normally returns the same information as engine-state with
    // commit set, so this branch is not needed for normal operation.
    if (op == "translated")
    {
        auto state = std::make_unique<EngineState>();
        state->sequence = Int64Value(message, "sequence", _sequence);
        state->requestId = StringValue(message, "requestId");
        state->handled = true;
        state->commit = Utf8ToWide(StringValue(message, "text"));
        state->mode = "claude";
        PostState(state.release());
    }
}

bool NativeCandidate::IsEnabledContext(ITfContext* context) const
{
    // Disabled/empty flags belong to this context, not the global manager.
    // A missing or unreadable context is left to the host.
    if (context == nullptr || _threadMgr == nullptr) return false;
    TS_STATUS status{};
    const HRESULT statusResult = context->GetStatus(&status);
    if (FAILED(statusResult) || (status.dwDynamicFlags & TS_SD_READONLY) != 0)
    {
        DiagnosticEvent("context", status.dwDynamicFlags, 1, 0);
        return false;
    }

    ITfCompartmentMgr* compartments = nullptr;
    if (FAILED(context->QueryInterface(IID_PPV_ARGS(&compartments))) || compartments == nullptr)
        return false;
    const auto compartmentIsTrue = [compartments](REFGUID guid) {
        ITfCompartment* compartment = nullptr;
        const HRESULT getResult = compartments->GetCompartment(guid, &compartment);
        if (FAILED(getResult) || compartment == nullptr)
            return false;

        VARIANT value{};
        VariantInit(&value);
        const HRESULT valueResult = compartment->GetValue(&value);
        SafeRelease(compartment);
        if (FAILED(valueResult))
            return false;

        bool enabled = false;
        if (value.vt == VT_I4)
            enabled = value.lVal != 0;
        else if (value.vt == VT_BOOL)
            enabled = value.boolVal != VARIANT_FALSE;
        VariantClear(&value);
        return enabled;
    };

    const bool keyboardDisabled = compartmentIsTrue(GUID_COMPARTMENT_KEYBOARD_DISABLED);
    const bool emptyContext = compartmentIsTrue(GUID_COMPARTMENT_EMPTYCONTEXT);
    const bool disabled = keyboardDisabled || emptyContext;
    DiagnosticEvent("context", status.dwDynamicFlags, keyboardDisabled ? 1 : 0,
                    emptyContext ? 1 : 0);
    SafeRelease(compartments);
    return !disabled;
}

void NativeCandidate::DiagnosticEvent(const char* event, long long first,
                                      long long second, long long third) const
{
    if (event == nullptr) return;
    const std::filesystem::path directory(ModuleDirectory());
    const std::filesystem::path flagPath = directory / L"ClaudeZhInput.NativeCandidate.diagnostics";
    std::ifstream flag(flagPath);
    std::string enabled;
    std::getline(flag, enabled);
    if (TrimAscii(enabled) != "1") return;

    std::lock_guard lock(g_diagnosticGate);
    const std::filesystem::path logPath = directory / L"ClaudeZhInput.NativeCandidate.diagnostics.log";
    std::ofstream log(logPath, std::ios::app);
    if (!log) return;
    log << "tick=" << GetTickCount64()
        << " pid=" << GetCurrentProcessId()
        << " event=" << event
        << " a=" << first
        << " b=" << second
        << " c=" << third << "\n";
}

bool NativeCandidate::HasCtrl() const
{
    return (GetKeyState(VK_CONTROL) & 0x8000) != 0;
}

bool NativeCandidate::HasAlt() const
{
    return (GetKeyState(VK_MENU) & 0x8000) != 0;
}

bool NativeCandidate::HasWin() const
{
    return (GetKeyState(VK_LWIN) & 0x8000) != 0 || (GetKeyState(VK_RWIN) & 0x8000) != 0;
}

DWORD NativeCandidate::Modifiers() const
{
    DWORD modifiers = 0;
    const bool physicalShift = (GetKeyState(VK_SHIFT) & 0x8000) != 0 || _shiftDown;
    const bool capsLock = (GetKeyState(VK_CAPITAL) & 0x0001) != 0;
    // Rime receives the effective character state as Shift plus the native
    // CapsLock bit.  This keeps Shift+Caps producing lowercase while still
    // exposing CapsLock to a schema that distinguishes it.
    if (physicalShift ^ capsLock) modifiers |= SHIFT_MASK;
    if (capsLock) modifiers |= CAPS_MASK;
    if (HasCtrl()) modifiers |= CONTROL_MASK;
    if (HasAlt()) modifiers |= ALT_MASK;
    return modifiers;
}

bool NativeCandidate::IsShift(UINT key) const
{
    return key == VK_SHIFT || key == VK_LSHIFT || key == VK_RSHIFT;
}

bool NativeCandidate::IsLetter(UINT key) const
{
    return key >= 'A' && key <= 'Z';
}

bool NativeCandidate::IsCandidateKey(UINT key) const
{
    return key >= '1' && key <= '9';
}

bool NativeCandidate::IsSpecialKey(UINT key) const
{
    return key == VK_BACK || key == VK_ESCAPE || key == VK_SPACE || key == VK_RETURN ||
           key == VK_PRIOR || key == VK_NEXT || key == VK_UP || key == VK_DOWN;
}

bool NativeCandidate::IsInnerPunctuation(UINT key) const
{
    return key == VK_OEM_COMMA || key == VK_OEM_1 || key == VK_OEM_2 || key == VK_OEM_5 ||
           key == VK_OEM_7 || key == VK_OEM_4 || key == VK_OEM_6;
}

bool NativeCandidate::IsSentencePunctuation(UINT key) const
{
    const bool shifted = (GetKeyState(VK_SHIFT) & 0x8000) != 0 || _shiftDown;
    return (key == VK_OEM_PERIOD && !shifted) ||
           (key == '1' && shifted) ||
           (key == VK_OEM_2 && shifted);
}

bool NativeCandidate::IsLogicalComposition() const
{
    std::lock_guard lock(_stateGate);
    return _logicalComposition;
}

bool NativeCandidate::ShouldEatKey(ITfContext* context, UINT key) const
{
    if (_suspended) return false;
    const bool enabled = _active && _pipe && _pipe->AcceptingInput() && IsEnabledContext(context) &&
        !RetiredInContext(context) &&
        (!_awaitingSince || GetTickCount64() - _awaitingSince < RESPONSE_TIMEOUT_MS);
    std::lock_guard lock(_stateGate);
    return InputRouting::Eat(key, enabled, HasCtrl(), HasAlt(), HasWin(),
        _mode == "normal", _asciiMode, _logicalComposition, _commandActive, _commandPrefixPending,
        _sequence > _lastAppliedSequence);
}

UINT NativeCandidate::RimeKey(UINT key, DWORD modifiers) const
{
    const bool effectiveShift = (modifiers & SHIFT_MASK) != 0;
    const bool capsLock = (modifiers & CAPS_MASK) != 0;
    const bool physicalShift = effectiveShift ^ capsLock;

    // librime's key table uses ASCII/X11 keysyms.  A Windows virtual key is
    // not the same value: VK_N is 0x4e while the Rime key is 'n' (0x6e).
    if (const UINT mapped = InputRouting::LetterOrDigit(key, physicalShift, capsLock)) return mapped;
    if (key == VK_SPACE) return 0x20;

    // Prefer the active keyboard layout for OEM keys.  Only printable ASCII
    // is forwarded to the Rime protocol; layouts producing another character
    // fall back to the standard Windows US key positions below.
    if (key == VK_OEM_1 || key == VK_OEM_2 || key == VK_OEM_3 ||
        key == VK_OEM_4 || key == VK_OEM_5 || key == VK_OEM_6 ||
        key == VK_OEM_7 || key == VK_OEM_8 || key == VK_OEM_102 ||
        key == VK_OEM_COMMA || key == VK_OEM_MINUS || key == VK_OEM_PERIOD ||
        key == VK_OEM_PLUS)
    {
        BYTE keyboardState[256]{};
        if (GetKeyboardState(keyboardState))
        {
            keyboardState[VK_SHIFT] = physicalShift ? 0x80 : 0;
            keyboardState[VK_CONTROL] = (modifiers & CONTROL_MASK) != 0 ? 0x80 : 0;
            keyboardState[VK_MENU] = (modifiers & ALT_MASK) != 0 ? 0x80 : 0;
            keyboardState[VK_CAPITAL] = capsLock ? 1 : 0;
            const HKL layout = GetKeyboardLayout(0);
            const UINT scanCode = MapVirtualKeyExW(key, MAPVK_VK_TO_VSC, layout);
            wchar_t translated[4]{};
            const int count = ToUnicodeEx(key, scanCode, keyboardState, translated,
                                          ARRAYSIZE(translated), 4, layout);
            if (count == 1 && translated[0] >= 0x20 && translated[0] <= 0x7e)
                return static_cast<UINT>(translated[0]);
        }
    }

    switch (key)
    {
    case VK_BACK: return 0xff08;
    case VK_TAB: return 0xff09;
    case VK_RETURN: return 0xff0d;
    case VK_ESCAPE: return 0xff1b;
    case VK_HOME: return 0xff50;
    case VK_LEFT: return 0xff51;
    case VK_UP: return 0xff52;
    case VK_RIGHT: return 0xff53;
    case VK_DOWN: return 0xff54;
    case VK_PRIOR: return 0xff55;
    case VK_NEXT: return 0xff56;
    case VK_END: return 0xff57;
    case VK_INSERT: return 0xff63;
    case VK_DELETE: return 0xffff;
    case VK_SHIFT: case VK_LSHIFT: return 0xffe1;
    case VK_RSHIFT: return 0xffe2;
    case VK_NUMPAD0: case VK_NUMPAD1: case VK_NUMPAD2: case VK_NUMPAD3: case VK_NUMPAD4:
    case VK_NUMPAD5: case VK_NUMPAD6: case VK_NUMPAD7: case VK_NUMPAD8: case VK_NUMPAD9:
        return '0' + key - VK_NUMPAD0;
    case VK_ADD: return '+';
    case VK_SUBTRACT: return '-';
    case VK_MULTIPLY: return '*';
    case VK_DIVIDE: return '/';
    case VK_DECIMAL: return '.';
    case VK_OEM_MINUS: return physicalShift ? '_' : '-';
    case VK_OEM_PLUS: return physicalShift ? '+' : '=';
    case VK_OEM_COMMA: return physicalShift ? '<' : ',';
    case VK_OEM_PERIOD: return physicalShift ? '>' : '.';
    case VK_OEM_1: return physicalShift ? ':' : ';';
    case VK_OEM_2: return physicalShift ? '?' : '/';
    case VK_OEM_3: return physicalShift ? '~' : '`';
    case VK_OEM_4: return physicalShift ? '{' : '[';
    case VK_OEM_5: return physicalShift ? '|' : '\\';
    case VK_OEM_6: return physicalShift ? '}' : ']';
    case VK_OEM_7: return physicalShift ? '"' : '\'';
    case VK_OEM_102: return physicalShift ? '>' : '<';
    default: return key;
    }
}

std::string NativeCandidate::NewRequestId()
{
    return _sessionId + "-r" + std::to_string(++_requestCounter);
}

bool NativeCandidate::SendEngineKey(UINT key, DWORD modifiers)
{
    if (!_pipe || !_pipe->AcceptingInput() || !_context) return false;
    const UINT rimeKey = RimeKey(key, modifiers);
    if (_suspended) return false;
    if (_finalizationSequence)
    {
        // Enter confirms the sentence already being translated. Repeated
        // confirmation must neither cancel that transaction nor send the host
        // an Enter that could submit its still-Chinese preview. Keep the same
        // request even when the pending reply or final TSF write is queued.
        if (rimeKey == 0xff0d) return true;
        if (rimeKey == 0xff08 || rimeKey == 0xffff || (rimeKey >= 0xff50 && rimeKey <= 0xff57))
        {
            AbandonInput(false);
            return false;
        }
        if (!_continuousSentences) AbandonInput(false); // Safe interaction during an older-backend rollback.
        // The backend owns an ordered sentence queue in this composition.
        // Text starts a new tail; boundary edits restore/cancel the pending
        // source there. Keep the context generation and unacknowledged keys.
        _finalizationSequence = 0;
    }
    const auto before = _displayedText;
    // Store a local preview under a real key-event write lock before claiming
    // a text key. If the backend dies, this text stays in its original host.
    if (!PreviewInput(rimeKey)) return false;
    const bool wroteText = before != _displayedText;
    CaptureCaret();
    const long long sequence = ++_sequence;
    const auto request = NewRequestId();
    std::string mode;
    { std::lock_guard lock(_stateGate); mode = _mode; }
    if (mode == "claude" && (rimeKey == '.' || rimeKey == '!' || rimeKey == '?' || rimeKey == 0xff0d))
        _finalizationSequence = sequence;
    _previewKeys.push_back({sequence, rimeKey});
    _lastSentRequest = request;
    if (!_awaitingSince) _awaitingSince = GetTickCount64();
    json message = {{"type", "engine-key"}, {"sessionId", _sessionId},
        {"sequence", sequence}, {"requestId", request},
        {"keycode", static_cast<int>(rimeKey)}, {"modifiers", static_cast<int>(modifiers)}};
    if (_hasCaret) { message["screenX"] = _caret.x; message["screenY"] = _caret.y; }
    if (!_pipe->Send(message.dump()))
    {
        AbandonInput(true);
        return wroteText; // Already written text must not be echoed twice by the host.
    }
    ArmRetry();
    return true;
}

void NativeCandidate::EditPreview(std::wstring& text, int& caret, UINT key, bool clear)
{
    caret = std::clamp(caret, 0, static_cast<int>(text.size()));
    if (clear) { text.clear(); caret = 0; return; }
    const auto previous = [&] {
        int pos = caret - 1;
        if (pos > 0 && text[pos] >= 0xdc00 && text[pos] <= 0xdfff &&
            text[pos-1] >= 0xd800 && text[pos-1] <= 0xdbff) --pos;
        return std::max(0, pos);
    };
    const auto next = [&] {
        int pos = caret + 1;
        if (caret + 1 < static_cast<int>(text.size()) && text[caret] >= 0xd800 && text[caret] <= 0xdbff &&
            text[caret+1] >= 0xdc00 && text[caret+1] <= 0xdfff) ++pos;
        return std::min(static_cast<int>(text.size()), pos);
    };
    if (key == 0xff08 && caret > 0) { int pos = previous(); text.erase(pos, caret-pos); caret = pos; }
    else if (key == 0xffff && caret < static_cast<int>(text.size())) text.erase(caret, next()-caret);
    else if (key == 0xff51) caret = previous();
    else if (key == 0xff53) caret = next();
    else if (key == 0xff50) caret = 0;
    else if (key == 0xff57) caret = static_cast<int>(text.size());
    else if (key >= 0x20 && key <= 0x7e) { text.insert(caret, 1, static_cast<wchar_t>(key)); ++caret; }
    // Return finalizes the displayed sentence; no synthetic newline is added.
}

bool NativeCandidate::PreviewInput(UINT key, bool clear)
{
    if (!_context || RetiredInContext(_context)) return false;
    if (_pendingEdit && _pendingEdit->commitApplied && !_pendingEdit->done && !_composition)
    {
        // A host refused to attach the already written suffix. Keep that
        // text in place and let the host handle subsequent keys.
        AbandonInput(false);
        return false;
    }
    auto* context = _context;
    const auto generation = _contextGeneration;
    auto text = _displayedText;
    int caret = _displayedCaret;
    EditPreview(text, caret, key, clear);
    const HRESULT hr = RequestEdit(context, [this, context, generation, text, caret](TfEditCookie ec) {
        if (!_active || generation != _contextGeneration || !FocusStillMatches(context) || !IsEnabledContext(context))
            return TF_E_DISCONNECTED;
        if (!CompositionStillMatches(context, ec)) { AbandonInput(false); return TF_E_DISCONNECTED; }
        if (text == _displayedText && caret == _displayedCaret) return S_OK;
        return StartOrUpdateComposition(context, ec, text, caret);
    }, TF_ES_SYNC | TF_ES_READWRITE);
    return hr == S_OK;
}

void NativeCandidate::CaptureCaret()
{
    if (_context == nullptr) return;
    auto* context = _context;
    RequestEdit(context, [this, context](TfEditCookie ec) {
        TF_SELECTION selection{};
        ULONG count = 0;
        ITfContextView* view = nullptr;
        HRESULT hr = context->GetSelection(ec, TF_DEFAULT_SELECTION, 1, &selection, &count);
        if (SUCCEEDED(hr) && count && selection.range && SUCCEEDED(context->GetActiveView(&view)))
        {
            RECT rect{}; BOOL clipped = FALSE;
            hr = view->GetTextExt(ec, selection.range, &rect, &clipped);
            if (SUCCEEDED(hr)) { _caret = {rect.left, rect.bottom}; _hasCaret = true; }
        }
        SafeRelease(selection.range); SafeRelease(view);
        return hr;
    }, TF_ES_SYNC | TF_ES_READ);
}

void NativeCandidate::SendEngineReset()
{
    const auto sequence = ++_sequence;
    _contextStartSequence = sequence;
    _logicalComposition = false;
    const auto request = NewRequestId();
    if (_pipe) _pipe->Send(json{{"type", "engine-reset"}, {"sessionId", _sessionId},
        {"sequence", sequence}, {"requestId", request}}.dump());
}

void NativeCandidate::SendEngineCancel()
{
    if (_pipe == nullptr) return;
    std::string mode;
    { std::lock_guard lock(_stateGate); mode = _mode; }
    if (mode == "claude" && _finalizationSequence)
    {
        // Esc may race a completed translation waiting for a TSF lock.
        // Preserve the source already shown, invalidate that queued write,
        // and reset the backend rather than letting late English replace it.
        AbandonInput(false);
        return;
    }
    const bool clear = mode == "normal";
    if (!PreviewInput(0xff1b, clear)) { AbandonInput(false); return; }
    const long long sequence = ++_sequence;
    _previewKeys.push_back({sequence, 0xff1b, clear});
    if (!_awaitingSince) _awaitingSince = GetTickCount64();
    std::string request;
    {
        std::lock_guard lock(_stateGate);
        request = _lastSentRequest;
    }
    json message = {
        {"type", "engine-cancel"},
        {"sessionId", _sessionId},
        {"sequence", sequence},
        {"requestId", request}
    };
    if (!_pipe->Send(message.dump())) AbandonInput(true);
    ArmRetry();
}

bool NativeCandidate::SendEnginePage(bool backward)
{
    if (_pipe == nullptr || !_pipe->Ready()) return false;
    std::string request;
    {
        std::lock_guard lock(_stateGate);
        request = _lastAppliedRequest;
    }
    json message = {
        {"type", "engine-page"},
        {"sessionId", _sessionId},
        {"sequence", _sequence},
        {"requestId", request},
        {"backward", backward}
    };
    if (!_pipe->Send(message.dump())) { AbandonInput(true); return false; }
    if (!_awaitingSince) _awaitingSince = GetTickCount64();
    ArmRetry();
    return true;
}

void NativeCandidate::SendEngineSelect(int index)
{
    if (_pipe == nullptr || index < 0) return;
    std::string request;
    {
        std::lock_guard lock(_stateGate);
        request = _lastAppliedRequest;
    }
    json message = {
        {"type", "engine-select"},
        {"sessionId", _sessionId},
        {"sequence", _sequence},
        {"requestId", request},
        {"index", index}
    };
    if (!_pipe->Send(message.dump())) { AbandonInput(true); return; }
    if (!_awaitingSince) _awaitingSince = GetTickCount64();
    ArmRetry();
}

void NativeCandidate::SendCommandQuery()
{
    if (_pipe == nullptr) return;
    json message = {
        {"type", "commands"},
        {"sessionId", _sessionId},
        {"requestId", _commandRequestId},
        {"text", _commandText}
    };
    _pipe->Send(message.dump());
}

bool NativeCandidate::HandleCommandKey(ITfContext*, UINT key, LPARAM)
{
    if (!_commandActive) return false;
    if (key == VK_ESCAPE)
    {
        _commandActive = false;
        _commandText.clear();
        _commandRequestId.clear();
        return true;
    }
    if (key == VK_RETURN)
    {
        // The picker fills text; Enter is left to the host exactly as the
        // existing service contract requires.
        _commandActive = false;
        _commandText.clear();
        _commandRequestId.clear();
        return false;
    }
    if (key == VK_BACK)
    {
        if (!_commandText.empty())
            _commandText.pop_back();
        _commandRequestId = NewRequestId();
        SendCommandQuery();
        return true;
    }
    if (IsLetter(key))
    {
        _commandText.push_back(static_cast<char>('a' + key - 'A'));
        _commandRequestId = NewRequestId();
        SendCommandQuery();
        return true;
    }
    if (key == VK_SPACE)
    {
        _commandText.push_back(' ');
        _commandRequestId = NewRequestId();
        SendCommandQuery();
        return true;
    }
    return true;
}

void NativeCandidate::SendLiteralSemicolon(ITfContext* context)
{
    std::string mode;
    {
        std::lock_guard lock(_stateGate);
        mode = _mode;
    }
    const std::wstring literal = mode == "normal" ? L"；" : L";";
    // The first semicolon is already visible as a local preview.
    const auto hr = RequestEdit(context, [this, context, literal](TfEditCookie ec) {
        return CommitText(context, ec, literal);
    }, TF_ES_SYNC | TF_ES_READWRITE);
    if (FAILED(hr)) AbandonInput(false);
}

void NativeCandidate::QueueLocalCommit(std::wstring text, ITfContext* context)
{
    if (context == nullptr || _wakeWindow == nullptr || text.empty()) return;
    context->AddRef();
    {
        std::lock_guard lock(_stateGate);
        _localCommits.emplace_back(std::move(text), context);
    }
    PostMessageW(_wakeWindow, WM_NATIVE_LOCAL_COMMIT, 0, 0);
}

bool NativeCandidate::HandleAsciiKey(ITfContext* context, UINT key, LPARAM)
{
    (void)context;
    if (!_asciiMode) return false;
    if (key == VK_BACK || key == VK_ESCAPE) return false;
    if (key == VK_RETURN || key == VK_SPACE || IsLetter(key) || IsCandidateKey(key) || key == VK_OEM_PERIOD ||
        key == VK_OEM_COMMA || key == VK_OEM_1 || key == VK_OEM_2 || key == VK_OEM_3 || key == VK_OEM_4 ||
        key == VK_OEM_5 || key == VK_OEM_6 || key == VK_OEM_7 || key == VK_OEM_PLUS || key == VK_OEM_MINUS)
    {
        // In normal mode, an ASCII state intentionally delegates to the host;
        // this function only documents that it is not an engine key.  False
        // lets Terminal/editor receive the original key without simulation.
        return false;
    }
    return false;
}

bool NativeCandidate::HandleKey(ITfContext* context, UINT key, LPARAM flags, BOOL* eaten)
{
    if (eaten == nullptr) return false;
    *eaten = FALSE;
    if (_shiftDown && !IsShift(key)) _shiftUsed = true;
    // CoreText may call OnKeyDown without first calling OnTestKeyDown.
    // Apply the same decision here so ASCII and host shortcuts pass through.
    if (FocusStillMatches(context)) _focusLost = false;
    MaintainInputState();
    FinishRetiredCompositions(true);
    if (!ShouldEatKey(context, key)) return false;
    ReplaceContext(context);
    std::string mode;
    {
        std::lock_guard lock(_stateGate);
        mode = _mode;
    }

    if (IsShift(key))
    {
        if (!_shiftDown)
        {
            _shiftDown = true;
            _shiftUsed = false;
        }
        *eaten = TRUE;
        return true;
    }
    if (_shiftDown) _shiftUsed = true;

    if (_commandPrefixPending)
    {
        _commandPrefixPending = false;
        if (key == VK_OEM_1 && !HasCtrl() && !_shiftDown)
        {
            const auto hr = RequestEdit(context, [this, context](TfEditCookie ec) {
                return EndComposition(context, ec, true);
            }, TF_ES_SYNC | TF_ES_READWRITE);
            if (FAILED(hr)) { AbandonInput(false); return false; }
            _commandActive = true;
            _commandText.clear();
            _commandRequestId = NewRequestId();
            SendCommandQuery();
            *eaten = TRUE;
            return true;
        }
        SendLiteralSemicolon(context);
    }

    if (HasCtrl() && key == VK_OEM_1 && !HasAlt())
    {
        _commandActive = true;
        _commandText.clear();
        _commandRequestId = NewRequestId();
        SendCommandQuery();
        *eaten = TRUE;
        return true;
    }
    if (!_commandActive && !_asciiMode && key == VK_OEM_1 && !HasCtrl() && !_shiftDown && !_logicalComposition)
    {
        if (!PreviewInput(';')) return false;
        _commandPrefixPending = true;
        *eaten = TRUE;
        return true;
    }
    if (_commandActive)
    {
        *eaten = HandleCommandKey(context, key, flags) ? TRUE : FALSE;
        return true;
    }

    const DWORD modifiers = Modifiers();

    // Claude mode uses the backtick as a mode boundary.  It must enter the
    // engine even when the sentence is currently empty so code fragments can
    // remain ASCII inside the same protected composition.
    if (mode == "claude" && key == VK_OEM_3)
    {
        _logicalComposition = true;
        *eaten = SendEngineKey(key, modifiers) ? TRUE : FALSE;
        return true;
    }

    if (mode == "normal" && _asciiMode && _sequence == _lastAppliedSequence) return false;

    if (key == VK_ESCAPE && IsLogicalComposition())
    {
        SendEngineCancel();
        *eaten = TRUE;
        return true;
    }
    if (key == VK_BACK && IsLogicalComposition())
    {
        *eaten = SendEngineKey(key, modifiers) ? TRUE : FALSE;
        return true;
    }
    if (key == VK_PRIOR || key == VK_NEXT)
    {
        if (!IsLogicalComposition()) return false;
        *eaten = SendEnginePage(key == VK_PRIOR) ? TRUE : FALSE;
        return true;
    }
    if (key == VK_UP || key == VK_DOWN)
    {
        if (!IsLogicalComposition()) return false;
        *eaten = SendEngineKey(key, modifiers) ? TRUE : FALSE;
        return true;
    }
    if (key == VK_LEFT || key == VK_RIGHT || key == VK_HOME || key == VK_END || key == VK_DELETE)
    {
        *eaten = SendEngineKey(key, modifiers) ? TRUE : FALSE;
        return true;
    }
    if (InputRouting::Printable(key))
    {
        _logicalComposition = true;
        *eaten = SendEngineKey(key, modifiers) ? TRUE : FALSE;
        return true;
    }
    if (key == VK_SPACE || key == VK_RETURN || IsLetter(key) || IsCandidateKey(key))
    {
        if (key == VK_SPACE || key == VK_RETURN) _logicalComposition = true;
        else _logicalComposition = true;
        *eaten = SendEngineKey(key, modifiers) ? TRUE : FALSE;
        return true;
    }

    if (mode == "claude" && _logicalComposition &&
        (IsInnerPunctuation(key) || IsSentencePunctuation(key)))
    {
        *eaten = SendEngineKey(key, modifiers) ? TRUE : FALSE;
        return true;
    }
    if (mode == "normal" && _logicalComposition &&
        (IsInnerPunctuation(key) || IsSentencePunctuation(key)))
    {
        *eaten = SendEngineKey(key, modifiers) ? TRUE : FALSE;
        return true;
    }
    return false;
}

STDMETHODIMP NativeCandidate::OnSetFocus(BOOL foreground)
{
    _focusLost = foreground == FALSE;
    DiagnosticEvent("focus", foreground ? 1 : 0, _focusLost ? 1 : 0, 0);
    return S_OK;
}

STDMETHODIMP NativeCandidate::OnTestKeyDown(ITfContext* context, WPARAM wParam, LPARAM, BOOL* eaten)
{
    if (eaten == nullptr) return E_POINTER;
    const UINT key = static_cast<UINT>(wParam);
    if (_shiftDown && !IsShift(key)) _shiftUsed = true;
    if (FocusStillMatches(context)) _focusLost = false;
    MaintainInputState();
    FinishRetiredCompositions(true);
    *eaten = ShouldEatKey(context, key) ? TRUE : FALSE;
    DiagnosticEvent("test-down", key, *eaten ? 1 : 0, _focusLost ? 1 : 0);
    return S_OK;
}

STDMETHODIMP NativeCandidate::OnTestKeyUp(ITfContext*, WPARAM wParam, LPARAM, BOOL* eaten)
{
    if (eaten == nullptr) return E_POINTER;
    *eaten = IsShift(static_cast<UINT>(wParam)) && _shiftDown ? TRUE : FALSE;
    DiagnosticEvent("test-up", static_cast<UINT>(wParam), *eaten ? 1 : 0, _focusLost ? 1 : 0);
    return S_OK;
}

STDMETHODIMP NativeCandidate::OnKeyDown(ITfContext* context, WPARAM wParam, LPARAM lParam, BOOL* eaten)
{
    if (eaten == nullptr) return E_POINTER;
    const UINT key = static_cast<UINT>(wParam);
    HandleKey(context, key, lParam, eaten);
    DiagnosticEvent("key-down", key, *eaten ? 1 : 0, _focusLost ? 1 : 0);
    return S_OK;
}

STDMETHODIMP NativeCandidate::OnKeyUp(ITfContext* context, WPARAM wParam, LPARAM, BOOL* eaten)
{
    (void)context;
    if (eaten == nullptr) return E_POINTER;
    *eaten = FALSE;
    const UINT key = static_cast<UINT>(wParam);
    if (!IsShift(key) || !_shiftDown) return S_OK;
    MaintainInputState();
    if (_pipe && _pipe->Ready() && IsEnabledContext(context) && !_shiftUsed && !HasCtrl() && !HasAlt())
    {
        SendEngineKey(key, 0);
    }
    _shiftDown = false;
    _shiftUsed = false;
    *eaten = TRUE;
    DiagnosticEvent("key-up", key, 1, _focusLost ? 1 : 0);
    return S_OK;
}

STDMETHODIMP NativeCandidate::OnPreservedKey(ITfContext*, REFGUID, BOOL* eaten)
{
    if (eaten == nullptr) return E_POINTER;
    *eaten = FALSE;
    return S_OK;
}

STDMETHODIMP NativeCandidate::OnCompositionTerminated(TfEditCookie, ITfComposition* composition)
{
    for (const auto& old : _retiredCompositions)
        if (old->composition.get() == composition) old->done = true;
    if (_endingOwnComposition || composition != _composition) return S_OK;
    // The host/user terminated this specific composition. Never invalidate a
    // newer composition when an asynchronous close of the old one completes.
    ResetCompositionState();
    AbandonInput(false);
    return S_OK;
}

void NativeCandidate::CreateWakeWindow()
{
    if (_wakeWindow != nullptr) return;
    const unsigned long serial = ++g_wakeSerial;
    _wakeClassName = L"ClaudeZhInput.NativeCandidate.Wake." +
                     std::to_wstring(GetCurrentProcessId()) + L"." + std::to_wstring(serial);
    WNDCLASSW windowClass{};
    windowClass.lpfnWndProc = &NativeCandidate::WakeWindowProc;
    windowClass.hInstance = g_instance;
    windowClass.lpszClassName = _wakeClassName.c_str();
    RegisterClassW(&windowClass);
    _wakeWindow = CreateWindowExW(0, _wakeClassName.c_str(),
                                  L"ClaudeZhInput NativeCandidate wake",
                                  0, 0, 0, 0, 0, HWND_MESSAGE, nullptr, g_instance, this);
    if (_wakeWindow) _powerNotification = RegisterSuspendResumeNotification(_wakeWindow, DEVICE_NOTIFY_WINDOW_HANDLE);
    if (_wakeWindow == nullptr)
    {
        UnregisterClassW(_wakeClassName.c_str(), g_instance);
        _wakeClassName.clear();
    }
}

void NativeCandidate::DestroyWakeWindow()
{
    if (_powerNotification) { UnregisterSuspendResumeNotification(_powerNotification); _powerNotification = nullptr; }
    if (_wakeWindow != nullptr)
    {
        KillTimer(_wakeWindow, RETRY_TIMER);
        DestroyWindow(_wakeWindow);
        _wakeWindow = nullptr;
    }
    if (!_wakeClassName.empty())
    {
        UnregisterClassW(_wakeClassName.c_str(), g_instance);
        _wakeClassName.clear();
    }
}

LRESULT CALLBACK NativeCandidate::WakeWindowProc(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam)
{
    auto* self = reinterpret_cast<NativeCandidate*>(GetWindowLongPtrW(hwnd, GWLP_USERDATA));
    if (message == WM_NCCREATE)
    {
        auto* create = reinterpret_cast<CREATESTRUCTW*>(lParam);
        self = static_cast<NativeCandidate*>(create->lpCreateParams);
        SetWindowLongPtrW(hwnd, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(self));
    }
    if (self == nullptr) return DefWindowProcW(hwnd, message, wParam, lParam);
    switch (message)
    {
    case WM_POWERBROADCAST:
        self->HandlePowerEvent(wParam);
        return TRUE;
    case WM_TIMER:
        if (wParam != RETRY_TIMER) break;
        [[fallthrough]];
    case WM_NATIVE_WAKE:
        self->MaintainInputState();
        self->FinishRetiredCompositions();
        self->ApplyPendingState();
        self->ArmRetry();
        return 0;
    case WM_NATIVE_SELECT:
        self->OnSelectMessage(static_cast<int>(wParam));
        return 0;
    case WM_NATIVE_LOCAL_COMMIT:
        self->OnLocalCommitMessage();
        return 0;
    case WM_NATIVE_SHOW_COMMANDS:
        self->OpenToolbarCommands();
        return 0;
    case WM_NATIVE_PROBE:
    {
        GUID foreground{};
        if (self->_keystrokeMgr != nullptr && SUCCEEDED(self->_keystrokeMgr->GetForeground(&foreground)) &&
            foreground == CLSID_NativeCandidate)
            return 1;
        return 0;
    }
    default:
        return DefWindowProcW(hwnd, message, wParam, lParam);
    }
    return DefWindowProcW(hwnd, message, wParam, lParam);
}

void NativeCandidate::OpenToolbarCommands()
{
    GUID foreground{};
    ITfDocumentMgr* document = nullptr;
    ITfContext* context = nullptr;
    const bool selected = _active && _keystrokeMgr != nullptr &&
        SUCCEEDED(_keystrokeMgr->GetForeground(&foreground)) && foreground == CLSID_NativeCandidate;
    const bool hostFocused = GetWindowThreadProcessId(GetForegroundWindow(), nullptr) == GetCurrentThreadId();
    if (selected && hostFocused && _threadMgr != nullptr && SUCCEEDED(_threadMgr->GetFocus(&document)) && document != nullptr)
        document->GetTop(&context);
    if (context != nullptr && IsEnabledContext(context) && !IsLogicalComposition())
    {
        ReplaceContext(context);
        {
            std::lock_guard lock(_stateGate);
            _commandActive = true;
            _commandPrefixPending = false;
            _commandText.clear();
            _commandRequestId = NewRequestId();
        }
        SendCommandQuery();
    }
    else if (_pipe != nullptr)
        _pipe->Send(json({{"type", "toolbar-command-rejected"}, {"sessionId", _sessionId}}).dump());
    SafeRelease(context);
    SafeRelease(document);
}

void NativeCandidate::OnSelectMessage(int index)
{
    if (!_active || index < 0) return;
    SendEngineSelect(index);
}

void NativeCandidate::OnLocalCommitMessage()
{
    std::pair<std::wstring, ITfContext*> item;
    {
        std::lock_guard lock(_stateGate);
        if (!_localCommits.empty())
        {
            item = std::move(_localCommits.front());
            _localCommits.pop_front();
        }
        else if (!_commandFills.empty() && _context != nullptr)
        {
            item.first = std::move(_commandFills.front());
            item.second = _context;
            item.second->AddRef();
            _commandFills.pop_front();
        }
    }
    ITfContext* context = item.second;
    if (context == nullptr) return;
    if (context == _context && FocusStillMatches(context))
    {
        auto state = std::make_unique<EngineState>();
        state->local = true;
        state->sequence = _lastAppliedSequence;
        { std::lock_guard lock(_stateGate); state->mode = _mode; }
        state->asciiMode = _asciiMode;
        state->commit = std::move(item.first);
        PostState(state.release());
    }
    context->Release();
}

bool NativeCandidate::FocusStillMatches(ITfContext* context) const
{
    if (context == nullptr || _threadMgr == nullptr) return false;
    ITfDocumentMgr* focused = nullptr;
    ITfDocumentMgr* document = nullptr;
    if (FAILED(_threadMgr->GetFocus(&focused)) || focused == nullptr)
    {
        SafeRelease(focused);
        return false;
    }
    if (FAILED(context->GetDocumentMgr(&document)) || document == nullptr)
    {
        SafeRelease(focused);
        SafeRelease(document);
        return false;
    }
    ITfContext* top = nullptr;
    const bool same = IsSameUnknown(focused, document) &&
        SUCCEEDED(focused->GetTop(&top)) && top != nullptr && IsSameUnknown(top, context);
    SafeRelease(top);
    SafeRelease(focused);
    SafeRelease(document);
    return same;
}

bool NativeCandidate::CompositionStillMatches(ITfContext* context, TfEditCookie cookie) const
{
    if (!_composition || _compositionContext != context) return true;
    ITfRange* range = nullptr;
    if (FAILED(_composition->GetRange(&range)) || !range) return false;
    auto keep = std::shared_ptr<ITfRange>(range, [](ITfRange* r) { r->Release(); });
    std::wstring actual(_displayedText.size() + 1, L'\0');
    ULONG read = 0;
    if (FAILED(range->GetText(cookie, 0, actual.data(), static_cast<ULONG>(actual.size()), &read))) return false;
    actual.resize(read);
    if (actual != _displayedText) return false;
    // A click/host edit can move the selection without changing focus or
    // terminating the composition. Never move it back for a late translation.
    ITfRange* expected = nullptr;
    if (FAILED(range->Clone(&expected)) || !expected) return false;
    auto keepExpected = std::shared_ptr<ITfRange>(expected, [](ITfRange* r) { r->Release(); });
    LONG shifted = 0;
    if (FAILED(expected->Collapse(cookie, TF_ANCHOR_START)) ||
        FAILED(expected->ShiftEnd(cookie, _displayedCaret, &shifted, nullptr)) || shifted != _displayedCaret ||
        FAILED(expected->Collapse(cookie, TF_ANCHOR_END))) return false;
    TF_SELECTION selection{}; ULONG count = 0;
    const auto hr = context->GetSelection(cookie, TF_DEFAULT_SELECTION, 1, &selection, &count);
    auto selected = std::shared_ptr<ITfRange>(selection.range, [](ITfRange* r) { if (r) r->Release(); });
    if (FAILED(hr) || count != 1 || !selected) return false;
    BOOL empty = FALSE; LONG compare = 1;
    return SUCCEEDED(selected->IsEmpty(cookie, &empty)) && empty &&
        SUCCEEDED(selected->CompareStart(cookie, expected, TF_ANCHOR_START, &compare)) && compare == 0;
}

void NativeCandidate::PostState(EngineState* state)
{
    if (state == nullptr) return;
    if (_wakeWindow == nullptr)
    {
        delete state;
        return;
    }
    {
        std::lock_guard lock(_stateGate);
        _pendingStates.emplace_back(state);
    }
    PostMessageW(_wakeWindow, WM_NATIVE_WAKE, 0, 0);
}

void NativeCandidate::ArmRetry()
{
    if (!_wakeWindow) return;
    bool pending;
    { std::lock_guard lock(_stateGate); pending = !_pendingStates.empty(); }
    bool retired = false;
    for (const auto& old : _retiredCompositions)
        retired |= !old->done && FocusStillMatches(old->context.get()) && IsEnabledContext(old->context.get());
    if (_awaitingSince || _pendingEdit || pending || retired)
        SetTimer(_wakeWindow, RETRY_TIMER, 100, nullptr);
    else KillTimer(_wakeWindow, RETRY_TIMER);
}

bool NativeCandidate::RetiredInContext(ITfContext* context) const
{
    for (const auto& old : _retiredCompositions)
        if (!old->done && old->context.get() == context) return true;
    return false;
}

void NativeCandidate::FinishRetiredCompositions(bool synchronous)
{
    for (const auto& old : _retiredCompositions)
    {
        if (old->done || (old->inFlight && !synchronous)) continue;
        if (synchronous && !FocusStillMatches(old->context.get())) continue;
        old->inFlight = true;
        const auto hr = RequestEdit(old->context.get(), [this, old](TfEditCookie ec) {
            if (old->done) return S_OK;
            // This closes only the captured old composition and never replaces
            // text or moves a selection in the newly focused context.
            _endingOwnComposition = true;
            const HRESULT result = old->composition->EndComposition(ec);
            _endingOwnComposition = false;
            old->inFlight = false;
            old->done |= SUCCEEDED(result) || result == TF_E_DISCONNECTED;
            return result;
        }, (synchronous ? TF_ES_SYNC : TF_ES_ASYNCDONTCARE) | TF_ES_READWRITE);
        if (FAILED(hr)) { old->inFlight = false; old->done |= hr == TF_E_DISCONNECTED; }
    }
    std::erase_if(_retiredCompositions, [](const auto& old) { return old->done; });
}

void NativeCandidate::PreserveComposition()
{
    if (_composition && _compositionContext)
    {
        auto old = std::make_shared<RetiredComposition>();
        old->context = RetainCom(_compositionContext);
        old->composition = RetainCom(_composition);
        _retiredCompositions.push_back(std::move(old));
    }
    ResetCompositionState();
    FinishRetiredCompositions();
}

void NativeCandidate::AbandonInput(bool disconnect)
{
    ++_contextGeneration; // Invalidate even edit sessions that TSF already queued.
    _pendingEdit.reset();
    _previewKeys.clear();
    _awaitingSince = 0;
    _finalizationSequence = 0;
    _shiftDown = _shiftUsed = false;
    {
        std::lock_guard lock(_stateGate);
        _pendingStates.clear();
        _commandActive = _commandPrefixPending = false;
        _commandText.clear(); _commandRequestId.clear(); _commandFills.clear();
        for (auto& item : _localCommits) SafeRelease(item.second);
        _localCommits.clear();
        _asciiMode = false;
    }
    PreserveComposition(); // Local previews and the original sentence remain in the host.
    SendEngineReset();
    if (disconnect && _pipe) _pipe->ResetConnection();
    ArmRetry();
}

void NativeCandidate::MaintainInputState()
{
    if (!_active) return;
    if (_pipe && _seenDisconnect != _pipe->DisconnectVersion())
    {
        _seenDisconnect = _pipe->DisconnectVersion();
        AbandonInput(false);
        return;
    }
    const auto now = GetTickCount64();
    if ((_awaitingSince && now - _awaitingSince >= RESPONSE_TIMEOUT_MS) ||
        (_pendingEdit && !_pendingEdit->done && now - _pendingEdit->created >= RESPONSE_TIMEOUT_MS))
    {
        AbandonInput(true);
        return;
    }
    if (_context && (_logicalComposition || _pendingEdit || !_previewKeys.empty()) &&
        (!_pipe || !_pipe->AcceptingInput() || _focusLost || !FocusStillMatches(_context) || !IsEnabledContext(_context)))
        AbandonInput(false);
}

void NativeCandidate::ApplyPendingState()
{
    if (!_active || _suspended) return;
    if (_pendingEdit && _pendingEdit->done) _pendingEdit.reset();
    if (!_pendingEdit)
    {
        std::unique_ptr<EngineState> state;
        {
            std::lock_guard lock(_stateGate);
            if (_pendingStates.empty()) return;
            state = std::move(_pendingStates.front());
            _pendingStates.pop_front();
        }
        if ((!state->local && (state->sequence < _lastAppliedSequence || state->sequence < _contextStartSequence ||
            (state->sequence == 0 && _sequence > 0) || (!state->commit.empty() && state->sequence == _lastCommittedSequence &&
                (state->commitId.empty() ? state->requestId : state->commitId) == _lastCommittedRequest))) || !_context)
        {
            if (_wakeWindow) PostMessageW(_wakeWindow, WM_NATIVE_WAKE, 0, 0);
            return;
        }
        if (_focusLost || !FocusStillMatches(_context) || !IsEnabledContext(_context))
        {
            // A reset reply may arrive while the old field is still blurred.
            // Do not answer an empty reset with another reset indefinitely.
            if (_logicalComposition || !_previewKeys.empty() || _commandActive || _commandPrefixPending)
                AbandonInput(false);
            return;
        }
        auto item = std::make_shared<PendingApply>();
        item->state = std::move(*state);
        item->context = RetainCom(_context);
        item->generation = _contextGeneration;
        _pendingEdit = std::move(item);
    }
    auto item = _pendingEdit;
    if (item->inFlight || item->done) return;
    item->inFlight = true;
    const HRESULT hr = RequestEdit(item->context.get(), [this, item](TfEditCookie ec) {
        item->inFlight = false;
        if (!_active || item->generation != _contextGeneration || item->context.get() != _context)
            return TF_E_DISCONNECTED;
        if (_focusLost || !FocusStillMatches(item->context.get()) || !IsEnabledContext(item->context.get()))
        {
            AbandonInput(false);
            return TF_E_DISCONNECTED;
        }
        if (!CompositionStillMatches(item->context.get(), ec)) { AbandonInput(false); return TF_E_DISCONNECTED; }
        const auto result = ApplyTransaction(item, ec);
        if (SUCCEEDED(result))
        {
            item->done = true;
            if (item->state.sequence >= _finalizationSequence && !item->state.pendingTranslation)
                _finalizationSequence = 0;
            if (!item->state.local) {
                std::lock_guard lock(_stateGate);
                _lastAppliedSequence = item->state.sequence;
                _lastAppliedRequest = item->state.requestId;
                if (!item->state.commit.empty()) { _lastCommittedSequence = item->state.sequence;
                    _lastCommittedRequest = item->state.commitId.empty() ? item->state.requestId : item->state.commitId; }
                _mode = item->state.mode;
                _asciiMode = item->state.asciiMode;
            }
            while (!item->state.local && !_previewKeys.empty() && _previewKeys.front().sequence <= item->state.sequence)
                _previewKeys.pop_front();
            _awaitingSince = _previewKeys.empty() ? 0 : _previewKeys.front().acceptedAt;
            if (_wakeWindow) PostMessageW(_wakeWindow, WM_NATIVE_WAKE, 0, 0);
        }
        ArmRetry();
        return result;
    });
    // TF_S_ASYNC is queued, not applied. Only the callback above advances the
    // applied sequence. Failed scheduling retains this exact packet for retry.
    if (FAILED(hr)) item->inFlight = false;
    ArmRetry();
}

HRESULT NativeCandidate::ApplyTransaction(const std::shared_ptr<PendingApply>& item, TfEditCookie ec)
{
    auto* context = item->context.get();
    auto state = item->state;
    if (!state.local && !state.handled && state.commit.empty() && state.composition.empty() && !item->hostEditApplied)
    {
        for (const auto& key : _previewKeys)
            if (key.sequence == state.sequence && (key.keysym == 0xff08 || key.keysym == 0xffff ||
                key.keysym == 0xff50 || key.keysym == 0xff51 || key.keysym == 0xff53 || key.keysym == 0xff57))
            {
                // This key was claimed while a preceding commit was in
                // transit. Rime no longer owns text to edit. First remove
                // speculative suffixes, then edit the acknowledged host
                // text exactly once under this same context write lock.
                HRESULT hr = ApplyState(context, ec, state);
                if (FAILED(hr)) return hr;
                hr = ApplyHostEdit(context, ec, key.keysym);
                if (FAILED(hr)) return hr;
                item->hostEditApplied = true;
                break;
            }
    }
    // Synchronous previews may run before this asynchronous callback. Keep
    // all later keys in the same text update as the acknowledged result.
    for (const auto& key : _previewKeys)
        if (key.sequence > state.sequence) EditPreview(state.composition, state.caret, key.keysym, key.clear);
    if (state.commit.empty()) return ApplyState(context, ec, state);

    if (!item->commitApplied)
    {
        const auto text = state.commit + state.composition;
        HRESULT hr = StartOrUpdateComposition(context, ec, text,
            static_cast<int>(state.commit.size()) + state.caret);
        if (FAILED(hr)) return hr;
        // The committed prefix and the unacknowledged suffix are now both
        // present. A failed EndComposition can no longer erase the suffix.
        ITfRange* range = nullptr;
        hr = _composition->GetRange(&range);
        if (FAILED(hr)) return hr;
        auto keepRange = std::shared_ptr<ITfRange>(range, [](ITfRange* r) { r->Release(); });
        if (!state.composition.empty())
        {
            ITfRange* tail = nullptr;
            hr = range->Clone(&tail);
            if (FAILED(hr)) return hr;
            item->remainder = std::shared_ptr<ITfRange>(tail, [](ITfRange* r) { r->Release(); });
            LONG shifted = 0;
            hr = tail->ShiftStart(ec, static_cast<LONG>(state.commit.size()), &shifted, nullptr);
            if (FAILED(hr) || shifted != static_cast<LONG>(state.commit.size())) return E_FAIL;
        }
        _endingOwnComposition = true;
        hr = _composition->EndComposition(ec);
        _endingOwnComposition = false;
        if (FAILED(hr)) return hr;
        item->commitApplied = true;
        ResetCompositionState();
    }
    if (item->remainder)
    {
        ITfContextComposition* api = nullptr;
        HRESULT hr = context->QueryInterface(IID_PPV_ARGS(&api));
        if (FAILED(hr)) return hr;
        hr = api->StartComposition(ec, item->remainder.get(), static_cast<ITfCompositionSink*>(this), &_composition);
        api->Release();
        if (FAILED(hr) || !_composition) return FAILED(hr) ? hr : E_FAIL;
        _compositionContext = context;
        context->AddRef();
        _displayedText = state.composition;
        _displayedCaret = state.caret;
    }
    _logicalComposition = !state.composition.empty() || state.sequence < _sequence;
    return S_OK;
}

HRESULT NativeCandidate::ApplyHostEdit(ITfContext* context, TfEditCookie ec, UINT key)
{
    TF_SELECTION selection{};
    ULONG count = 0;
    HRESULT hr = context->GetSelection(ec, TF_DEFAULT_SELECTION, 1, &selection, &count);
    if (FAILED(hr) || count != 1 || !selection.range) return FAILED(hr) ? hr : E_FAIL;
    auto range = std::shared_ptr<ITfRange>(selection.range, [](ITfRange* r) { r->Release(); });
    const bool backward = key == 0xff08 || key == 0xff51 || key == 0xff50;
    const bool deleting = key == 0xff08 || key == 0xffff;
    BOOL empty = FALSE;
    hr = range->IsEmpty(ec, &empty);
    if (FAILED(hr)) return hr;
    if (empty || !deleting)
    {
        hr = range->Collapse(ec, backward ? TF_ANCHOR_START : TF_ANCHOR_END);
        if (FAILED(hr)) return hr;
        // Home/End are bounded by the actual line break, not by the whole
        // document. One UTF-16 unit at a time also avoids splitting a pair.
        const bool line = key == 0xff50 || key == 0xff57;
        for (int step = 0; step < (line ? 32768 : 2); ++step)
        {
            LONG shifted = 0;
            hr = backward ? range->ShiftStart(ec, -1, &shifted, nullptr) : range->ShiftEnd(ec, 1, &shifted, nullptr);
            if (FAILED(hr)) return hr;
            if (!shifted) break;
            wchar_t value[2]{}; ULONG read = 0;
            ITfRange* probe = nullptr;
            hr = range->Clone(&probe);
            if (FAILED(hr)) return hr;
            auto keep = std::shared_ptr<ITfRange>(probe, [](ITfRange* r) { r->Release(); });
            if (backward) { probe->Collapse(ec, TF_ANCHOR_START); probe->ShiftEnd(ec, 1, &shifted, nullptr); }
            else { probe->Collapse(ec, TF_ANCHOR_END); probe->ShiftStart(ec, -1, &shifted, nullptr); }
            hr = probe->GetText(ec, 0, value, 1, &read);
            if (FAILED(hr)) return hr;
            if (line && read && (value[0] == L'\r' || value[0] == L'\n'))
            {
                if (backward) range->ShiftStart(ec, 1, &shifted, nullptr);
                else range->ShiftEnd(ec, -1, &shifted, nullptr);
                break;
            }
            if (!line && !(step == 0 && read && (backward ? value[0] >= 0xdc00 && value[0] <= 0xdfff : value[0] >= 0xd800 && value[0] <= 0xdbff))) break;
        }
    }
    if (deleting) { hr = range->SetText(ec, 0, L"", 0); if (FAILED(hr)) return hr; }
    range->Collapse(ec, backward ? TF_ANCHOR_START : TF_ANCHOR_END);
    selection.style = {TF_AE_NONE, FALSE};
    hr = context->SetSelection(ec, 1, &selection);
    // Never repeat a successful deletion because caret placement failed.
    return deleting ? S_OK : hr;
}

void NativeCandidate::HandlePowerEvent(WPARAM event)
{
    if (event == PBT_APMSUSPEND)
    {
        _suspended = true;
        AbandonInput(true);
    }
    else if (event == PBT_APMRESUMEAUTOMATIC || (event == PBT_APMRESUMESUSPEND && _suspended))
    {
        _suspended = false;
        AbandonInput(true);
    }
}

HRESULT NativeCandidate::RequestEdit(ITfContext* context, std::function<HRESULT(TfEditCookie)> action, DWORD flags)
{
    if (context == nullptr || _threadMgr == nullptr) return E_INVALIDARG;
    auto* session = new (std::nothrow) EditSession(this, context, std::move(action));
    if (session == nullptr) return E_OUTOFMEMORY;
    HRESULT sessionResult = E_FAIL;
    HRESULT requestResult = context->RequestEditSession(
        _clientId, session, flags, &sessionResult);
    session->Release();
    if (FAILED(requestResult)) return requestResult;
    return sessionResult;
}

HRESULT NativeCandidate::ApplyState(ITfContext* context, TfEditCookie editCookie, const EngineState& state)
{
    HRESULT hr = S_OK;
    if (!state.commit.empty())
    {
        hr = CommitText(context, editCookie, state.commit);
        if (FAILED(hr)) return hr;
        _logicalComposition = InputRouting::LogicalAfterState(false, state.sequence, _sequence);
    }

    if (!state.composition.empty())
    {
        hr = StartOrUpdateComposition(context, editCookie, state.composition, state.caret);
        if (SUCCEEDED(hr)) _logicalComposition = true;
        return hr;
    }

    if (_composition != nullptr && _compositionContext == context)
    {
        hr = EndComposition(context, editCookie, state.commit.empty());
    }
    if (SUCCEEDED(hr)) _logicalComposition = InputRouting::LogicalAfterState(false, state.sequence, _sequence);
    return hr;
}

HRESULT NativeCandidate::StartOrUpdateComposition(ITfContext* context, TfEditCookie editCookie,
                                                  const std::wstring& text, int caret)
{
    if (_composition == nullptr || _compositionContext != context)
    {
        if (_composition != nullptr)
            SafeRelease(_composition);
        SafeRelease(_compositionContext);

        ITfInsertAtSelection* insertion = nullptr;
        HRESULT hr = context->QueryInterface(IID_PPV_ARGS(&insertion));
        if (FAILED(hr)) return hr;
        ITfRange* range = nullptr;
        hr = insertion->InsertTextAtSelection(editCookie, TF_IAS_QUERYONLY_VALUE, L"", 0, &range);
        insertion->Release();
        if (FAILED(hr) || range == nullptr)
        {
            SafeRelease(range);
            return FAILED(hr) ? hr : E_FAIL;
        }

        ITfContextComposition* compositionApi = nullptr;
        hr = context->QueryInterface(IID_PPV_ARGS(&compositionApi));
        if (SUCCEEDED(hr))
        {
            hr = compositionApi->StartComposition(editCookie, range,
                                                   static_cast<ITfCompositionSink*>(this),
                                                   &_composition);
            compositionApi->Release();
        }
        range->Release();
        if (FAILED(hr) || _composition == nullptr)
        {
            SafeRelease(_composition);
            return FAILED(hr) ? hr : E_FAIL;
        }
        _compositionContext = context;
        _compositionContext->AddRef();
    }

    ITfRange* range = nullptr;
    HRESULT hr = _composition->GetRange(&range);
    if (FAILED(hr) || range == nullptr)
    {
        SafeRelease(range);
        return FAILED(hr) ? hr : E_FAIL;
    }
    hr = range->SetText(editCookie, 0, text.c_str(), static_cast<LONG>(text.size()));
    const HRESULT textResult = hr;
    if (SUCCEEDED(hr))
    {
        _displayedText = text;
        _displayedCaret = std::clamp(caret, 0, static_cast<int>(text.size()));
        _logicalComposition = !text.empty();
        ITfRange* selectionRange = nullptr;
        hr = range->Clone(&selectionRange);
        if (SUCCEEDED(hr)) hr = selectionRange->Collapse(editCookie, TF_ANCHOR_START);
        LONG shifted = 0;
        if (SUCCEEDED(hr)) hr = selectionRange->ShiftEnd(editCookie,
            std::clamp(caret, 0, static_cast<int>(text.size())), &shifted, nullptr);
        if (SUCCEEDED(hr)) hr = selectionRange->Collapse(editCookie, TF_ANCHOR_END);
        if (SUCCEEDED(hr))
        {
            TF_SELECTION selection{selectionRange, {TF_AE_NONE, FALSE}};
            hr = context->SetSelection(editCookie, 1, &selection);
        }
        SafeRelease(selectionRange);
    }
    range->Release();
    return textResult;
}

HRESULT NativeCandidate::CommitText(ITfContext* context, TfEditCookie editCookie,
                                    const std::wstring& text)
{
    if (text.empty()) return S_OK;
    if (_composition != nullptr && _compositionContext == context)
    {
        ITfRange* range = nullptr;
        HRESULT hr = _composition->GetRange(&range);
        if (FAILED(hr) || range == nullptr)
        {
            SafeRelease(range);
            return FAILED(hr) ? hr : E_FAIL;
        }
        hr = range->SetText(editCookie, 0, text.c_str(), static_cast<LONG>(text.size()));
        if (SUCCEEDED(hr))
        {
            _displayedText = text;
            _displayedCaret = static_cast<int>(text.size());
            MoveCaretToEnd(context, editCookie, range);
        }
        range->Release();
        if (FAILED(hr)) return hr;
        _endingOwnComposition = true;
        hr = _composition->EndComposition(editCookie);
        _endingOwnComposition = false;
        if (SUCCEEDED(hr))
        {
            SafeRelease(_composition);
            SafeRelease(_compositionContext);
            _displayedText.clear(); _displayedCaret = 0;
        }
        return hr;
    }

    ITfInsertAtSelection* insertion = nullptr;
    HRESULT hr = context->QueryInterface(IID_PPV_ARGS(&insertion));
    if (FAILED(hr)) return hr;
    ITfRange* inserted = nullptr;
    hr = insertion->InsertTextAtSelection(editCookie, 0, text.c_str(),
                                          static_cast<LONG>(text.size()), &inserted);
    SafeRelease(inserted);
    insertion->Release();
    return hr;
}

HRESULT NativeCandidate::EndComposition(ITfContext* context, TfEditCookie editCookie, bool clearText)
{
    if (_composition == nullptr || _compositionContext != context) return S_OK;
    HRESULT hr = S_OK;
    ITfRange* range = nullptr;
    if (clearText)
    {
        hr = _composition->GetRange(&range);
        if (SUCCEEDED(hr) && range != nullptr)
            hr = range->SetText(editCookie, 0, L"", 0);
        SafeRelease(range);
    }
    if (SUCCEEDED(hr))
    {
        _endingOwnComposition = true;
        hr = _composition->EndComposition(editCookie);
        _endingOwnComposition = false;
    }
    if (SUCCEEDED(hr)) ResetCompositionState();
    return hr;
}

HRESULT NativeCandidate::MoveCaretToEnd(ITfContext* context, TfEditCookie editCookie, ITfRange* range)
{
    if (context == nullptr || range == nullptr) return E_INVALIDARG;
    ITfRange* caret = nullptr;
    HRESULT hr = range->Clone(&caret);
    if (SUCCEEDED(hr)) hr = caret->Collapse(editCookie, TF_ANCHOR_END);
    if (SUCCEEDED(hr))
    {
        TF_SELECTION selection{};
        selection.range = caret;
        selection.style.ase = TF_AE_NONE;
        selection.style.fInterimChar = FALSE;
        hr = context->SetSelection(editCookie, 1, &selection);
    }
    SafeRelease(caret);
    return hr;
}

// -----------------------------------------------------------------------------
// Isolated registration.  DllRegisterServer writes only this candidate's
// HKLM COM key and TSF profile; it never changes the default active profile.
// The supplied PowerShell scripts call this export explicitly and are not run
// during the build.
// -----------------------------------------------------------------------------

HRESULT NativeCandidate::RegisterServer()
{
    if (g_instance == nullptr) return E_UNEXPECTED;
    wchar_t modulePath[MAX_PATH * 4]{};
    DWORD length = GetModuleFileNameW(g_instance, modulePath, ARRAYSIZE(modulePath));
    if (length == 0 || length >= ARRAYSIZE(modulePath)) return HRESULT_FROM_WIN32(GetLastError());

    const std::wstring clsid = GuidTextWide(CLSID_NativeCandidate);
    const std::wstring root = L"Software\\Classes\\CLSID\\" + clsid;
    HRESULT hr = SetRegString(HKEY_LOCAL_MACHINE, root, nullptr, L"Claude 中文输入法");
    if (FAILED(hr)) return hr;
    hr = SetRegString(HKEY_LOCAL_MACHINE, root + L"\\InprocServer32", nullptr, modulePath);
    if (FAILED(hr)) return hr;
    hr = SetRegString(HKEY_LOCAL_MACHINE, root + L"\\InprocServer32", L"ThreadingModel", L"Apartment");
    if (FAILED(hr)) return hr;

    ITfInputProcessorProfiles* profiles = nullptr;
    hr = CoCreateInstance(CLSID_TF_InputProcessorProfiles, nullptr,
                          CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&profiles));
    if (FAILED(hr)) return hr;
    GUID clsidValue = CLSID_NativeCandidate;
    GUID profileValue = GUID_NativeCandidateProfile;
    hr = profiles->Register(clsidValue);
    if (SUCCEEDED(hr) || hr == TF_E_ALREADY_EXISTS)
    {
        hr = profiles->AddLanguageProfile(clsidValue, 0x0804, profileValue,
                                          L"Claude 中文输入法",
                                          static_cast<ULONG>(wcslen(L"Claude 中文输入法")),
                                          nullptr, 0, 0);
        if (hr == TF_E_ALREADY_EXISTS) hr = S_OK;
    }
    if (SUCCEEDED(hr))
    {
        // Enable the installed profile. This is separate from selecting the
        // user's default keyboard (SetDefaultLanguageProfile).
        hr = profiles->EnableLanguageProfile(clsidValue, 0x0804, profileValue, TRUE);
    }
    if (SUCCEEDED(hr))
        hr = profiles->EnableLanguageProfileByDefault(clsidValue, 0x0804, profileValue, TRUE);
    profiles->Release();
    if (FAILED(hr)) return hr;

    ITfCategoryMgr* categories = nullptr;
    hr = CoCreateInstance(CLSID_TF_CategoryMgr, nullptr, CLSCTX_INPROC_SERVER,
                          IID_PPV_ARGS(&categories));
    if (FAILED(hr)) return hr;
    // CoreText hosts (including Windows Terminal) filter desktop-only TIPs.
    // COM-less activation uses this DLL's DllGetClassObject export. Do not
    // advertise UIElement/search support, which this implementation lacks.
    const GUID supportedCategories[] = {
        GUID_TFCAT_TIP_KEYBOARD,
        GUID_TFCAT_TIPCAP_COMLESS,
        GUID_TFCAT_TIPCAP_IMMERSIVESUPPORT,
        GUID_TFCAT_TIPCAP_SYSTRAYSUPPORT
    };
    for (const auto& category : supportedCategories)
    {
        hr = categories->RegisterCategory(clsidValue, category, clsidValue);
        if (hr == TF_E_ALREADY_EXISTS) hr = S_OK;
        if (FAILED(hr)) break;
    }
    categories->Release();
    return hr;
}

HRESULT NativeCandidate::UnregisterServer()
{
    GUID clsidValue = CLSID_NativeCandidate;
    GUID profileValue = GUID_NativeCandidateProfile;
    ITfCategoryMgr* categories = nullptr;
    if (SUCCEEDED(CoCreateInstance(CLSID_TF_CategoryMgr, nullptr, CLSCTX_INPROC_SERVER,
                                   IID_PPV_ARGS(&categories))) && categories != nullptr)
    {
        const GUID supportedCategories[] = {
            GUID_TFCAT_TIP_KEYBOARD,
            GUID_TFCAT_TIPCAP_COMLESS,
            GUID_TFCAT_TIPCAP_IMMERSIVESUPPORT,
            GUID_TFCAT_TIPCAP_SYSTRAYSUPPORT
        };
        for (const auto& category : supportedCategories)
            categories->UnregisterCategory(clsidValue, category, clsidValue);
        categories->Release();
    }
    ITfInputProcessorProfiles* profiles = nullptr;
    if (SUCCEEDED(CoCreateInstance(CLSID_TF_InputProcessorProfiles, nullptr,
                                   CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&profiles))) && profiles != nullptr)
    {
        profiles->RemoveLanguageProfile(clsidValue, 0x0804, profileValue);
        profiles->Unregister(clsidValue);
        profiles->Release();
    }
    DeleteRegTree(HKEY_LOCAL_MACHINE, L"Software\\Classes\\CLSID\\" + GuidTextWide(CLSID_NativeCandidate));
    return S_OK;
}

extern "C" BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_instance = instance;
        DisableThreadLibraryCalls(instance);
    }
    return TRUE;
}

STDAPI DllCanUnloadNow()
{
    return g_objects.load() == 0 && g_locks.load() == 0 ? S_OK : S_FALSE;
}

STDAPI DllGetClassObject(REFCLSID clsid, REFIID riid, void** object)
{
    if (object == nullptr) return E_POINTER;
    *object = nullptr;
    if (clsid != CLSID_NativeCandidate) return CLASS_E_CLASSNOTAVAILABLE;
    auto* factory = new (std::nothrow) NativeCandidate::ClassFactory();
    if (factory == nullptr) return E_OUTOFMEMORY;
    HRESULT hr = factory->QueryInterface(riid, object);
    factory->Release();
    return hr;
}

STDAPI DllRegisterServer()
{
    HRESULT hr = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    const bool initialized = SUCCEEDED(hr);
    HRESULT result = NativeCandidate::RegisterServer();
    if (initialized) CoUninitialize();
    return result;
}

STDAPI DllUnregisterServer()
{
    HRESULT hr = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    const bool initialized = SUCCEEDED(hr);
    HRESULT result = NativeCandidate::UnregisterServer();
    if (initialized) CoUninitialize();
    return result;
}

