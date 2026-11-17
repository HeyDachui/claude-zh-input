#pragma once

#include <windows.h>
#include <msctf.h>
#include "ServiceStartup.h"

#include <atomic>
#include <condition_variable>
#include <deque>
#include <functional>
#include <memory>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

// This is an isolated native TIP.  It deliberately has a different identity
// from the managed TIP in native/ so the two registrations can be tested and
// removed independently.
inline constexpr CLSID CLSID_NativeCandidate =
    {0xb763d5f3, 0x0f9e, 0x47d2, {0x8c, 0xd8, 0x7c, 0xcb, 0xfa, 0x6c, 0x3a, 0x41}};
inline constexpr GUID GUID_NativeCandidateProfile =
    {0x56e8b5b8, 0x99e6, 0x4c0b, {0xaf, 0x16, 0x33, 0xc5, 0x80, 0x5f, 0xc3, 0xd2}};

class NativeCandidate;

struct EngineState
{
    bool local = false;
    long long sequence = 0;
    std::string requestId;
    bool handled = false;
    std::wstring composition;
    std::wstring commit;
    std::string commitId;
    int caret = 0;
    std::string mode = "normal";
    bool pendingTranslation = false;
    bool asciiMode = false;
    int selectedIndex = 0;
    int page = 0;
    int pageCount = 0;
    bool isLastPage = true;
    std::vector<std::wstring> candidates;
};

class PipeClient final
{
public:
    explicit PipeClient(NativeCandidate* owner, std::string pipeName);
    ~PipeClient();

    void Start();
    void Stop();
    bool Send(std::string line);
    bool Ready() const { return _ready; }
    bool AcceptingInput() const { return _ready || (!_stop && GetTickCount64() < _startingUntil.load()); }
    unsigned long DisconnectVersion() const { return _disconnectVersion; }
    void ResetConnection();
    void RequestServiceStart();
    void CancelServiceStart();

private:
    friend struct NativeRecoveryTests;
    void Run();
    bool Connect();
    void Disconnect();
    bool WriteLine(const std::string& line);
    void ConsumeAvailable();

    NativeCandidate* _owner;
    std::string _pipeName;
    std::atomic<bool> _stop{false};
    std::atomic<bool> _startRequested{false};
    std::atomic<bool> _ready{false};
    std::atomic<bool> _resetRequested{false};
    std::atomic<unsigned long> _disconnectVersion{0};
    std::atomic<ULONGLONG> _startingUntil{0};
    std::thread _thread;
    std::mutex _gate;
    std::condition_variable _wake;
    std::deque<std::string> _outgoing;
    HANDLE _pipe = INVALID_HANDLE_VALUE;
    bool _helloPending = false;
    std::string _incoming;
};

