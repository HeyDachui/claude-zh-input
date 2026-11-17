using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using ClaudeZhInput.Rime;

namespace ClaudeZhInput;

/// <summary>
/// The state sent back for protocol v2.  Candidates are copied text values;
/// the native side does not need to know librime's native structures.
/// </summary>
public sealed record RimeEngineState(
    long Sequence,
    string RequestId,
    bool Handled,
    string Composition,
    string Commit,
    IReadOnlyList<RimeCandidate> CandidateDetails,
    string Pinyin,
    int SelectedIndex,
    int Page,
    int? PageCount,
    bool IsLastPage,
    bool AsciiMode,
    string Mode,
    bool PendingTranslation,
    int? ScreenX,
    int? ScreenY)
{
    public bool LiteralWord { get; init; }
    public int Caret { get; init; }
    public string Sentence { get; init; } = "";
    public string CommitId { get; init; } = "";
}

/// <summary>
/// A state transition produced by a v2 message.  LegacyType is used only by
/// BridgeServer to keep the existing WPF candidate surface working while the
/// native client moves to engine-state messages.
/// </summary>
public sealed record RimeCoordinatorResult(
    RimeEngineState State,
    string LegacyType,
    string Source,
    IReadOnlyList<string> Warnings)
{
    public TranslationReview? Review { get; init; }
}

/// <summary>
/// Coordinates one native protocol-v2 connection with one librime session.
/// The RimeEngine is process-wide, but this class owns exactly one
/// RimeSession and all Claude sentence state for the connection.
/// </summary>
public sealed class RimeSessionCoordinator : IDisposable
{
    private const int ShiftMask = 1 << 0;
    private const int ControlMask = 1 << 2;
    private const int VkBack = 0x08;
    private const int VkReturn = 0x0d;
    private const int VkEscape = 0x1b;
    private const int X11Back = 0xff08;
    private const int X11Return = 0xff0d;
    private const int X11Escape = 0xff1b;
    private const int VkSpace = 0x20;
    private const int VkPageUp = 0xff55;
    private const int VkPageDown = 0xff56;
    private const int VkUp = 0xff52;
    private const int VkDown = 0xff54;
    private const int VkShiftLeft = 0xffe1;
    private const int VkShiftRight = 0xffe2;

    private readonly object gate = new();
    private readonly RimeSession session;
    private readonly OfflineTranslator translator;
    private readonly PhraseStore phrases;
    private sealed class PendingSentence(string source)
    {
        public string Source { get; } = source;
        public CancellationTokenSource Cancellation { get; } = new();
    }
    private readonly List<PendingSentence> translations = [];
    private Task translationTail = Task.CompletedTask;
    private bool sentenceSeparator;
    private long commitSequence;
    private bool disposed;
    private string mode;
    private string sentence = "";
    private string requestId = "";
    private long sequence;
    private bool codeMode;
    private bool asciiMode;
    private bool asciiModeBeforeCode;
    // An uppercase keysym is an explicit literal-word gesture in pinyin mode.
    // Lowercase letters remain Rime input; Space confirms this word.
    private string literalWord = "";
    private int literalCaret;
    private int sentenceCaret;
    private bool pendingTranslation => translations.Count > 0;
    private int? screenX;
    private int? screenY;

    public RimeSessionCoordinator(
        RimeEngine engine,
        OfflineTranslator translator,
        PhraseStore phrases,
        string initialMode = "normal")
    {
        ArgumentNullException.ThrowIfNull(engine);
        this.translator = translator ?? throw new ArgumentNullException(nameof(translator));
        this.phrases = phrases ?? throw new ArgumentNullException(nameof(phrases));
        session = engine.CreateSession();
        mode = NormalizeMode(initialMode);
    }

    /// <summary>Raised by a completed translation on a background thread.</summary>
    public event Action<RimeCoordinatorResult>? StateReady;
    // The bridge serializes completion mutations with key handling and wire
    // publication, so a completed prefix cannot be overtaken by a newer key.
    public Func<Action, Task>? DispatchCompletion { get; set; }

