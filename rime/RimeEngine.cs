#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace ClaudeZhInput.Rime;

/// <summary>Paths used by the standalone Rime instance.</summary>
public sealed record RimeEngineOptions
{
    public required string RuntimeDirectory { get; init; }
    public string SharedDataDirectory => Path.Combine(RuntimeDirectory, "data");
    public string? UserDataDirectory { get; init; }
    public string? StagingDirectory { get; init; }
    public string? PrebuiltDataDirectory { get; init; }
    public string SchemaId { get; init; } = "claude_pinyin";
    public string AppName { get; init; } = "rime.claude_zh_input";
    public int MinimumLogLevel { get; init; } = 2;

    internal string EffectiveUserDataDirectory => UserDataDirectory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClaudeZhInput", "rime");

    internal string EffectiveStagingDirectory => StagingDirectory ?? Path.Combine(
        EffectiveUserDataDirectory, "build");

    internal string EffectivePrebuiltDataDirectory => PrebuiltDataDirectory ?? Path.Combine(
        SharedDataDirectory, "build");
}

public sealed record RimeCandidate(string Text, string Comment);

/// <summary>
/// A copy of the Rime session state. All strings and candidates are copied
/// before librime's context is freed.
/// </summary>
public sealed record RimeSessionState(
    bool Handled,
    string Commit,
    string Preedit,
    IReadOnlyList<RimeCandidate> Candidates,
    int SelectedIndex,
    int PageNo,
    int PageSize,
    int? PageCount,
    bool IsLastPage,
    string SelectKeys,
    int CaretUtf16,
    int SelectionStartUtf16,
    int SelectionEndUtf16,
    bool IsAsciiMode)
{
    public bool HasPreviousPage => PageNo > 0;
    public bool HasNextPage => !IsLastPage;

    public static RimeSessionState Empty(bool handled = false, string? commit = null)
        => new(handled, commit ?? string.Empty, string.Empty,
            Array.Empty<RimeCandidate>(), -1, 0, 0, 0, true, string.Empty,
            0, 0, 0, false);
}

/// <summary>
/// Process-wide librime owner. Rime has process-global initialization; this
/// class permits multiple independent sessions while serializing native calls.
/// </summary>
public sealed class RimeEngine : IDisposable
{
    private static readonly object SingletonGate = new();
    private static RimeEngine? _singleton;

    private readonly object _gate = new();
    private readonly RimeEngineOptions _options;
    private readonly RimeApiLayout _api;
    private readonly RimeNative.SetupDelegate _setup;
    private readonly RimeNative.InitializeDelegate _initialize;
    private readonly RimeNative.FinalizeDelegate _finalize;
    private readonly RimeNative.CreateSessionDelegate _createSession;
    private readonly RimeNative.SessionDelegate _destroySession;
    private readonly RimeNative.ProcessKeyDelegate _processKey;
    private readonly RimeNative.CommitDelegate _commitComposition;
    private readonly RimeNative.ClearCompositionDelegate _clearComposition;
    private readonly RimeNative.GetCommitDelegate _getCommit;
    private readonly RimeNative.FreeCommitDelegate _freeCommit;
    private readonly RimeNative.GetContextDelegate _getContext;
    private readonly RimeNative.FreeContextDelegate _freeContext;
    private readonly RimeNative.SelectCandidateDelegate _selectCandidate;
    private readonly RimeNative.ChangePageDelegate _changePage;
    private readonly RimeNative.SelectSchemaDelegate _selectSchema;
    private readonly RimeNative.SetOptionDelegate _setOption;
    private readonly RimeNative.GetOptionDelegate _getOption;
    private readonly HashSet<UIntPtr> _sessions = new();
    private readonly List<IntPtr> _traitStrings = new();
    private bool _disposed;

