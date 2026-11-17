using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ClaudeZhInput;

public record Phrase(string Zh, string En, string Keys = "", int Uses = 0);
public record TranslationResult(string Text, string Engine, double Seconds, string[] Warnings);

public static class PhraseMatcher
{
    public static string Key(string value)
    {
        var key = value.Trim();
        if (key.Length > 0 && key[^1] is '。' or '.' &&
            (key.Length == 1 || key[^2] is not ('。' or '.')))
            return key[..^1].Trim();
        return key;
    }
    public static List<Phrase> Suggest(string text, IEnumerable<Phrase> phrases, int limit = 5)
    {
        var query = text.Trim();
        var tail = Regex.Split(query, "[，。；！？\\r\\n]").LastOrDefault()?.Trim() ?? "";
        if (query.Length == 0) return phrases.OrderByDescending(p => p.Uses).Take(limit).ToList();
        if (tail.Length == 0) return [];
        return phrases.Select(p => (p, score: Score(tail, p)))
            .Where(x => x.score > 0 && Key(x.p.Zh) != Key(tail))
            .OrderByDescending(x => x.score).ThenByDescending(x => x.p.Uses)
            .Take(limit).Select(x => x.p).ToList();
    }
    private static int Score(string q, Phrase p)
    {
        if (p.Zh.StartsWith(q, StringComparison.OrdinalIgnoreCase)) return 100;
        if (p.Keys.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(k => k.StartsWith(q, StringComparison.OrdinalIgnoreCase))) return 70;
        if (q.Length > 1 && p.Zh.Contains(q, StringComparison.OrdinalIgnoreCase)) return 50;
        return 0;
    }
    public static string Complete(string current, string replacement)
    {
        var matches = Regex.Matches(current, "[，。；！？\\r\\n]");
        var start = matches.Count > 0 ? matches[^1].Index + 1 : 0;
        return current[..start] + replacement;
    }
}

public sealed partial class PhraseStore
{
    private readonly string path;
    private readonly string defaultsFilePath;
    private readonly List<Phrase> defaults;
    private readonly object sync = new();
    private List<Phrase> personal = [];
    // A writer owns these collections until this single reference is published.
    // Published dictionaries and arrays are never mutated or exposed to callers.
    private sealed record PhraseIndex(Dictionary<string, Phrase> Exact, IReadOnlyList<Phrase> All, string[] OrderedKeys);
    private PhraseIndex index = new(new(StringComparer.Ordinal), Array.AsReadOnly(Array.Empty<Phrase>()), []);
    public string? LoadWarning { get; private set; }
    public PhraseStore(string defaultsPath, string dataPath)
    {
        path = dataPath;
        defaultsFilePath = Path.GetFullPath(defaultsPath);
        defaults = File.Exists(defaultsPath)
            ? JsonSerializer.Deserialize<List<Phrase>>(File.ReadAllText(defaultsPath), JsonOptions) ?? []
            : BuiltInPhrases.Create();
        if (File.Exists(path))
        {
            try { personal = JsonSerializer.Deserialize<List<Phrase>>(File.ReadAllText(path), JsonOptions) ?? []; }
            catch (Exception ex) when (ex is JsonException or IOException)
            { LoadWarning = "个人短语文件暂时无法读取；本次仍可翻译。原文件已保留。"; }
        }
        LoadPacks();
        RebuildIndex();
    }
    public static JsonSerializerOptions JsonOptions { get; } = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    public IEnumerable<Phrase> All => Volatile.Read(ref index).All;
    public void Remember(string zh, string en)
    {
        lock (sync)
        {
            if (LoadWarning != null) throw new InvalidOperationException(LoadWarning);
            var key = PhraseMatcher.Key(zh);
            var old = personal.FirstOrDefault(p => PhraseMatcher.Key(p.Zh) == key);
            var updated = personal.Where(p => PhraseMatcher.Key(p.Zh) != key).ToList();
            updated.Insert(0, new(zh.Trim(), en.Trim(), "", (old?.Uses ?? 0) + 1));
            updated = updated.Take(200).ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(updated, JsonOptions), new UTF8Encoding(false));
            File.Move(temp, path, true);
            personal = updated;
            RebuildIndex();
        }
    }
    private void RebuildIndex()
    {
        var updated = new Dictionary<string, Phrase>(StringComparer.Ordinal);
        foreach (var phrase in personal.Concat(activePacks.SelectMany(pack => pack.Phrases)).Concat(defaults))
            updated.TryAdd(PhraseMatcher.Key(phrase.Zh), phrase);
        var orderedKeys = updated.Keys.OrderBy(key => key, StringComparer.Ordinal).ToArray();
        Volatile.Write(ref index, new(updated, Array.AsReadOnly(updated.Values.ToArray()), orderedKeys));
    }
    public Phrase? Exact(string text)
    {
        var key = PhraseMatcher.Key(text);
        return Volatile.Read(ref index).Exact.GetValueOrDefault(key);
    }
    public bool CouldComplete(string prefix)
    {
        var key = PhraseMatcher.Key(prefix);
        if (key.Length == 0) return true;
        var keys = Volatile.Read(ref index).OrderedKeys;
        var position = Array.BinarySearch(keys, key, StringComparer.Ordinal);
        if (position < 0) position = ~position;
        return position < keys.Length && keys[position].StartsWith(key, StringComparison.Ordinal);
    }
}