    public string Mode
    {
        get { lock (gate) return mode; }
    }

    public RimeCoordinatorResult Snapshot(long requestedSequence = 0, string? requestedRequestId = null)
    {
        lock (gate)
        {
            EnsureOpen();
            if (requestedSequence != 0) sequence = requestedSequence;
            if (requestedRequestId is not null) requestId = requestedRequestId;
            return BuildResultLocked(session.Snapshot(), false, "snapshot", "composition");
        }
    }

    public RimeCoordinatorResult ProcessKey(
        long requestedSequence,
        string requestedRequestId,
        int keycode,
        int modifiers,
        int? requestedScreenX = null,
        int? requestedScreenY = null)
    {
        lock (gate)
        {
            EnsureOpen();
            if ((modifiers & (ControlMask | (1 << 3) | (1 << 30))) != 0)
                return BuildResultLocked(session.Snapshot(), false, "shortcut", null);
            BeginVersionLocked(requestedSequence, requestedRequestId, requestedScreenX, requestedScreenY);

            if (IsEscapeKey(keycode))
                return CancelLocked("escape");

            if (mode == "claude")
            {
                if (keycode is >= 0x20 and <= 0x7e) translator.TouchIfRunning();
                // Keys inside the new tail do not cancel an earlier sentence.
                // Editing across its boundary restores the pending source and
                // invalidates the translations before applying that edit.
                if (pendingTranslation && literalWord.Length == 0 && session.Snapshot().Preedit.Length == 0 &&
                    (keycode == 0xff50 || ((IsBackspaceKey(keycode) || keycode == 0xff51) && sentenceCaret == 0)))
                    CancelTranslationLocked();
            }

            if (literalWord.Length > 0)
            {
                if (EditBuffer(ref literalWord, ref literalCaret, keycode))
                {
                    return BuildResultLocked(session.Snapshot(), true, "literal-edit", null);
                }
                if (keycode == VkSpace || IsShiftKey(keycode) || (mode == "normal" && IsReturnKey(keycode)))
                {
                    var word = TakeLiteralWordLocked();
                    if (mode == "claude") InsertSentenceLocked(word);
                    return BuildResultLocked(session.Snapshot(), true, "literal-confirm", null,
                        commitOverride: mode == "normal" ? word : "");
                }
                if (TryAsciiText(keycode, modifiers, out var literal))
                {
                    literalWord = literalWord.Insert(literalCaret, literal);
                    literalCaret += literal.Length;
                    return BuildResultLocked(session.Snapshot(), true, "literal-word", null);
                }
            }

            var before = session.Snapshot();
            if (literalWord.Length == 0 && keycode is >= 'A' and <= 'Z' &&
                !codeMode && !(mode == "claude" ? asciiMode : before.IsAsciiMode))
            {
                var commit = "";
                if (mode == "claude") AppendCurrentRimeTextLocked(before);
                else if (before.Preedit.Length > 0) commit = session.CommitComposition().Commit;
                literalWord = ((char)keycode).ToString();
                literalCaret = literalWord.Length;
                return BuildResultLocked(session.Snapshot(), true, "literal-start", null, commitOverride: commit);
            }

            if (mode == "claude")
                return ProcessClaudeKeyLocked(keycode, modifiers);

            if (IsShiftKey(keycode) && (modifiers & (ControlMask | (1 << 3))) == 0)
            {
                var current = session.Snapshot();
                var commit = current.Preedit.Length > 0 ? session.CommitComposition().Commit : "";
                var toggled = session.SetOption("ascii_mode", !current.IsAsciiMode);
                return BuildResultLocked(toggled, true, "ascii-toggle",
                    commit.Length > 0 ? "commit" : "composition", commitOverride: commit);
            }

            var state = session.ProcessKey(keycode, modifiers);
            // Keys queued before native receives an ASCII-mode acknowledgement
            // still belong to this connection and must not disappear.
            if (!state.Handled && state.Commit.Length == 0 && TryAsciiText(keycode, modifiers, out var direct))
                return BuildResultLocked(state, true, "literal-key", "commit", commitOverride: direct);
            return BuildResultLocked(state, state.Handled, "key", state.Commit.Length > 0 ? "commit" : null);
        }
    }