class NativeCandidate final : public ITfTextInputProcessorEx,
                              public ITfKeyEventSink,
                              public ITfCompositionSink,
                              public ITfActiveLanguageProfileNotifySink
{
public:
    NativeCandidate();
    ~NativeCandidate();
    class ClassFactory;

    // IUnknown
    STDMETHODIMP QueryInterface(REFIID riid, void** object) override;
    STDMETHODIMP_(ULONG) AddRef() override;
    STDMETHODIMP_(ULONG) Release() override;

    // ITfTextInputProcessor / ITfTextInputProcessorEx
    STDMETHODIMP Activate(ITfThreadMgr* threadMgr, TfClientId clientId) override;
    STDMETHODIMP Deactivate() override;
    STDMETHODIMP ActivateEx(ITfThreadMgr* threadMgr, TfClientId clientId, DWORD flags) override;

    // ITfKeyEventSink
    STDMETHODIMP OnSetFocus(BOOL foreground) override;
    STDMETHODIMP OnTestKeyDown(ITfContext* context, WPARAM wParam, LPARAM lParam, BOOL* eaten) override;
    STDMETHODIMP OnTestKeyUp(ITfContext* context, WPARAM wParam, LPARAM lParam, BOOL* eaten) override;
    STDMETHODIMP OnKeyDown(ITfContext* context, WPARAM wParam, LPARAM lParam, BOOL* eaten) override;
    STDMETHODIMP OnKeyUp(ITfContext* context, WPARAM wParam, LPARAM lParam, BOOL* eaten) override;
    STDMETHODIMP OnPreservedKey(ITfContext* context, REFGUID guid, BOOL* eaten) override;

    // ITfCompositionSink
    STDMETHODIMP OnCompositionTerminated(TfEditCookie editCookie, ITfComposition* composition) override;
    STDMETHODIMP OnActivated(REFCLSID clsid, REFGUID profile, BOOL activated) override;

    // Called only by PipeClient's worker thread.  It never touches a TSF
    // context; it parses a line and posts a small owned packet to _wakeWindow.
    void OnPipeLine(const std::string& line);
    std::string HelloLine() const;

    static LRESULT CALLBACK WakeWindowProc(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam);

    static HRESULT RegisterServer();
    static HRESULT UnregisterServer();

private:
    class EditSession;

    friend class PipeClient;
    friend class EditSession;
    friend struct NativeRecoveryTests;
    struct PendingApply;
    struct RetiredComposition;
    struct PreviewKey { long long sequence; UINT keysym; bool clear = false; ULONGLONG acceptedAt = GetTickCount64(); };

    HRESULT ActivateOnThread(ITfThreadMgr* threadMgr, TfClientId clientId, DWORD flags);
    void CheckSelectedProfile();
    void ResetCompositionState();
    bool IsEnabledContext(ITfContext* context) const;
    bool ShouldEatKey(ITfContext* context, UINT key) const;
    bool HandleKey(ITfContext* context, UINT key, LPARAM flags, BOOL* eaten);
    bool HandleCommandKey(ITfContext* context, UINT key, LPARAM flags);
    bool HandleAsciiKey(ITfContext* context, UINT key, LPARAM flags);
    bool IsLogicalComposition() const;
    bool IsShift(UINT key) const;
    bool IsLetter(UINT key) const;
    bool IsCandidateKey(UINT key) const;
    bool IsSpecialKey(UINT key) const;
    bool IsInnerPunctuation(UINT key) const;
    bool IsSentencePunctuation(UINT key) const;
    bool HasCtrl() const;
    bool HasAlt() const;
    bool HasWin() const;
    DWORD Modifiers() const;
    UINT RimeKey(UINT key, DWORD modifiers) const;
    std::string NewRequestId();
    bool SendEngineKey(UINT key, DWORD modifiers);
    void SendEngineCancel();
    void SendEngineReset();
    void CaptureCaret();
    bool SendEnginePage(bool backward);
    void SendEngineSelect(int index);
    void SendCommandQuery();
    void OpenToolbarCommands();
    void SendLiteralSemicolon(ITfContext* context);
    void QueueLocalCommit(std::wstring text, ITfContext* context);
    void PostState(EngineState* state);
    void ApplyPendingState();
    HRESULT ApplyTransaction(const std::shared_ptr<PendingApply>& item, TfEditCookie editCookie);
    HRESULT ApplyHostEdit(ITfContext* context, TfEditCookie editCookie, UINT key);
    void HandlePowerEvent(WPARAM event);
    void MaintainInputState();
    void AbandonInput(bool disconnect);
    void PreserveComposition();
    void FinishRetiredCompositions(bool synchronous = false);
    bool RetiredInContext(ITfContext* context) const;
    void ArmRetry();
    bool PreviewInput(UINT keysym, bool clear = false);
    static void EditPreview(std::wstring& text, int& caret, UINT keysym, bool clear);
    HRESULT ApplyState(ITfContext* context, TfEditCookie editCookie, const EngineState& state);
    HRESULT StartOrUpdateComposition(ITfContext* context, TfEditCookie editCookie, const std::wstring& text, int caret);
    HRESULT CommitText(ITfContext* context, TfEditCookie editCookie, const std::wstring& text);
    HRESULT EndComposition(ITfContext* context, TfEditCookie editCookie, bool clearText);
    HRESULT MoveCaretToEnd(ITfContext* context, TfEditCookie editCookie, ITfRange* range);
    HRESULT RequestEdit(ITfContext* context, std::function<HRESULT(TfEditCookie)> action,
                        DWORD flags = TF_ES_ASYNCDONTCARE | TF_ES_READWRITE);
    bool FocusStillMatches(ITfContext* context) const;
    bool CompositionStillMatches(ITfContext* context, TfEditCookie cookie) const;
    void OnSelectMessage(int index);
    void OnLocalCommitMessage();
    void CreateWakeWindow();
    void DestroyWakeWindow();
    std::string LoadPipeName() const;
    std::wstring ModuleDirectory() const;
    void DiagnosticEvent(const char* event, long long first = 0,
                         long long second = 0, long long third = 0) const;
    void ReplaceContext(ITfContext* context);
    void ReleaseContext();

    std::atomic<ULONG> _references{1};
    mutable std::mutex _stateGate;
    ITfThreadMgr* _threadMgr = nullptr;
    ITfKeystrokeMgr* _keystrokeMgr = nullptr;
    ITfSource* _profileSource = nullptr;
    DWORD _profileCookie = TF_INVALID_COOKIE;
    ServiceStartup::SelectionWake _profileSelection;
    TfClientId _clientId = TF_CLIENTID_NULL;
    ITfContext* _context = nullptr;
    ITfComposition* _composition = nullptr;
    ITfContext* _compositionContext = nullptr;
    HWND _wakeWindow = nullptr;
    HPOWERNOTIFY _powerNotification = nullptr;
    bool _suspended = false;
    std::wstring _wakeClassName;
    std::string _sessionId;
    std::string _pipeName;
    std::string _mode = "normal";
    std::unique_ptr<PipeClient> _pipe;
    bool _active = false;
    std::atomic<bool> _continuousSentences{false};
    bool _focusLost = false;
    bool _logicalComposition = false;
    bool _asciiMode = false;
    bool _shiftDown = false;
    bool _shiftUsed = false;
    bool _commandPrefixPending = false;
    bool _commandActive = false;
    std::string _commandRequestId;
    std::string _commandText;
    long long _sequence = 0;
    long long _lastAppliedSequence = 0;
    long long _lastCommittedSequence = -1;
    std::string _lastCommittedRequest;
    long long _contextStartSequence = 0;
    bool _hasCaret = false;
    POINT _caret{};
    std::string _lastAppliedRequest;
    std::string _lastSentRequest;
    unsigned long long _contextGeneration = 0;
    ULONGLONG _awaitingSince = 0;
    long long _finalizationSequence = 0;
    unsigned long _seenDisconnect = 0;
    bool _endingOwnComposition = false;
    std::wstring _displayedText;
    int _displayedCaret = 0;
    std::deque<PreviewKey> _previewKeys;
    std::shared_ptr<PendingApply> _pendingEdit;
    std::vector<std::shared_ptr<RetiredComposition>> _retiredCompositions;
    unsigned long long _requestCounter = 0;
    std::deque<std::unique_ptr<EngineState>> _pendingStates;
    std::deque<std::pair<std::wstring, ITfContext*>> _localCommits;
    std::deque<std::wstring> _commandFills;
};
