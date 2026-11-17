using System.Collections.Concurrent;
using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using ClaudeZhInput.Rime;

namespace ClaudeZhInput;

/// <summary>Per-user bidirectional TSF connection. Never records input.</summary>
public sealed class BridgeServer : IDisposable
{
    private readonly CancellationTokenSource stop = new();
    private readonly ConcurrentDictionary<Guid, ImeConnection> clients = new();
    public IReadOnlyCollection<ImeConnection> Connections => clients.Values.ToArray();
    private readonly OfflineTranslator translator;
    private readonly PhraseStore phrases;
    private readonly string appRoot;
    private readonly string dataRoot;
    private readonly string pipeName;
    private readonly object modeGate = new();
    private string? configuredInitialMode;
    private readonly object rimeGate = new();
    private RimeEngine? rimeEngine;
    private readonly BehaviorLog? behavior;
    public event Action<ImeConnection, JsonElement>? State;
    /// <summary>Optional diagnostic hook for protocol v2 hosts; payload is an outbound JSON message.</summary>
    public event Action<ImeConnection, JsonElement>? MessageSent;
    public event Action<ImeConnection>? Disconnected;
    public TranslationReviewStore Reviews { get; } = new();
    public PhraseStore Phrases => phrases;
    public BridgeServer(string root, string pipeName = "ClaudeZhInput", string? dataRoot = null, string? initialMode = null, BehaviorLog? behavior = null)
    {
        appRoot = Path.GetFullPath(root);
        this.dataRoot = Path.GetFullPath(dataRoot ?? App.DataRoot);
        this.pipeName = pipeName;
        this.behavior = behavior;
        configuredInitialMode = initialMode == null ? null : NormalizeMode(initialMode);
        translator = new(appRoot);
        phrases = new(Path.Combine(appRoot, "phrases.json"), Path.Combine(this.dataRoot, "phrases.json"));
    }
    private string InitialMode
    {
        get
        {
            lock (modeGate)
                return configuredInitialMode ?? NormalizeMode(Preferences.Load().Mode);
        }
    }
    private static string NormalizeMode(string? value)
        => string.Equals(value, "claude", StringComparison.OrdinalIgnoreCase) ? "claude" : "normal";
    public void Start() => _ = AcceptLoop();
    private async Task AcceptLoop()
    {
        while (!stop.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 65536, 65536);
                await pipe.WaitForConnectionAsync(stop.Token);
                var client = new ImeConnection(pipe);
                pipe = null;
                clients[client.Id] = client;
                _ = ReadLoop(client);
            }
            catch (OperationCanceledException) { break; }
            catch (IOException) { await Task.Delay(250, stop.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing); }
            finally { pipe?.Dispose(); }
        }
    }
    private async Task ReadLoop(ImeConnection client)
    {
        try
        {
            while (!stop.IsCancellationRequested && client.IsConnected)
            {
                var line = await client.Reader.ReadLineAsync(stop.Token);
                if (line == null) break;
                if (line.Length > 65536) break;
                using var doc = JsonDocument.Parse(line);
                var data = doc.RootElement.Clone();
                var type = GetString(data, "type");
                if (type == "hello")
                {
                    client.SessionId = GetString(data, "sessionId");
                    client.ProcessId = data.TryGetProperty("processId", out var p) && p.TryGetInt32(out var pid) ? pid : 0;
                    client.NativeWindow = data.TryGetProperty("wakeWindow", out var w) && w.TryGetInt64(out var hwnd) ? new IntPtr(hwnd) : IntPtr.Zero;
                    client.SupportsToolbarCommands = data.TryGetProperty("toolbarCommands", out var toolbar) && toolbar.ValueKind == JsonValueKind.True;
                    client.Protocol = GetInt(data, "protocol");
                    client.Engine = GetString(data, "engine");
                    var clientVersion = GetString(data, "clientVersion");
                    client.ClientVersion = System.Text.RegularExpressions.Regex.IsMatch(clientVersion, @"^\d{1,3}\.\d{1,3}\.\d{1,3}$") ? clientVersion : "旧版";
                    string host = "unknown";
                    try { using var process = Process.GetProcessById(client.ProcessId); host = process.ProcessName; }
                    catch (Exception e) when (e is ArgumentException or InvalidOperationException) { }
                    behavior?.Write("ime_connected", new { connection = client.Id, hostPid = client.ProcessId,
                        host, client.Protocol, rime = client.Engine == "rime", toolbarCommands = client.SupportsToolbarCommands });
                    var initialMode = InitialMode;
                    if (client.Protocol == 2 && string.Equals(client.Engine, "rime", StringComparison.OrdinalIgnoreCase))
                        AttachRime(client, initialMode);
                    await Send(client, new { op = "set-mode", mode = initialMode }, stop.Token);
                    if (client.Rime is not null)
                        await PublishRimeOrdered(client, client.Rime.Snapshot(), stop.Token);
                    else if (client.Protocol == 2 && string.Equals(client.Engine, "rime", StringComparison.OrdinalIgnoreCase))
                        await Send(client, new
                        {
                            type = "engine-state",
                            engineReady = false,
                            translationStatus = "none",
                            sessionId = client.SessionId,
                            sequence = 0L,
                            requestId = "",
                            handled = false,
                            composition = "",
                            commit = "",
                            candidates = Array.Empty<string>(),
                            pinyin = "",
                            selectedIndex = 0,
                            page = 0,
                            pageCount = 0,
                            isLastPage = true,
                            asciiMode = false,
                            mode = initialMode,
                            pendingTranslation = false,
                            warnings = new[] { client.RimeError.Length == 0 ? "Rime 协调器不可用。" : client.RimeError }
                        }, stop.Token);
                    continue;
                }
                if (client.SessionId.Length == 0) continue;
                var incomingSession = GetString(data, "sessionId");
                if (incomingSession.Length > 0 && incomingSession != client.SessionId) continue;
                if (type is "engine-key" or "engine-select" or "engine-page" or "engine-cancel" or "commands" or "deactivate" or "blur" or "toolbar-command-rejected")
                    behavior?.Write(type, new { connection = client.Id, sequence = GetLong(data, "sequence") });

                if (client.Rime is not null && await HandleRimeMessage(client, type, data))
                    continue;

                var session = GetString(data, "sessionId");
                if (session.Length > 0 && session != client.SessionId) continue;
                var request = GetString(data, "requestId");
                if (request.Length > 0 && request != client.LatestRequest)
                {
                    client.CancelTranslation();
                    client.LatestRequest = request;
                }
                State?.Invoke(client, data);
                if (type == "translate")
                {
                    client.CancelTranslation();
                    var pending = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                    pending.CancelAfter(TimeSpan.FromSeconds(12));
                    var token = pending.Token;
                    client.BeginTranslation(pending);
                    _ = Translate(client, request, GetString(data, "text"), token);
                }
                else if (type is "cancel" or "commit" or "deactivate" or "blur")
                    client.CancelTranslation();
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException or JsonException)
        { if (!stop.IsCancellationRequested) behavior?.Error("pipe_read", e, client.Id); }
        finally
        {
            clients.TryRemove(client.Id, out _);
            behavior?.Write("ime_disconnected", new { connection = client.Id, pendingTranslation = client.TraceTranslationStarted != 0 });
            client.Dispose();
            Disconnected?.Invoke(client);
        }
    }
    private async Task Translate(ImeConnection client, string request, string source, CancellationToken cancellation)
    {
        try
        {
            var phrase = phrases.Exact(source);
            var result = phrase != null
                ? new TranslationResult(phrase.En, "常用指令", 0, [])
                : await translator.Translate(source, cancellation);
            if (!cancellation.IsCancellationRequested && client.LatestRequest == request)
            {
                var review = TranslationPolicy.Evaluate(source, result);
                Reviews.Add(review);
                await Send(client, new { op = "translated", requestId = request, text = review.Output, warnings = review.Warnings }, cancellation);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is IOException or InvalidOperationException or JsonException)
        {
            if (client.LatestRequest == request && !cancellation.IsCancellationRequested)
            {
                var review = TranslationPolicy.Failed(source);
                Reviews.Add(review);
                await Send(client, new { op = "translated", requestId = request, text = "", warnings = review.Warnings }, stop.Token);
            }
        }
    }
    public async Task SetMode(string mode)
    {
        var nextMode = NormalizeMode(mode);
        behavior?.Write("mode_changed", new { mode = nextMode, connections = clients.Count });
        // An explicitly injected mode belongs to an isolated host and must
        // also seed connections that arrive after this broadcast. The formal
        // App leaves this null, so its new connections continue reading the
        // persisted Preferences file as before.
        lock (modeGate)
        {
            if (configuredInitialMode != null)
                configuredInitialMode = nextMode;
        }

        foreach (var client in clients.Values)
        {
            client.CancelTranslation();
            await EnterRimeInputAsync(client, stop.Token);
            try
            {
                if (client.Rime is not null)
                {
                    try
                    {
                        var result = client.Rime.SetMode(mode);
                        await PublishRimeOrdered(client, result, stop.Token);
                    }
                    catch (ObjectDisposedException) { }
                    catch (InvalidOperationException) { }

                    // Keep the mode notification in the same per-connection
                    // write order as the engine-state it describes. A key
                    // arriving while this method is broadcasting must wait
                    // behind both messages, while its coordinator result is
                    // still held by RimeInputDepth.
                    await SendRimeOrdered(client, new { op = "set-mode", mode = nextMode }, stop.Token);
                }
                else
                {
                    await Send(client, new { op = "set-mode", mode = nextMode }, stop.Token);
                }
            }
            finally
            {
                await ExitRimeInputAsync(client, stop.Token);
            }
        }
    }

    private void AttachRime(ImeConnection client, string initialMode)
    {
        try
        {
            var engine = OpenRimeEngine();
            var coordinator = new RimeSessionCoordinator(engine, translator, phrases, initialMode);
            coordinator.StateReady += result => QueueRimeResult(client, result);
            coordinator.DispatchCompletion = async action =>
            {
                await EnterRimeInputAsync(client, stop.Token).ConfigureAwait(false);
                try { action(); }
                finally { await ExitRimeInputAsync(client, stop.Token).ConfigureAwait(false); }
            };
            client.Rime = coordinator;
            client.RimeError = "";
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or FileNotFoundException)
        {
            client.RimeError = ex.Message;
        }
    }

    private RimeEngine OpenRimeEngine()
    {
        lock (rimeGate)
        {
            if (rimeEngine is not null) return rimeEngine;

            var candidates = new[]
            {
                Path.Combine(appRoot, "rime", "runtime"),
                Path.Combine(AppContext.BaseDirectory, "runtime"),
                Path.Combine(AppContext.BaseDirectory, "rime", "runtime")
            };
            var runtime = candidates.FirstOrDefault(path => File.Exists(Path.Combine(path, "rime.dll")));
            if (runtime is null)
                throw new FileNotFoundException("Rime runtime is unavailable for protocol v2.");

            rimeEngine = RimeEngine.Open(new RimeEngineOptions
            {
                RuntimeDirectory = runtime,
                UserDataDirectory = Path.Combine(dataRoot, "rime")
            });
            return rimeEngine;
        }
    }

    private async Task<bool> HandleRimeMessage(ImeConnection client, string type, JsonElement data)
    {
        var coordinator = client.Rime;
        if (coordinator is null) return false;

        var sequence = GetLong(data, "sequence");
        var request = GetString(data, "requestId");
        var x = GetNullableInt(data, "screenX");
        var y = GetNullableInt(data, "screenY");
        RimeCoordinatorResult? result = null;

        await EnterRimeInputAsync(client, stop.Token);
        try
        {
            switch (type)
            {
                case "engine-key":
                    if (!TryGetInt(data, "keycode", out var keycode)) return true;
                    result = coordinator.ProcessKey(sequence, request, keycode,
                        GetInt(data, "modifiers"), x, y);
                    break;
                case "engine-select":
                    if (!TryGetInt(data, "index", out var index)) return true;
                    result = coordinator.SelectCandidate(sequence, request, index, x, y);
                    break;
                case "engine-page":
                    result = coordinator.ChangePage(sequence, request, GetBool(data, "backward"), x, y);
                    break;
                case "engine-cancel":
                    result = coordinator.Cancel(sequence, request, x, y);
                    break;
                case "engine-reset":
                case "blur":
                case "deactivate":
                    result = coordinator.Reset(sequence, request);
                    break;
                default:
                    return false;
            }
        }
        catch (ObjectDisposedException) { return true; }
        catch (InvalidOperationException ex)
        {
            client.RimeError = ex.Message;
            behavior?.Error("rime_input", ex, client.Id);
            await Send(client, new { type = "engine-state", engineReady = false,
                sessionId = client.SessionId, sequence, requestId = request,
                translationStatus = "none", warnings = new[] { "输入引擎未就绪，已保留原输入。" } }, stop.Token);
            return true;
        }
        finally
        {
            if (result is not null)
                await PublishRimeOrdered(client, result, stop.Token);

            await ExitRimeInputAsync(client, stop.Token);
        }
        return true;
    }

    private void QueueRimeResult(ImeConnection client, RimeCoordinatorResult result)
    {
        bool flush;
        lock (client.RimeReadyGate)
        {
            client.RimeReady.Enqueue(result);
            flush = client.RimeInputDepth == 0;
        }

        // A translation can finish synchronously while ProcessKey still holds
        // the coordinator lock. RimeInputDepth keeps that result behind the
        // key's pending state; asynchronous completions are flushed directly.
        if (flush)
            _ = FlushRimeResults(client, stop.Token);
    }

    private async Task EnterRimeInputAsync(ImeConnection client, CancellationToken cancellation)
    {
        await client.RimeActionGate.WaitAsync(cancellation).ConfigureAwait(false);
        while (true)
        {
            var shouldFlush = false;
            lock (client.RimeReadyGate)
            {
                if (client.RimeInputDepth == 0 && client.RimeReady.Count > 0)
                    shouldFlush = true;
                else
                {
                    client.RimeInputDepth++;
                    return;
                }
            }

            // A completed background result already precedes this input. Let
            // it publish before taking the depth reservation for the new key.
            if (shouldFlush)
                await FlushRimeResults(client, cancellation).ConfigureAwait(false);
        }
    }

    private async Task ExitRimeInputAsync(ImeConnection client, CancellationToken cancellation)
    {
        try
        {
        var shouldFlush = false;
        lock (client.RimeReadyGate)
        {
            if (client.RimeInputDepth > 0)
                client.RimeInputDepth--;
            shouldFlush = client.RimeInputDepth == 0;
        }

        if (shouldFlush)
            await FlushRimeResults(client, cancellation).ConfigureAwait(false);
        }
        finally { client.RimeActionGate.Release(); }
    }

    private async Task FlushRimeResults(ImeConnection client, CancellationToken cancellation)
    {
        var entered = false;
        try
        {
            await client.RimePublishGate.WaitAsync(cancellation).ConfigureAwait(false);
            entered = true;

            while (true)
            {
                RimeCoordinatorResult? next;
                lock (client.RimeReadyGate)
                {
                    if (client.RimeInputDepth != 0 || client.RimeReady.Count == 0)
                        return;
                    next = client.RimeReady.Dequeue();
                }
                if (next is null)
                    return;
                await PublishRimeCore(client, next, cancellation).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        finally
        {
            if (entered)
                client.RimePublishGate.Release();
        }
    }

    private async Task PublishRimeOrdered(ImeConnection client, RimeCoordinatorResult result, CancellationToken cancellation)
    {
        await client.RimePublishGate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            await PublishRimeCore(client, result, cancellation).ConfigureAwait(false);
        }
        finally
        {
            client.RimePublishGate.Release();
        }
    }

    private async Task SendRimeOrdered(ImeConnection client, object message, CancellationToken cancellation)
    {
        await client.RimePublishGate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            await Send(client, message, cancellation).ConfigureAwait(false);
        }
        finally
        {
            client.RimePublishGate.Release();
        }
    }

    private async Task PublishRimeCore(ImeConnection client, RimeCoordinatorResult result, CancellationToken cancellation)
    {
        if (result.Review != null) Reviews.Add(result.Review);
        behavior?.RecordEngine(client, result);
        var state = result.State;
        client.LatestRequest = state.RequestId;
        await Send(client, new
        {
            type = "engine-state",
            engineReady = true,
            continuousSentences = true,
            translationStatus = result.Source == "claude-fallback" ? "fallback"
                : result.Source == "claude-translate" ? "translated" : "none",
            sessionId = client.SessionId,
            sequence = state.Sequence,
            requestId = state.RequestId,
            handled = state.Handled,
            composition = state.Composition,
            commit = state.Commit,
            commitId = state.CommitId,
            candidates = state.CandidateDetails.Select(candidate => candidate.Text).ToArray(),
            pinyin = state.Pinyin,
            selectedIndex = state.SelectedIndex,
            page = state.Page,
            pageCount = state.PageCount,
            isLastPage = state.IsLastPage,
            asciiMode = state.AsciiMode,
            literalWord = state.LiteralWord,
            caret = state.Caret,
            mode = state.Mode,
            pendingTranslation = state.PendingTranslation,
            screenX = state.ScreenX,
            screenY = state.ScreenY,
            warnings = result.Warnings
        }, cancellation);

        var legacy = ToLegacyState(client, result);
        if (legacy is JsonElement value)
            State?.Invoke(client, value);
    }

    private async Task Send(ImeConnection client, object message, CancellationToken cancellation)
    {
        await client.Send(message, cancellation).ConfigureAwait(false);
        try
        {
            MessageSent?.Invoke(client, JsonSerializer.SerializeToElement(message));
        }
        catch (Exception) { }
    }

    private static JsonElement? ToLegacyState(ImeConnection client, RimeCoordinatorResult result)
    {
        var state = result.State;
        if (result.LegacyType == "cancel" && state.Composition.Length == 0 && state.Commit.Length == 0 &&
            (result.Source == "snapshot" || (result.Source == "key" && !state.Handled)))
            return null;

        var sentence = state.Sentence;
        var displayType = result.LegacyType;
        if (displayType == "commit" && state.Composition.Length > 0)
            displayType = state.PendingTranslation ? "translate" : "composition";
        if (displayType == "translate" && (state.Pinyin.Length > 0 || state.CandidateDetails.Count > 0 || state.LiteralWord))
            displayType = "composition";
        var text = displayType == "translate"
            ? state.Composition
            : state.Commit.Length > 0 ? state.Commit : state.Composition;
        var legacy = new
        {
            type = displayType,
            sessionId = client.SessionId,
            requestId = state.RequestId,
            pinyin = state.Pinyin,
            sentence,
            text,
            candidates = state.CandidateDetails.Select(candidate => candidate.Text).ToArray(),
            selectedIndex = state.SelectedIndex,
            page = state.Page,
            pageCount = state.PageCount ?? (state.IsLastPage ? state.Page + 1 : 0),
            asciiMode = state.AsciiMode,
            literalWord = state.LiteralWord,
            pendingTranslation = state.PendingTranslation,
            mode = state.Mode,
            screenX = state.ScreenX,
            screenY = state.ScreenY,
            source = result.Source,
            warnings = result.Warnings
        };
        return JsonSerializer.SerializeToElement(legacy);
    }

    private static int GetInt(JsonElement data, string property)
        => TryGetInt(data, property, out var value) ? value : 0;

    private static bool TryGetInt(JsonElement data, string property, out int value)
    {
        value = 0;
        if (!data.TryGetProperty(property, out var token)) return false;
        if (token.ValueKind == JsonValueKind.Number && token.TryGetInt32(out value)) return true;
        if (token.ValueKind == JsonValueKind.String && int.TryParse(token.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) return true;
        return false;
    }

    private static long GetLong(JsonElement data, string property)
    {
        if (!data.TryGetProperty(property, out var token)) return 0;
        if (token.ValueKind == JsonValueKind.Number && token.TryGetInt64(out var value)) return value;
        if (token.ValueKind == JsonValueKind.String &&
            long.TryParse(token.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            return parsed;
        return 0;
    }

    private static int? GetNullableInt(JsonElement data, string property)
        => TryGetInt(data, property, out var value) ? value : null;

    private static bool GetBool(JsonElement data, string property)
        => data.TryGetProperty(property, out var token) &&
           (token.ValueKind == JsonValueKind.True ||
            (token.ValueKind == JsonValueKind.String && bool.TryParse(token.GetString(), out var value) && value));
    public static string GetString(JsonElement data, string property)
        => data.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    public void Dispose()
    {
        Reviews.Clear();
        stop.Cancel();
        foreach (var client in clients.Values) client.Dispose();
        translator.Dispose();
        lock (rimeGate)
        {
            rimeEngine?.Dispose();
            rimeEngine = null;
        }
    }
}

public sealed class ImeConnection : IDisposable
{
    private readonly NamedPipeServerStream pipe;
    private readonly SemaphoreSlim writeGate = new(1, 1);
    public Guid Id { get; } = Guid.NewGuid();
    public StreamReader Reader { get; }
    public string SessionId { get; set; } = "";
    public int ProcessId { get; set; }
    public IntPtr NativeWindow { get; set; }
    public bool SupportsToolbarCommands { get; set; }
    public int Protocol { get; set; }
    public string Engine { get; set; } = "";
    public string ClientVersion { get; set; } = "旧版";
    public RimeSessionCoordinator? Rime { get; set; }
    public string RimeError { get; set; } = "";
    public string LatestRequest { get; set; } = "";
    internal long TraceTranslationStarted;
    internal readonly SemaphoreSlim RimePublishGate = new(1, 1);
    internal readonly SemaphoreSlim RimeActionGate = new(1, 1);
    internal readonly object RimeReadyGate = new();
    internal readonly Queue<RimeCoordinatorResult> RimeReady = new();
    internal int RimeInputDepth;
    private CancellationTokenSource? pending;
    public bool IsConnected { get { try { return pipe.IsConnected; } catch (ObjectDisposedException) { return false; } } }
    internal ImeConnection(NamedPipeServerStream stream)
    {
        pipe = stream;
        Reader = new(stream, new UTF8Encoding(false), false, 4096, leaveOpen: true);
    }
    public async Task Send(object message, CancellationToken cancellation = default)
    {
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(TimeSpan.FromSeconds(2));
            await writeGate.WaitAsync(deadline.Token);
            try
            {
                if (pipe.IsConnected)
                    await pipe.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message) + "\n"), deadline.Token);
            }
            finally { writeGate.Release(); }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException) { }
    }
    public void CancelTranslation()
    {
        var previous = Interlocked.Exchange(ref pending, null);
        if (previous != null) { previous.Cancel(); previous.Dispose(); }
    }
    public void BeginTranslation(CancellationTokenSource source)
    {
        var previous = Interlocked.Exchange(ref pending, source);
        if (previous != null) { previous.Cancel(); previous.Dispose(); }
    }
    public void Dispose()
    {
        CancelTranslation();
        Rime?.Dispose();
        Rime = null;
        lock (RimeReadyGate) RimeReady.Clear();
        pipe.Dispose();
    }
}