    public RimeCoordinatorResult? SelectCandidate(
        long requestedSequence,
        string requestedRequestId,
        int index,
        int? requestedScreenX = null,
        int? requestedScreenY = null)
    {
        lock (gate)
        {
            EnsureOpen();
            if (!MatchesCurrentVersionLocked(requestedSequence, requestedRequestId))
                return null;

            screenX = requestedScreenX ?? screenX;
            screenY = requestedScreenY ?? screenY;
            var state = session.SelectCandidate(index);
            if (mode == "claude")
            {
                AppendCommitLocked(state.Commit);
                return BuildResultLocked(state, true, "candidate", null);
            }

            return BuildResultLocked(state, state.Handled, "candidate", state.Commit.Length > 0 ? "commit" : null);
        }
    }

    public RimeCoordinatorResult? ChangePage(
        long requestedSequence,
        string requestedRequestId,
        bool backward,
        int? requestedScreenX = null,
        int? requestedScreenY = null)
    {
        lock (gate)
        {
            EnsureOpen();
            if (!MatchesCurrentVersionLocked(requestedSequence, requestedRequestId))
                return null;

            screenX = requestedScreenX ?? screenX;
            screenY = requestedScreenY ?? screenY;
            var state = session.ChangePage(backward);
            return BuildResultLocked(state, state.Handled, backward ? "page-backward" : "page-forward", null);
        }
    }

    public RimeCoordinatorResult Cancel(
        long requestedSequence,
        string? requestedRequestId = null,
        int? requestedScreenX = null,
        int? requestedScreenY = null)
    {
        lock (gate)
        {
            EnsureOpen();
            BeginVersionLocked(requestedSequence, requestedRequestId ?? requestId, requestedScreenX, requestedScreenY);
            return CancelLocked("cancel");
        }
    }

    public RimeCoordinatorResult SetMode(
        string requestedMode,
        long? requestedSequence = null,
        string? requestedRequestId = null)
    {
        lock (gate)
        {
            EnsureOpen();
            var next = NormalizeMode(requestedMode);
            if (next == mode) return BuildResultLocked(session.Snapshot(), true, "mode-unchanged", null);
            if (requestedSequence is not null) sequence = requestedSequence.Value;
            if (requestedRequestId is not null) requestId = requestedRequestId;

            CancelTranslationLocked();
            var oldMode = mode;
            if (oldMode == "claude") InsertSentenceLocked(TakeLiteralWordLocked());
            mode = next;
            if (oldMode != next) session.SetOption("ascii_mode", false);
            var state = session.Snapshot();

            // A Claude sentence cannot be left in an invisible buffer when the
            // user switches to ordinary input. Commit it as Chinese first.
            if (oldMode == "claude" && next == "normal" && (sentence.Length > 0 || state.Preedit.Length > 0))
            {
                AppendCurrentRimeTextLocked(state);
                var commit = sentence;
                sentence = "";
                codeMode = false;
                asciiMode = false;
                session.ClearComposition();
                var preserved = BuildResultLocked(
                    session.Snapshot(),
                    true,
                    "mode-change",
                    "commit",
                    commitOverride: commit);
                sentenceSeparator = false;
                return preserved;
            }

            if (next == "normal")
            {
                sentenceSeparator = false;
                codeMode = false;
                asciiMode = false;
            }

            var result = BuildResultLocked(state, true, "mode-change", null);
            return result with { State = result.State with { Mode = next, AsciiMode = next == "claude" ? asciiMode || codeMode : state.IsAsciiMode } };
        }
    }

