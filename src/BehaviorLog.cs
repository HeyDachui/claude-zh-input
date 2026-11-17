using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace ClaudeZhInput;

/// <summary>Local, bounded metadata journal. Call sites must never pass input text, keys, paths or window titles.</summary>
public sealed class BehaviorLog : IDisposable
{
    private readonly string directory;
    private readonly string configuration;
    private readonly long segmentBytes;
    private readonly Channel<string> queue = Channel.CreateBounded<string>(new BoundedChannelOptions(4096)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Task writer;
    private readonly System.Threading.Timer metrics;
    private readonly string run = Guid.NewGuid().ToString("N");
    private volatile bool enabled;
    private long dropped;
    private long writeErrors;
    public bool Enabled => enabled;
    public string DirectoryPath => directory;

    public BehaviorLog(string dataRoot, bool? enabledOverride = null, long segmentBytes = 5 * 1024 * 1024)
    {
        directory = Path.Combine(dataRoot, "behavior-log");
        configuration = Path.Combine(dataRoot, "behavior-recording.json");
        this.segmentBytes = segmentBytes;
        try
        {
            using var config = JsonDocument.Parse(File.ReadAllText(configuration));
            enabled = config.RootElement.TryGetProperty("enabled", out var value) && value.ValueKind == JsonValueKind.True;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        if (enabledOverride is bool supplied) enabled = supplied;
        writer = Task.Run(WriteLoop);
        metrics = new System.Threading.Timer(_ => RecordMetrics(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    public void SetEnabled(bool value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(configuration)!);
        var temp = configuration + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new { enabled = value, inputText = false }));
        File.Move(temp, configuration, true);
        if (!value) Write("recording_stopped", new { });
        enabled = value;
        if (value) Write("recording_started", new { inputText = false, maximumMiB = 20 });
    }

    public void Write(string name, object metadata)
    {
        if (!enabled) return;
        var line = JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow, run, servicePid = Environment.ProcessId, name, metadata });
        if (!queue.Writer.TryWrite(line)) Interlocked.Increment(ref dropped);
    }

    public void Error(string stage, Exception error, Guid? connection = null)
        => Write("error", new { stage, connection, exception = error.GetType().Name, hresult = error.HResult });

    public void RecordEngine(ImeConnection client, RimeCoordinatorResult result)
    {
        var s = result.State;
        if (s.PendingTranslation && client.TraceTranslationStarted == 0)
        {
            client.TraceTranslationStarted = Stopwatch.GetTimestamp();
            Write("translation_started", new { connection = client.Id, s.Sequence, characters = s.Composition.Length });
        }
        else if (!s.PendingTranslation && client.TraceTranslationStarted != 0)
        {
            var elapsed = Stopwatch.GetElapsedTime(client.TraceTranslationStarted).TotalMilliseconds;
            client.TraceTranslationStarted = 0;
            Write("translation_finished", new { connection = client.Id, s.Sequence, elapsedMs = Math.Round(elapsed, 2),
                outcome = result.Source is "claude-translate" or "claude-fallback" or "claude-review" ? result.Source : "cancelled",
                outputCharacters = s.Commit.Length, warningCount = result.Warnings.Count });
        }
        Write("engine_state", new { connection = client.Id, s.Sequence, s.Handled, mode = s.Mode, ascii = s.AsciiMode,
            pending = s.PendingTranslation, source = result.Source, kind = result.LegacyType,
            composingCharacters = s.Composition.Length, pinyinCharacters = s.Pinyin.Length,
            committedCharacters = s.Commit.Length, candidateCount = s.CandidateDetails.Count, s.Page,
            warningCount = result.Warnings.Count });
    }

    private void RecordMetrics()
    {
        if (!enabled) return;
        try
        {
            using var process = Process.GetCurrentProcess();
            Write("health", new { workingSetMiB = process.WorkingSet64 / 1048576, privateMiB = process.PrivateMemorySize64 / 1048576,
                cpuSeconds = Math.Round(process.TotalProcessorTime.TotalSeconds, 2),
                droppedEvents = Interlocked.Read(ref dropped), writeErrors = Interlocked.Read(ref writeErrors) });
        }
        catch (InvalidOperationException) { }
    }

    private async Task WriteLoop()
    {
        StreamWriter? output = null;
        long size = 0;
        try
        {
            await foreach (var line in queue.Reader.ReadAllAsync())
            {
                try
                {
                    Directory.CreateDirectory(directory);
                    var current = Path.Combine(directory, "activity.jsonl");
                    if (output == null) size = File.Exists(current) ? new FileInfo(current).Length : 0;
                    var bytes = Encoding.UTF8.GetByteCount(line) + 1;
                    if (size + bytes > segmentBytes)
                    {
                        output?.Dispose(); output = null;
                        // Four segments total, bounded to approximately 20 MiB.
                        for (var index = 3; index >= 1; index--)
                        {
                            var source = index == 1 ? current : Path.Combine(directory, $"activity.{index - 1}.jsonl");
                            if (File.Exists(source)) File.Move(source, Path.Combine(directory, $"activity.{index}.jsonl"), true);
                        }
                        size = 0;
                    }
                    output ??= new StreamWriter(new FileStream(current, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false))
                        { AutoFlush = true, NewLine = "\n" };
                    await output.WriteLineAsync(line);
                    size += bytes;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    Interlocked.Increment(ref writeErrors);
                    output?.Dispose(); output = null;
                    await Task.Delay(500);
                }
            }
        }
        finally { output?.Dispose(); }
    }

    public void Dispose()
    {
        metrics.Dispose();
        Write("service_stopped", new { droppedEvents = Interlocked.Read(ref dropped), writeErrors = Interlocked.Read(ref writeErrors) });
        queue.Writer.TryComplete();
        try { writer.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
    }
}