    private RimeEngine(RimeEngineOptions options)
    {
        _options = options;
        Directory.CreateDirectory(options.SharedDataDirectory);
        Directory.CreateDirectory(options.EffectiveUserDataDirectory);
        Directory.CreateDirectory(options.EffectiveStagingDirectory);
        Directory.CreateDirectory(options.EffectivePrebuiltDataDirectory);

        string dllPath = Path.Combine(options.RuntimeDirectory, "rime.dll");
        RimeNative.Configure(dllPath);
        _api = RimeNative.LoadApi();

        _setup = RimeNative.Function<RimeNative.SetupDelegate>(_api.Setup);
        _initialize = RimeNative.Function<RimeNative.InitializeDelegate>(_api.Initialize);
        _finalize = RimeNative.Function<RimeNative.FinalizeDelegate>(_api.Finalize);
        _createSession = RimeNative.Function<RimeNative.CreateSessionDelegate>(_api.CreateSession);
        _destroySession = RimeNative.Function<RimeNative.SessionDelegate>(_api.DestroySession);
        _processKey = RimeNative.Function<RimeNative.ProcessKeyDelegate>(_api.ProcessKey);
        _commitComposition = RimeNative.Function<RimeNative.CommitDelegate>(_api.CommitComposition);
        _clearComposition = RimeNative.Function<RimeNative.ClearCompositionDelegate>(_api.ClearComposition);
        _getCommit = RimeNative.Function<RimeNative.GetCommitDelegate>(_api.GetCommit);
        _freeCommit = RimeNative.Function<RimeNative.FreeCommitDelegate>(_api.FreeCommit);
        _getContext = RimeNative.Function<RimeNative.GetContextDelegate>(_api.GetContext);
        _freeContext = RimeNative.Function<RimeNative.FreeContextDelegate>(_api.FreeContext);
        _selectCandidate = RimeNative.Function<RimeNative.SelectCandidateDelegate>(
            _api.SelectCandidateOnCurrentPage);
        _changePage = RimeNative.Function<RimeNative.ChangePageDelegate>(_api.ChangePage);
        _selectSchema = RimeNative.Function<RimeNative.SelectSchemaDelegate>(_api.SelectSchema);
        _setOption = RimeNative.Function<RimeNative.SetOptionDelegate>(_api.SetOption);
        _getOption = RimeNative.Function<RimeNative.GetOptionDelegate>(_api.GetOption);

        InitializeNative();
    }