    /// <summary>Discard connection state after a document/context boundary.
    /// Unlike Esc this must never commit the old sentence into a new host.</summary>
    public RimeCoordinatorResult Reset(long requestedSequence, string requestedRequestId)
    {
        lock (gate)
        {
            EnsureOpen();
            CancelTranslationLocked();
            BeginVersionLocked(requestedSequence, requestedRequestId, null, null);
            sentence = literalWord = "";
            sentenceSeparator = false;
            sentenceCaret = literalCaret = 0;
            asciiMode = codeMode = asciiModeBeforeCode = false;
            session.ClearComposition();
            session.SetOption("ascii_mode", false);
            return BuildResultLocked(session.Snapshot(), true, "context-reset", "cancel");
        }
    }

    private RimeCoordinatorResult ProcessClaudeKeyLocked(int keycode, int modifiers)
    {
        if (IsShiftKey(keycode) && (modifiers & (ControlMask | (1 << 3))) == 0)
        {
            if (codeMode)
                return AppendLiteralLocked("", true, "shift");
            AppendCurrentRimeTextLocked(session.Snapshot());
            asciiMode = !asciiMode;
            return BuildResultLocked(session.Snapshot(), true, "ascii-toggle", "composition");
        }

        if (IsBackspaceKey(keycode))
            return BackspaceLocked(modifiers);

        if (session.Snapshot().Preedit.Length == 0 && sentence.Length > 0 &&
            EditBuffer(ref sentence, ref sentenceCaret, keycode))
            return BuildResultLocked(session.Snapshot(), true, "sentence-edit", null);

        if (IsReturnKey(keycode))
        {
            if (codeMode)
                return AppendLiteralLocked("\n", true, "code-enter");
            return FinalizeLocked(null, "enter");
        }

        if (IsBacktick(keycode, modifiers))
        {
            if (!codeMode)
            {
                AppendCurrentRimeTextLocked(session.Snapshot());
                asciiModeBeforeCode = asciiMode;
                codeMode = true;
                asciiMode = true;
            }
            else
            {
                codeMode = false;
                asciiMode = asciiModeBeforeCode;
                asciiModeBeforeCode = false;
            }
            return AppendLiteralLocked("`", true, codeMode ? "code-open" : "code-close");
        }

        if (codeMode || asciiMode)
        {
            if (TryAsciiText(keycode, modifiers, out var ascii))
                return AppendLiteralLocked(ascii, true, codeMode ? "code" : "ascii");
        }

        // In a typed numeric slot, a period after an integer may begin a
        // decimal. Keep it editable; Enter still explicitly ends the sentence.
        // Probe the same strict grammar so ordinary sentence punctuation is
        // unchanged and an opaque literal cannot trigger this branch.
        if (!codeMode && !asciiMode && keycode == '.' &&
            session.Snapshot().Preedit.Length == 0 && sentenceCaret > 0 &&
            sentence[sentenceCaret - 1] is >= '0' and <= '9' &&
            StrictPhraseTemplates.CouldComplete(sentence.Insert(sentenceCaret, ".0")))
            return AppendLiteralLocked(".", true, "decimal-point");

        if (!codeMode && !asciiMode && TrySentencePunctuation(keycode, modifiers, out var ending))
            return FinalizeLocked(ending, "punctuation");

        if (!codeMode && !asciiMode && TryInnerPunctuation(keycode, modifiers, out var inner))
        {
            AppendCurrentRimeTextLocked(session.Snapshot());
            return AppendLiteralLocked(inner, true, "punctuation");
        }

        var state = session.ProcessKey(keycode, modifiers);
        AppendCommitLocked(state.Commit);
        if (!state.Handled && keycode == VkSpace && sentence.Length > 0)
            return BuildResultLocked(state, true, "key", null);
        if (!state.Handled && state.Commit.Length == 0 && TryAsciiText(keycode, modifiers, out var literal))
            return AppendLiteralLocked(literal, true, "literal-key");

        // A second Space after a completed Claude clause is deliberately
        // consumed so it cannot leak an accidental ASCII space to the host.
        var handled = state.Handled || state.Commit.Length > 0 ||
            (keycode == VkSpace && sentence.Length > 0 && state.Preedit.Length == 0);
        return BuildResultLocked(state, handled, "key", null);
    }