public sealed class OfflineTranslator : IDisposable
{
    private static readonly HashSet<string> BuiltInKeys = BuiltInPhrases.Create()
        .Select(p => PhraseMatcher.Key(p.Zh)).ToHashSet(StringComparer.Ordinal);
    private readonly string root;
    private Process? worker;
    private WorkerProcessJob? workerJob;
    private Task<string>? errorRead;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object processGate = new();
    private readonly System.Threading.Timer idleTimer;
    private readonly TimeSpan idleTimeout;
    private long lastActivity;
    private volatile bool disposed;
    private EventWaitHandle? cancelEvent;
    private volatile bool prepared;
    private int preparing;
    private long nextPreparationAt;
    public OfflineTranslator(string appRoot) : this(appRoot, TimeSpan.FromMinutes(5)) { }
    public OfflineTranslator(string appRoot, TimeSpan idleTimeout)
    {
        if (idleTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(idleTimeout));
        root = appRoot;
        this.idleTimeout = idleTimeout;
        idleTimer = new System.Threading.Timer(ReleaseIdle, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }
    // Called from input handling, but all process/model work runs off that path.
    // Coalesce repeated hints into one preparation.
    public void PrepareInBackground()
    {
        if (disposed) return;
        if (prepared) { Volatile.Write(ref lastActivity, Stopwatch.GetTimestamp()); return; }
        if (Stopwatch.GetTimestamp() < Volatile.Read(ref nextPreparationAt) ||
            Interlocked.CompareExchange(ref preparing, 1, 0) != 0) return;
        _ = Task.Run(async () =>
        {
            try { await PrepareAsync().ConfigureAwait(false); }
            catch { Volatile.Write(ref nextPreparationAt, Stopwatch.GetTimestamp() + 5 * Stopwatch.Frequency); }
            finally { Volatile.Write(ref preparing, 0); }
        });
    }
    public async Task PrepareAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try { await EnsurePrepared().ConfigureAwait(false); }
        finally { EndRequest(); }
    }
    public void TouchIfRunning()
    {
        // A key must never wait for process teardown. A stale reference is
        // harmless here: this hint only updates a timestamp and starts nothing.
        if (!disposed && Volatile.Read(ref worker) != null)
            Volatile.Write(ref lastActivity, Stopwatch.GetTimestamp());
    }
    private Process EnsureWorker()
    {
        lock (processGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            idleTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            if (worker == null || worker.HasExited)
            {
                DisposeProcess();
                var compiled = Path.Combine(root, "worker.exe");
                var binary = File.Exists(compiled) ? compiled : Path.Combine(root, "runtime", "python.exe");
                if (!File.Exists(binary)) throw new InvalidOperationException("离线运行组件缺失，请重新安装。中文输入仍可使用。");
                var info = new ProcessStartInfo(binary)
                {
                    WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                if (binary != compiled)
                {
                    info.ArgumentList.Add("-X"); info.ArgumentList.Add("utf8");
                    info.ArgumentList.Add(Path.Combine(root, "worker.py"));
                }
                var eventName = "Local\\ClaudeZhInput.Cancel." + Guid.NewGuid().ToString("N");
                cancelEvent = new EventWaitHandle(false, EventResetMode.ManualReset, eventName);
                info.Environment["CZI_TRANSLATION_CANCEL_EVENT"] = eventName;
                info.Environment["HF_HUB_OFFLINE"] = "1";
                info.Environment["TRANSFORMERS_OFFLINE"] = "1";
                info.Environment["PYTHONUTF8"] = "1";
                info.Environment["PYTHONIOENCODING"] = "utf-8";
                worker = Process.Start(info) ?? throw new InvalidOperationException("无法启动本地翻译引擎。");
                try { workerJob = WorkerProcessJob.Attach(worker); }
                catch { DisposeProcess(); throw; }
                errorRead = worker.StandardError.ReadToEndAsync();
            }
            return worker;
        }
    }
    private async Task EnsurePrepared()
    {
        var active = EnsureWorker();
        if (prepared) return;
        // Preparation is shared work. Editing/cancelling a sentence must not
        // interrupt loading and force the next sentence to load again.
        using var reply = await Exchange(active, new { op = "prepare" }, CancellationToken.None).ConfigureAwait(false);
        if (!reply.RootElement.TryGetProperty("ready", out var ready) || !ready.GetBoolean())
            throw new InvalidOperationException("本地翻译组件版本不匹配，请重新安装完整包。");
        lock (processGate) { ObjectDisposedException.ThrowIf(disposed, this); prepared = true; }
    }
    private async Task<JsonDocument> Exchange(Process active, object command, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        EventWaitHandle signal;
        lock (processGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            signal = cancelEvent!;
            signal.Reset();
        }
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        // Drain exactly one reply before releasing the request gate. An
        // unresponsive cancellation is a process fault, with a bounded restart.
        using var registration = cancellation.Register(() =>
        {
            try { signal.Set(); watchdog.CancelAfter(TimeSpan.FromSeconds(2)); }
            catch (ObjectDisposedException) { }
        });
        try
        {
            await active.StandardInput.WriteLineAsync(JsonSerializer.Serialize(command)).ConfigureAwait(false);
            await active.StandardInput.FlushAsync(watchdog.Token).ConfigureAwait(false);
            var line = await active.StandardOutput.ReadLineAsync(watchdog.Token).ConfigureAwait(false);
            if (line == null) throw new InvalidOperationException("本地翻译引擎已停止，请重试。");
            var result = JsonDocument.Parse(line);
            if (cancellation.IsCancellationRequested || result.RootElement.TryGetProperty("cancelled", out _))
            {
                result.Dispose();
                throw new OperationCanceledException(cancellation);
            }
            return result;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested && !watchdog.IsCancellationRequested)
        { throw; } // Graceful cancellation: the model and fixed prefix remain loaded.
        catch
        {
            lock (processGate) { if (ReferenceEquals(worker, active)) DisposeProcess(); }
            cancellation.ThrowIfCancellationRequested();
            throw;
        }
    }
    public async Task<TranslationResult> Translate(string source, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(disposed, this);
        // Exact built-ins remain authoritative; the coordinator has already
        // resolved personal and imported phrases. Strict grammar needs no model.
        if (!BuiltInKeys.Contains(PhraseMatcher.Key(source)) &&
            StrictPhraseTemplates.TryTranslate(source, out var templateEnglish))
        {
            cancellation.ThrowIfCancellationRequested();
            return new(templateEnglish, "严格句型", 0, []);
        }
        await gate.WaitAsync(cancellation);
        try
        {
            // Exact built-ins and literal commands need no model. For a new
            // Chinese sentence, share preparation so cancelling while loading
            // does not tear the model down and restart the next sentence cold.
            if (Regex.IsMatch(source, "[\\u3400-\\u9fff]") &&
                !Regex.IsMatch(source.TrimStart(), "^/[A-Za-z][\\w-]*(?:\\s|$)") &&
                !BuiltInKeys.Contains(PhraseMatcher.Key(source)))
                await EnsurePrepared().ConfigureAwait(false);
            var active = EnsureWorker();
            cancellation.ThrowIfCancellationRequested();
            using var result = await Exchange(active, new { text = source }, cancellation).ConfigureAwait(false);
            var obj = result.RootElement;
            if (obj.TryGetProperty("error", out var error)) throw new InvalidOperationException(error.GetString());
            return new(obj.GetProperty("text").GetString() ?? "", obj.GetProperty("engine").GetString() ?? "离线翻译",
                obj.GetProperty("seconds").GetDouble(), obj.GetProperty("warnings").EnumerateArray().Select(v => v.GetString() ?? "").ToArray());
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        { throw new InvalidOperationException("本次翻译超时，原文已保留。可以缩短文字后重试。"); }
        finally { EndRequest(); }
    }
    private void EndRequest()
    {
            lock (processGate)
            {
                lastActivity = Stopwatch.GetTimestamp();
                if (!disposed && worker != null) idleTimer.Change(idleTimeout, Timeout.InfiniteTimeSpan);
            }
            gate.Release();
    }
    private void ReleaseIdle(object? state)
    {
        // Never unload during a request or interrupt a queued request. A stale
        // timer callback must recheck the latest activity before closing anything.
        if (!gate.Wait(0)) return;
        try
        {
            lock (processGate)
            {
                if (disposed || worker == null) return;
                var remaining = idleTimeout - Stopwatch.GetElapsedTime(Volatile.Read(ref lastActivity));
                if (remaining > TimeSpan.Zero) idleTimer.Change(remaining, Timeout.InfiniteTimeSpan);
                else DisposeProcess();
            }
        }
        finally { gate.Release(); }
    }
    private void DisposeProcess()
    {
        prepared = false;
        workerJob?.Dispose(); workerJob = null;
        if (worker != null)
        {
            try
            {
                if (!worker.HasExited) worker.Kill(true);
                // Job close / Kill initiate termination asynchronously. Wait
                // briefly before releasing the handle so a restart or upgrade
                // does not immediately collide with our still-locked worker.
                worker.WaitForExit(2000);
            }
            catch { }
            worker.Dispose(); worker = null;
        }
        cancelEvent?.Dispose(); cancelEvent = null;
    }
    public void Dispose()
    {
        lock (processGate)
        {
            if (disposed) return;
            disposed = true;
            idleTimer.Dispose();
            DisposeProcess();
        }
    }
}