    /// <summary>Open the one process-wide engine instance.</summary>
    public static RimeEngine Open(RimeEngineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        lock (SingletonGate)
        {
            if (_singleton is not null)
            {
                if (!string.Equals(
                        Path.GetFullPath(_singleton._options.RuntimeDirectory),
                        Path.GetFullPath(options.RuntimeDirectory),
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        "A different librime runtime cannot be opened in the same process.");
                return _singleton;
            }

            _singleton = new RimeEngine(options);
            return _singleton;
        }
    }

    public string Version
    {
        get
        {
            lock (_gate)
            {
                EnsureOpen();
                var getVersion = RimeNative.Function<RimeNative.GetVersionDelegate>(_api.GetVersion);
                return RimeNative.ReadUtf8(getVersion());
            }
        }
    }

    public RimeSession CreateSession()
    {
        lock (_gate)
        {
            EnsureOpen();
            UIntPtr id = _createSession();
            if (id == UIntPtr.Zero)
                throw new InvalidOperationException("librime could not create a session.");

            _sessions.Add(id);
            try
            {
                using var schema = Utf8Buffer.From(_options.SchemaId);
                if (_selectSchema(id, schema.Pointer) == 0)
                    throw new InvalidOperationException(
                        $"librime schema '{_options.SchemaId}' is unavailable.");
                return new RimeSession(this, id);
            }
            catch
            {
                _destroySession(id);
                _sessions.Remove(id);
                throw;
            }
        }
    }

    /// <summary>Return the X11 keysym used by librime for a named key.</summary>
    public int Keycode(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        lock (_gate)
        {
            EnsureOpen();
            return RimeNative.GetKeycodeByName(name);
        }
    }

    public int Modifier(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        lock (_gate)
        {
            EnsureOpen();
            return RimeNative.GetModifierByName(name);
        }
    }

    internal RimeSessionState ProcessKey(RimeSession session, int keycode, int modifiers)
    {
        lock (_gate)
        {
            EnsureSession(session);
            bool handled = _processKey(session.Id, keycode, modifiers) != 0;
            return ReadState(session.Id, handled);
        }
    }

    internal RimeSessionState SelectCandidate(RimeSession session, int index)
    {
        if (index < 0)
            throw new ArgumentOutOfRangeException(nameof(index));
        lock (_gate)
        {
            EnsureSession(session);
            bool handled = _selectCandidate(session.Id, (UIntPtr)index) != 0;
            return ReadState(session.Id, handled);
        }
    }

    internal RimeSessionState ChangePage(RimeSession session, bool backward)
    {
        lock (_gate)
        {
            EnsureSession(session);
            bool handled = _changePage(session.Id, backward ? 1 : 0) != 0;
            return ReadState(session.Id, handled);
        }
    }

    internal RimeSessionState ClearComposition(RimeSession session)
    {
        lock (_gate)
        {
            EnsureSession(session);
            _clearComposition(session.Id);
            return ReadState(session.Id, true);
        }
    }

    internal RimeSessionState CommitComposition(RimeSession session)
    {
        lock (_gate)
        {
            EnsureSession(session);
            bool handled = _commitComposition(session.Id) != 0;
            return ReadState(session.Id, handled);
        }
    }

    internal RimeSessionState SetOption(RimeSession session, string option, bool value)
    {
        lock (_gate)
        {
            EnsureSession(session);
            _setOption(session.Id, option, value ? 1 : 0);
            return ReadState(session.Id, true);
        }
    }

    internal RimeSessionState Snapshot(RimeSession session)
    {
        lock (_gate)
        {
            EnsureSession(session);
            return ReadState(session.Id, false);
        }
    }

    internal void DestroySession(RimeSession session)
    {
        lock (_gate)
        {
            if (_disposed || !_sessions.Remove(session.Id))
                return;
            _destroySession(session.Id);
            session.MarkDisposed();
        }
    }

    private void InitializeNative()
    {
        var traits = new RimeTraits
        {
            DataSize = RimeNative.StructDataSize<RimeTraits>(),
            SharedDataDir = KeepTrait(_options.SharedDataDirectory),
            UserDataDir = KeepTrait(_options.EffectiveUserDataDirectory),
            DistributionName = KeepTrait("ClaudeZhInput"),
            DistributionCodeName = KeepTrait("claude_zh_input"),
            DistributionVersion = KeepTrait("rime-1.17.0"),
            AppName = KeepTrait(_options.AppName),
            Modules = IntPtr.Zero,
            MinLogLevel = _options.MinimumLogLevel,
            LogDir = KeepTrait(Path.Combine(_options.EffectiveUserDataDirectory, "log")),
            PrebuiltDataDir = KeepTrait(_options.EffectivePrebuiltDataDirectory),
            StagingDir = KeepTrait(_options.EffectiveStagingDirectory),
        };
        Directory.CreateDirectory(Path.Combine(_options.EffectiveUserDataDirectory, "log"));
        _setup(ref traits);
        _initialize(ref traits);
    }

    private IntPtr KeepTrait(string value)
    {
        IntPtr pointer = Marshal.StringToCoTaskMemUTF8(value);
        _traitStrings.Add(pointer);
        return pointer;
    }

    private RimeSessionState ReadState(UIntPtr id, bool handled)
    {
        string commit = ReadCommit(id);
        var context = new RimeContext
        {
            DataSize = RimeNative.StructDataSize<RimeContext>(),
        };
        if (_getContext(id, ref context) == 0)
            return RimeSessionState.Empty(handled, commit) with
            {
                IsAsciiMode = ReadOption(id, "ascii_mode"),
            };

        try
        {
            string preedit = RimeNative.ReadUtf8(context.Composition.Preedit);
            string selectKeys = RimeNative.ReadUtf8(context.Menu.SelectKeys);
            int caretUtf16 = Utf16Offset(preedit, context.Composition.CursorPos);
            int selectionStartUtf16 = Utf16Offset(preedit, context.Composition.SelStart);
            int selectionEndUtf16 = Utf16Offset(preedit, context.Composition.SelEnd);
            int count = Math.Clamp(context.Menu.NumCandidates, 0, 4096);
            int candidateSize = Marshal.SizeOf<RimeCandidateNative>();
            var candidates = new RimeCandidateNative[count];
            for (int i = 0; i < count; i++)
            {
                IntPtr address = IntPtr.Add(context.Menu.Candidates, i * candidateSize);
                candidates[i] = Marshal.PtrToStructure<RimeCandidateNative>(address);
            }

            var copied = candidates.Select(static candidate => new RimeCandidate(
                RimeNative.ReadUtf8(candidate.Text),
                RimeNative.ReadUtf8(candidate.Comment))).ToArray();
            int pageNo = Math.Max(0, context.Menu.PageNo);
            int pageSize = Math.Max(0, context.Menu.PageSize);
            bool last = context.Menu.IsLastPage != 0;
            int? pageCount = last ? pageNo + 1 : null;
            return new RimeSessionState(
                handled,
                commit,
                preedit,
                copied,
                context.Menu.HighlightedCandidateIndex,
                pageNo,
                pageSize,
                pageCount,
                last,
                selectKeys,
                caretUtf16,
                selectionStartUtf16,
                selectionEndUtf16,
                ReadOption(id, "ascii_mode"));
        }
        finally
        {
            _freeContext(ref context);
        }
    }

    private string ReadCommit(UIntPtr id)
    {
        var commit = new RimeCommit
        {
            DataSize = RimeNative.StructDataSize<RimeCommit>(),
        };
        if (_getCommit(id, ref commit) == 0)
            return string.Empty;
        try
        {
            return RimeNative.ReadUtf8(commit.Text);
        }
        finally
        {
            _freeCommit(ref commit);
        }
    }

    private bool ReadOption(UIntPtr id, string option)
    {
        using var value = Utf8Buffer.From(option);
        return _getOption(id, value.Pointer) != 0;
    }

    // Rime's composition positions are offsets in the UTF-8 preedit string.
    // The TSF bridge consumes UTF-16 code-unit positions, so stop only at a
    // complete UTF-8 scalar and count its UTF-16 width.
    private static int Utf16Offset(string text, int utf8Offset)
    {
        if (utf8Offset <= 0 || text.Length == 0)
            return 0;

        int byteCount = 0;
        int utf16Count = 0;
        foreach (Rune rune in text.EnumerateRunes())
        {
            int runeBytes = Encoding.UTF8.GetByteCount(rune.ToString());
            if (byteCount + runeBytes > utf8Offset)
                break;
            byteCount += runeBytes;
            utf16Count += rune.Utf16SequenceLength;
        }
        return utf16Count;
    }

    private void EnsureSession(RimeSession session)
    {
        EnsureOpen();
        if (session.IsDisposed || !_sessions.Contains(session.Id))
            throw new ObjectDisposedException(nameof(RimeSession));
    }

    private void EnsureOpen()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(RimeEngine));
    }

    public void Dispose()
    {
        lock (SingletonGate)
        {
            lock (_gate)
            {
                if (_disposed)
                    return;
                foreach (UIntPtr session in _sessions.ToArray())
                    _destroySession(session);
                _sessions.Clear();
                _finalize();
                foreach (IntPtr pointer in _traitStrings)
                    Marshal.FreeCoTaskMem(pointer);
                _traitStrings.Clear();
                _disposed = true;
                if (ReferenceEquals(_singleton, this))
                    _singleton = null;
            }
        }
    }

    internal sealed class Utf8Buffer : IDisposable
    {
        private Utf8Buffer(IntPtr pointer) => Pointer = pointer;
        internal IntPtr Pointer { get; }

        internal static Utf8Buffer From(string value)
            => new(Marshal.StringToCoTaskMemUTF8(value));

        public void Dispose() => Marshal.FreeCoTaskMem(Pointer);
    }
}