    private RimeCoordinatorResult BackspaceLocked(int modifiers)
    {
        var state = session.Snapshot();
        if (state.Preedit.Length > 0)
        {
            var changed = session.ProcessKey(X11Back, modifiers);
            return BuildResultLocked(changed, true, "backspace", null);
        }

        if (sentence.Length == 0)
            return BuildResultLocked(state, false, "backspace", "cancel");

        var oldCaret = Math.Clamp(sentenceCaret, 0, sentence.Length);
        var removed = oldCaret > 0 ? sentence[(oldCaret-1)..oldCaret] : "";
        EditBuffer(ref sentence, ref sentenceCaret, X11Back);
        if (removed == "`")
        {
            if (codeMode)
            {
                codeMode = false;
                asciiMode = asciiModeBeforeCode;
                asciiModeBeforeCode = false;
            }
            else
            {
                codeMode = true;
                asciiModeBeforeCode = asciiMode;
                asciiMode = true;
            }
        }
        return BuildResultLocked(session.Snapshot(), true, "backspace", sentence.Length == 0 ? "cancel" : null);
    }

    private RimeCoordinatorResult FinalizeLocked(string? punctuation, string source)
    {
        if (pendingTranslation && sentence.Length == 0 && literalWord.Length == 0 && session.Snapshot().Preedit.Length == 0 && punctuation is null)
            return BuildResultLocked(session.Snapshot(), true, source, "translate");

        InsertSentenceLocked(TakeLiteralWordLocked());
        AppendCurrentRimeTextLocked(session.Snapshot());
        if (!sentence.Any(c => c is >= '\u3400' and <= '\u9fff'))
            punctuation = punctuation switch { "。" => ".", "！" => "!", "？" => "?", _ => punctuation };
        if (!string.IsNullOrEmpty(punctuation)) InsertSentenceLocked(punctuation);
        if (sentence.Length == 0)
            return BuildResultLocked(session.Snapshot(), false, source, "cancel");

        // No translation or trimming is needed for an explicitly typed English
        // sentence. Spaces, punctuation, case and identifiers are user data.
        if (!pendingTranslation && !sentence.Any(c => c is >= '\u3400' and <= '\u9fff'))
        {
            var literal = sentence;
            sentence = "";
            var committed = BuildResultLocked(session.Snapshot(), true, "literal-commit", "commit", commitOverride: literal);
            sentenceSeparator = false;
            return committed;
        }

        if (translations.Count >= 16)
        {
            var preserved = CancelLocked("queue-full");
            return preserved with { Warnings = new[] { "待翻译内容过多，原文已保留，可分段重试。" } };
        }

        var item = new PendingSentence(sentence);
        translations.Add(item);
        sentence = "";
        sentenceCaret = 0;
        codeMode = asciiMode = asciiModeBeforeCode = false;

        var result = BuildResultLocked(
            session.Snapshot(),
            true,
            source,
            "translate",
            pinyinOverride: "",
            commitOverride: "",
            pendingOverride: true);

        // Process.Start and the work before Translate's first incomplete await
        // are synchronous. Keep that work off the input/pipe reply path so the
        // native component can acknowledge the source before its 2-second limit.
        var previous = translationTail;
        translationTail = Task.Run(async () =>
        {
            try { await previous.ConfigureAwait(false); } catch { }
            try { await TranslateAsync(item).ConfigureAwait(false); }
            finally { item.Cancellation.Dispose(); }
        });
        return result;
    }