public sealed class RimeSession : IDisposable
{
    private readonly RimeEngine _engine;
    private bool _disposed;

    internal RimeSession(RimeEngine engine, UIntPtr id)
    {
        _engine = engine;
        Id = id;
    }

    internal UIntPtr Id { get; }
    internal bool IsDisposed => _disposed;

    public RimeSessionState ProcessKey(int keycode, int modifiers = 0)
    {
        EnsureOpen();
        return _engine.ProcessKey(this, keycode, modifiers);
    }

    /// <summary>Convenience method for tests and console clients.</summary>
    public RimeSessionState ProcessAscii(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        RimeSessionState state = RimeSessionState.Empty();
        foreach (char character in text)
        {
            if (character > 0x7f)
                throw new ArgumentException("ProcessAscii accepts ASCII input only.", nameof(text));
            bool shift = char.IsLetter(character) && char.IsUpper(character);
            char lower = char.ToLowerInvariant(character);
            int keycode = lower == ' ' ? _engine.Keycode("space") : _engine.Keycode(lower.ToString());
            int mask = shift ? _engine.Modifier("Shift") : 0;
            state = ProcessKey(keycode, mask);
        }
        return state;
    }

    public RimeSessionState SelectCandidate(int index)
    {
        EnsureOpen();
        return _engine.SelectCandidate(this, index);
    }

    public RimeSessionState ChangePage(bool backward = false)
    {
        EnsureOpen();
        return _engine.ChangePage(this, backward);
    }

    public RimeSessionState ClearComposition()
    {
        EnsureOpen();
        return _engine.ClearComposition(this);
    }

    public RimeSessionState CommitComposition()
    {
        EnsureOpen();
        return _engine.CommitComposition(this);
    }

    public RimeSessionState SetOption(string option, bool value)
    {
        ArgumentException.ThrowIfNullOrEmpty(option);
        EnsureOpen();
        return _engine.SetOption(this, option, value);
    }

    public RimeSessionState Snapshot()
    {
        EnsureOpen();
        return _engine.Snapshot(this);
    }

    internal void MarkDisposed() => _disposed = true;

    private void EnsureOpen()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(RimeSession));
    }

    public void Dispose() => _engine.DestroySession(this);
}