    private async Task TranslateAsync(PendingSentence item)
    {
        var source = item.Source;
        var cancellation = item.Cancellation;
        if (cancellation.IsCancellationRequested) return;
        // Queue wait is not inference time. Each sentence gets its own deadline
        // once it reaches the front, while edits/reset cancel queued work too.
        cancellation.CancelAfter(TimeSpan.FromSeconds(30));
        TranslationResult? translated = null;
        bool timedOut = false;
        try
        {
            var phrase = phrases.Exact(source);
            translated = phrase is not null
                ? new TranslationResult(phrase.En, "常用指令", 0, Array.Empty<string>())
                : await translator.Translate(source, cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancellation caused by a new key, mode switch, or disconnect is
            // intentionally silent. A still-current 30-second timeout falls
            // through to the Chinese fallback below.
            lock (gate)
            {
                if (!IsCurrentTranslationLocked(item))
                    return;
            }
            timedOut = true;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or JsonException)
        {
            lock (gate)
            {
                if (!IsCurrentTranslationLocked(item))
                    return;
            }
        }
        catch (Exception)
        {
            lock (gate)
            {
                if (!IsCurrentTranslationLocked(item))
                    return;
            }
        }

        void Complete()
        {
            lock (gate)
            {
                if (!IsCurrentTranslationLocked(item)) return;
                var review = translated is null ? TranslationPolicy.Failed(source, timedOut)
                    : TranslationPolicy.Evaluate(source, translated);
                translations.RemoveAt(0);
                var state = session.Snapshot();
                var hasSuffix = pendingTranslation || sentence.Length > 0 || literalWord.Length > 0 || state.Preedit.Length > 0;
                var output = review.Output + (hasSuffix && !review.Output.EndsWith(' ') ? " " : "");
                var completed = BuildResultLocked(state, true,
                    review.Held ? "claude-fallback" : "claude-translate", "commit",
                    commitOverride: output, warnings: review.Warnings) with { Review = review };
                sentenceSeparator = !hasSuffix;
                try { StateReady?.Invoke(completed); }
                catch { /* A disconnected UI must not fault the queue. */ }
            }
        }
        if (DispatchCompletion is { } dispatch) await dispatch(Complete).ConfigureAwait(false);
        else Complete();
    }

    private RimeCoordinatorResult CancelLocked(string source)
    {
        CancelTranslationLocked();
        var state = session.Snapshot();

        if (mode != "claude")
        {
            var hadLiteral = literalWord.Length > 0;
            literalWord = "";
            session.ClearComposition();
            return BuildResultLocked(session.Snapshot(), hadLiteral || state.Preedit.Length > 0, source, "cancel");
        }

        InsertSentenceLocked(TakeLiteralWordLocked());
        AppendCurrentRimeTextLocked(state);

        if (sentence.Length > 0)
        {
            var chinese = sentence;
            sentence = "";
            codeMode = false;
            asciiMode = false;
            asciiModeBeforeCode = false;
            session.ClearComposition();
            var preserved = BuildResultLocked(
                session.Snapshot(),
                true,
                source,
                "commit",
                commitOverride: chinese,
                compositionOverride: "",
                pinyinOverride: "",
                pendingOverride: false);
            sentenceSeparator = false;
            return preserved;
        }

        session.ClearComposition();
        asciiMode = false;
        codeMode = false;
        asciiModeBeforeCode = false;
        return BuildResultLocked(session.Snapshot(), state.Preedit.Length > 0, source, "cancel");
    }

    private void AppendCurrentRimeTextLocked(RimeSessionState state)
    {
        if (state.Preedit.Length == 0) return;
        var committed = session.CommitComposition();
        var text = committed.Commit.Length > 0 ? committed.Commit : state.Preedit;
        if (text.Length > 0) InsertSentenceLocked(text);
        if (committed.Preedit.Length > 0)
            session.ClearComposition();
    }

    private void AppendCommitLocked(string text)
    {
        if (mode == "claude" && text.Length > 0)
            InsertSentenceLocked(text);
    }

    private string TakeLiteralWordLocked()
    {
        var word = literalWord;
        literalWord = "";
        literalCaret = 0;
        return word;
    }

    private RimeCoordinatorResult AppendLiteralLocked(string text, bool handled, string source)
    {
        if (text.Length > 0) InsertSentenceLocked(text);
        return BuildResultLocked(session.Snapshot(), handled, source, null);
    }

    private void InsertSentenceLocked(string text)
    {
        sentenceCaret = Math.Clamp(sentenceCaret, 0, sentence.Length);
        sentence = sentence.Insert(sentenceCaret, text);
        sentenceCaret += text.Length;
    }

    private static bool EditBuffer(ref string text, ref int caret, int key)
    {
        caret = Math.Clamp(caret, 0, text.Length);
        var previous = caret > 1 && char.IsLowSurrogate(text[caret-1]) && char.IsHighSurrogate(text[caret-2]) ? caret-2 : Math.Max(0,caret-1);
        var next = caret+1 < text.Length && char.IsHighSurrogate(text[caret]) && char.IsLowSurrogate(text[caret+1]) ? caret+2 : Math.Min(text.Length,caret+1);
        switch(key)
        {
            case 0xff50: caret=0; break;
            case 0xff57: caret=text.Length; break;
            case 0xff51: caret=previous; break;
            case 0xff53: caret=next; break;
            case 0xffff: text=text.Remove(caret,next-caret); break;
            case X11Back: case VkBack: text=text.Remove(previous,caret-previous); caret=previous; break;
            default: return false;
        }
        return true;
    }

    private RimeCoordinatorResult BuildResultLocked(
        RimeSessionState state,
        bool handled,
        string source,
        string? legacyType,
        string? commitOverride = null,
        string? compositionOverride = null,
        string? pinyinOverride = null,
        bool? pendingOverride = null,
        IReadOnlyList<string>? warnings = null)
    {
        // Wait for confirmed Chinese before deciding to load the model. Pinyin
        // and a prefix of a known complete phrase need no inference process.
        // Once a free sentence diverges, prepare while the user keeps typing.
        if (mode == "claude" && sentence.Any(c => c is >= '\u3400' and <= '\u9fff') &&
            !System.Text.RegularExpressions.Regex.IsMatch(sentence.TrimStart(), "^/[A-Za-z][\\w-]*(?:\\s|$)") &&
            !phrases.CouldComplete(sentence) && !StrictPhraseTemplates.CouldComplete(sentence))
            translator.PrepareInBackground();
        var commit = commitOverride ?? (mode == "claude" ? "" : state.Commit);
        var pinyin = pinyinOverride ?? state.Preedit;
        var sentencePosition = Math.Clamp(sentenceCaret,0,sentence.Length);
        var prefix = mode == "claude" ? string.Concat(translations.Select(item => item.Source)) : "";
        var composition = compositionOverride ?? (mode == "claude" ? prefix + sentence.Insert(sentencePosition,literalWord+pinyin) : literalWord+pinyin);
        var caret = prefix.Length + (mode == "claude" ? sentencePosition : 0) +
            (literalWord.Length > 0 ? literalCaret : state.CaretUtf16);
        if (sentenceSeparator)
        {
            if (commit.Length > 0) commit = " " + commit;
            else if (composition.Length > 0) { composition = " " + composition; ++caret; }
        }
        var pending = pendingOverride ?? pendingTranslation;
        var selected = state.SelectedIndex >= 0 ? state.SelectedIndex : 0;
        var engineState = new RimeEngineState(
            sequence,
            requestId,
            handled,
            composition,
            commit,
            state.Candidates,
            pinyin,
            selected,
            state.PageNo,
            state.PageCount,
            state.IsLastPage,
            mode == "claude" ? asciiMode || codeMode : state.IsAsciiMode,
            mode,
            pending,
            screenX,
            screenY) { LiteralWord = literalWord.Length > 0, Caret = Math.Clamp(caret,0,composition.Length),
                CommitId = commit.Length > 0 ? (++commitSequence).ToString(System.Globalization.CultureInfo.InvariantCulture) : "",
                Sentence = mode == "claude" ? prefix + sentence.Insert(sentencePosition,literalWord) : literalWord };

        var inferred = legacyType ?? InferLegacyType(engineState);
        return new(engineState, inferred, source, warnings ?? Array.Empty<string>());
    }

    private void BeginVersionLocked(
        long requestedSequence,
        string requestedRequestId,
        int? requestedScreenX,
        int? requestedScreenY)
    {
        sequence = requestedSequence;
        requestId = requestedRequestId ?? "";
        screenX = requestedScreenX;
        screenY = requestedScreenY;
    }

    private bool MatchesCurrentVersionLocked(long requestedSequence, string requestedRequestId)
    {
        if (!string.Equals(requestedRequestId, requestId, StringComparison.Ordinal)) return false;
        return requestedSequence == 0 || sequence == 0 || requestedSequence == sequence;
    }

    private bool IsCurrentTranslationLocked(PendingSentence item)
        => !disposed && translations.Count > 0 && ReferenceEquals(translations[0], item);

    private void CancelTranslationLocked()
    {
        if (!pendingTranslation) return;
        var prefix = string.Concat(translations.Select(item => item.Source));
        sentence = prefix + sentence;
        sentenceCaret += prefix.Length;
        foreach (var item in translations) item.Cancellation.Cancel();
        translations.Clear();
    }

    private static string InferLegacyType(RimeEngineState state)
    {
        if (state.PendingTranslation) return "translate";
        if (state.Commit.Length > 0) return "commit";
        if (state.Composition.Length > 0 || state.CandidateDetails.Count > 0) return "composition";
        return "cancel";
    }

    private static string NormalizeMode(string value)
        => string.Equals(value, "claude", StringComparison.OrdinalIgnoreCase) ? "claude" : "normal";

    private static bool IsShiftKey(int keycode)
        => keycode is VkShiftLeft or VkShiftRight or 0x10;

    private static bool IsBackspaceKey(int keycode)
        => keycode is VkBack or X11Back;

    private static bool IsReturnKey(int keycode)
        => keycode is VkReturn or X11Return;

    private static bool IsEscapeKey(int keycode)
        => keycode is VkEscape or X11Escape;

    // Protocol v2 carries resolved X11 keysyms, not Windows virtual keys.
    // Never apply Shift/Caps again after the native keyboard-layout mapping.
    private static bool IsBacktick(int keycode, int modifiers) => keycode == '`';

    private static bool TrySentencePunctuation(int keycode, int modifiers, out string punctuation)
    {
        punctuation = keycode switch { '.' => "。", '!' => "！", '?' => "？", _ => "" };
        return punctuation.Length > 0;
    }

    private static bool TryInnerPunctuation(int keycode, int modifiers, out string punctuation)
    {
        punctuation = keycode switch
        {
            ',' => "，", '<' => "《", '>' => "》", ';' => "；", ':' => "：",
            '/' or '\\' => "、", '\'' => "“", '"' => "”", '[' => "【", ']' => "】",
            '{' => "『", '}' => "』", '(' => "（", ')' => "）", _ => ""
        };
        return punctuation.Length > 0;
    }

    private static bool TryAsciiText(int keycode, int modifiers, out string text)
    {
        text = keycode is >= 0x20 and <= 0x7e ? ((char)keycode).ToString() : "";
        return text.Length > 0;
    }

    private static string RemoveLastRune(string value)
    {
        var runes = value.EnumerateRunes().ToArray();
        if (runes.Length == 0) return "";
        return string.Concat(runes[..^1].Select(static rune => rune.ToString()));
    }

    private static string LastRune(string value)
    {
        var runes = value.EnumerateRunes().ToArray();
        return runes.Length == 0 ? "" : runes[^1].ToString();
    }

    private void EnsureOpen()
    {
        if (disposed) throw new ObjectDisposedException(nameof(RimeSessionCoordinator));
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            CancelTranslationLocked();
            session.Dispose();
        }
    }
}
