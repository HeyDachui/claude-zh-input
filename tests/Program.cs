using ClaudeZhInput;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

Console.OutputEncoding = new System.Text.UTF8Encoding(false);

// A private copy of this test app supplies the worker protocol without a model.
if (string.Equals(Path.GetFileName(Environment.ProcessPath), "worker.exe", StringComparison.OrdinalIgnoreCase))
{
    while (Console.ReadLine() is string line)
    {
        using var command = JsonDocument.Parse(line);
        var prepare = command.RootElement.TryGetProperty("op", out var op) && op.GetString() == "prepare";
        File.AppendAllText("requests.log", prepare ? "prepare\n" : "translate\n");
        Console.WriteLine(prepare
            ? "{\"ready\":true}"
            : JsonSerializer.Serialize(new { text = "Test translation", engine = "test worker", seconds = 0, warnings = Array.Empty<string>() }));
    }
    return;
}

int passed = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); passed++; Console.WriteLine("PASS: " + name); }
var source = new[] { new Phrase("不要修改代码", "Do not modify code.", "xiugai xg"), new Phrase("运行测试", "Run tests.", "ceshi cs") };
Check(PhraseMatcher.Suggest("不要", source)[0].Zh == "不要修改代码", "Chinese prefix completion");
Check(PhraseMatcher.Suggest("xg", source)[0].Zh == "不要修改代码", "pinyin initials");
Check(PhraseMatcher.Complete("先检查，不要", "不要修改代码") == "先检查，不要修改代码", "completion preserves earlier clauses");
Check(PhraseMatcher.Key("继续？") != PhraseMatcher.Key("继续"), "questions do not become commands");
Check(PhraseMatcher.Key(" 继续。 ") == "继续" && PhraseMatcher.Key("继续.") == "继续", "single terminal full stop keeps existing normalization");
Check(PhraseMatcher.Key("继续...") != PhraseMatcher.Key("继续") && PhraseMatcher.Key("继续。。") != PhraseMatcher.Key("继续"), "ellipsis is not a full stop");
var dir = Path.Combine(Path.GetTempPath(), "ClaudeZhInput-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(dir);
File.WriteAllText(Path.Combine(dir,"defaults.json"), "[]");
var path = Path.Combine(dir,"personal.json");
var store = new PhraseStore(Path.Combine(dir,"defaults.json"), path);
Check(!store.All.Any(), "existing empty defaults replace built-in defaults");

File.WriteAllText(Path.Combine(dir, "phrases.dat"), "protected phrase resource");
var fallback = new PhraseStore(Path.Combine(dir, "phrases.json"), Path.Combine(dir, "fallback-personal.json"));
Check(fallback.All.Count() == BuiltInPhrases.Count && BuiltInPhrases.Count >= 50, "protected package without phrases.json has all built-in defaults and retains the original 50");
Check(BuiltInPhrases.Create().All(phrase => fallback.Exact(phrase.Zh) == phrase), "all built-in translations, keys and usage counts survive fallback");
Check(fallback.Exact("继续当前任务")?.En == "Continue the current task.", "built-in default exact translation is available without worker");
Check(fallback.CouldComplete("") && fallback.CouldComplete("先检查") && fallback.CouldComplete("继续"), "empty, partial and complete phrase prefixes can complete");
Check(!fallback.CouldComplete("jixu"), "completion feasibility uses confirmed text rather than pinyin keys");
foreach (var text in new[] { "继续？", "继续?", "继续!", "继续！", "继续...", "继续。。", "继续，等我确认", "不要继续", "如果失败就继续", "继续当前任务，但不要修改文件" })
{
    Check(fallback.Exact(text) == null && !fallback.CouldComplete(text), "extra condition, negation or punctuation does not match: " + text);
}
Check(fallback.Exact(" 继续。 ")?.En == "Continue." && fallback.Exact("继续.")?.En == "Continue.", "exact index retains single full stop normalization");

var externalPath = Path.Combine(dir, "external.json");
File.WriteAllText(externalPath, JsonSerializer.Serialize(new[] { new Phrase("继续", "External default."), new Phrase("检查 API_v2", "Inspect API_v2.") }, PhraseStore.JsonOptions));
var external = new PhraseStore(externalPath, Path.Combine(dir, "external-personal.json"));
Check(external.All.Count() == 2 && external.Exact("继续")?.En == "External default." && external.Exact("用中文回复") == null, "external defaults take precedence over built-in defaults");
Check(external.Exact("检查 API_v2")?.En == "Inspect API_v2." && external.CouldComplete("检查 API"), "literal code identifier can match exactly and by prefix");
foreach (var text in new[] { "检查 api_v2", "检查 API-v2", "检查 API_v2?", "检查  API_v2" })
    Check(external.Exact(text) == null && !external.CouldComplete(text), "case and symbols remain significant: " + text);

fallback.Remember("继续。", "My correction.");
Check(fallback.Exact("继续")?.En == "My correction." && fallback.All.Count(p => PhraseMatcher.Key(p.Zh) == "继续") == 1, "Remember updates exact index and personal correction overrides default");
fallback.Remember("继续", "My second correction.");
Check(fallback.Exact("继续") is { En: "My second correction.", Uses: 2 }, "Remember replaces normalized key and increments personal usage");
var personalLoaded = new PhraseStore(Path.Combine(dir, "missing-defaults.json"), Path.Combine(dir, "fallback-personal.json"));
Check(personalLoaded.Exact("继续")?.En == "My second correction.", "loaded personal phrase overrides built-in default");
var beforeRemember = fallback.All;
fallback.Remember("自定义新前缀的完整短语", "A complete custom phrase.");
Check(!beforeRemember.Any(p => p.Zh == "自定义新前缀的完整短语") && fallback.CouldComplete("自定义新前缀"), "All is a stable snapshot and Remember updates completion index");

using (var start = new ManualResetEventSlim())
{
    var readers = Enumerable.Range(0, 4).Select(reader => Task.Run(() =>
    {
        start.Wait();
        for (int round = 0; round < 200; ++round)
        {
            foreach (var phrase in fallback.All) { _ = phrase.Zh.Length; Thread.Yield(); }
            _ = fallback.Exact("继续");
            _ = fallback.CouldComplete("并发短语");
        }
    })).ToArray();
    var writer = Task.Run(() =>
    {
        start.Wait();
        for (int index = 0; index < 80; ++index) fallback.Remember("并发短语" + index, "Concurrent phrase " + index + ".");
    });
    start.Set();
    await Task.WhenAll(readers.Append(writer));
}
Check(fallback.Exact("并发短语79")?.En == "Concurrent phrase 79." && fallback.CouldComplete("并发短语7"), "parallel enumeration, exact lookup, completion and Remember remain safe");
store.Remember("保留 `UserID`", "Keep `UserID`.");
var loaded = new PhraseStore(Path.Combine(dir,"defaults.json"), path);
Check(loaded.Exact("保留 `UserID`")?.En == "Keep `UserID`.", "personal corrected phrase roundtrip");
File.WriteAllText(path,"broken-json");
var broken = new PhraseStore(Path.Combine(dir,"defaults.json"), path);
Check(broken.LoadWarning != null, "corrupt store surfaced");
try { broken.Remember("中文", "English"); throw new Exception("corrupt file overwritten"); } catch(InvalidOperationException) {}
Check(File.ReadAllText(path)=="broken-json", "corrupt store preserved");

var packTestRoot = Path.Combine(dir, "pack-tests");
var packInputRoot = Path.Combine(packTestRoot, "inputs");
Directory.CreateDirectory(packInputRoot);
object Entry(string zh, string en, string keys = "") => new { zh, en, keys };
string WritePack(string id, IEnumerable<object> entries, string version = "1.0.0", string sourceLanguage = "zh-CN", string targetLanguage = "en")
{
    var file = Path.Combine(packInputRoot, Guid.NewGuid().ToString("N") + ".json");
    File.WriteAllText(file, JsonSerializer.Serialize(new { schemaVersion = 1, id, name = "测试语包", version, sourceLanguage, targetLanguage, phrases = entries }));
    return file;
}
bool RejectPack(Action action)
{
    try { action(); return false; }
    catch (InvalidDataException ex) { return ex.Message.Length < 400; }
}
bool FailsIo(Action action)
{
    try { action(); return false; }
    catch (IOException) { return true; }
    catch (UnauthorizedAccessException) { return true; }
}
var packPersonalPath = Path.Combine(packTestRoot, "data", "phrases.json");
var packDefaultsPath = Path.Combine(packTestRoot, "app", "missing-defaults.json");
var packStore = new PhraseStore(packDefaultsPath, packPersonalPath);
var largePackFile = WritePack("developer-common", Enumerable.Range(0, 260)
    .Select(index => (object)new { id = "entry-" + index, zh = "语包验证短语" + index, en = "Pack phrase " + index + ".", keys = "packalias-" + index }));
var largePackOriginal = File.ReadAllBytes(largePackFile);
var inspected = packStore.InspectPack(largePackFile);
Check(inspected is { IsUpdate: false, Pack.PhraseCount: 260 } && !Directory.Exists(packStore.PackDirectory), "pack inspection validates all entries without writing or activating them");
packStore.ImportPack(largePackFile);
Check(packStore.ListPacks() is [{ Id: "developer-common", PhraseCount: 260, IsActive: true }] &&
      packStore.Exact("语包验证短语259")?.En == "Pack phrase 259." && packStore.CouldComplete("语包验证短语25"), "pack import retains more than 200 entries and immediately updates exact and completion indexes");
Check(packStore.Exact("packalias-259") == null && !packStore.CouldComplete("packalias-259") &&
      PhraseMatcher.Suggest("packalias-259", packStore.All).Any(phrase => phrase.Zh == "语包验证短语259"), "pack keys affect suggestions without becoming exact translation or completion aliases");
var stablePackList = packStore.ListPacks();
packStore.Remember("语包验证短语259", "My personal correction.");
Check(packStore.Exact("语包验证短语259")?.En == "My personal correction." && packStore.InspectPack(largePackFile).PersonalOverrideCount == 1, "personal corrections take precedence and import inspection reports affected personal phrases");
var packRestart = new PhraseStore(packDefaultsPath, packPersonalPath);
Check(packRestart.ListPacks().Count == 1 && packRestart.Exact("语包验证短语258")?.En == "Pack phrase 258." &&
      packRestart.Exact("语包验证短语259")?.En == "My personal correction.", "restart automatically loads independent pack JSON and preserves personal priority");
var sharedFile = WritePack("shared-same", new[] { Entry("语包验证短语0", "Pack phrase 0.") });
Check(packStore.InspectPack(sharedFile).SharedPhraseCount == 1, "same translation shared between two packs is explicitly reported");
packStore.ImportPack(sharedFile);
var builtInPhrase = BuiltInPhrases.Create().First();
var builtInShare = WritePack("shared-default", new[] { Entry(builtInPhrase.Zh, builtInPhrase.En) });
Check(packStore.InspectPack(builtInShare).SharedPhraseCount == 1, "same translation shared with defaults is allowed and reported");
packStore.ImportPack(builtInShare);
var beforeConflict = File.ReadAllBytes(Path.Combine(packStore.PackDirectory, "developer-common.json"));
var conflictingPack = WritePack("conflict-pack", new[] { Entry("语包验证短语0。", "A different translation.") });
var conflictingDefault = WritePack("conflict-default", new[] { Entry(builtInPhrase.Zh, "A different built-in translation.") });
Check(RejectPack(() => packStore.InspectPack(conflictingPack)) && RejectPack(() => packStore.ImportPack(conflictingPack)) &&
      RejectPack(() => packStore.ImportPack(conflictingDefault)), "preflight and import reject differing translations across packs and defaults");
Check(beforeConflict.SequenceEqual(File.ReadAllBytes(Path.Combine(packStore.PackDirectory, "developer-common.json"))) &&
      !File.Exists(Path.Combine(packStore.PackDirectory, "conflict-pack.json")) &&
      packStore.Exact("语包验证短语0")?.En == "Pack phrase 0.", "translation conflicts preserve installed disk files and memory");

var invalidPacks = new List<string>
{
    WritePack("../escape", new[] { Entry("越界", "Escape.") }),
    WritePack("con", new[] { Entry("保留名称", "Reserved.") }),
    WritePack("bad-language", new[] { Entry("语言", "Language.") }, targetLanguage: "fr"),
    WritePack("bad-source", new[] { Entry("语言", "Language.") }, sourceLanguage: "en"),
    WritePack("empty-en", new[] { Entry("非空中文", "   ") }),
    WritePack("empty-zh", new[] { Entry("  ", "Nonempty.") }),
    WritePack("empty-key", new[] { Entry("。", "Full stop.") }),
    WritePack("control-entry", new[] { Entry("控制\n字符", "Control.") }),
    WritePack("control-keys", new[] { Entry("控制别名", "Control alias.", "alias\tother") }),
    WritePack("duplicate-key", new[] { Entry("重复中文", "Same."), Entry("重复中文。", "Same.") }),
    WritePack("duplicate-id", new object[] { new { id = "same-id", zh = "甲", en = "A." }, new { id = "same-id", zh = "乙", en = "B." } }),
    WritePack("unsafe-entry-id", new object[] { new { id = "../escape", zh = "甲", en = "A." } }),
    WritePack("null-entry", new object[] { null! }),
    WritePack("long-entry", new[] { Entry(new string('长', 4001), "Too long.") }),
    WritePack("empty-pack", Array.Empty<object>()),
    WritePack("too-many", Enumerable.Range(0, PhraseStore.MaxPackPhrases + 1).Select(index => Entry("条数" + index, "Count " + index)))
};
foreach (var invalidJson in new[]
{
    "broken-json",
    "{\"schemaVersion\":1,\"id\":\"repeat\",\"id\":\"repeat\"}",
    "{\"schemaVersion\":1,\"id\":\"missing-fields\",\"phrases\":[]}",
    "{\"schemaVersion\":1,\"id\":\"unknown-field\",\"downloadUrl\":\"https://example.com/external.json\"}",
    "{\"schemaVersion\":999}",
    "{\"schemaVersion\":\"1\"}",
    new string(' ', PhraseStore.MaxPackBytes + 1)
})
{
    var file = Path.Combine(packInputRoot, Guid.NewGuid().ToString("N") + ".json");
    File.WriteAllText(file, invalidJson);
    invalidPacks.Add(file);
}
var filesBeforeRejections = Directory.EnumerateFiles(packStore.PackDirectory, "*", SearchOption.AllDirectories)
    .ToDictionary(file => file, File.ReadAllBytes);
Check(invalidPacks.All(file => RejectPack(() => packStore.ImportPack(file))), "malformed JSON, unsupported schema or fields, unsafe IDs, nulls, empties, control characters, duplicate keys and size or count limits are rejected");
Check(filesBeforeRejections.Count == Directory.EnumerateFiles(packStore.PackDirectory, "*", SearchOption.AllDirectories).Count() &&
      filesBeforeRejections.All(item => File.ReadAllBytes(item.Key).SequenceEqual(item.Value)) &&
      largePackOriginal.SequenceEqual(File.ReadAllBytes(largePackFile)), "invalid imports preserve all installed pack bytes and the original source");
Check(RejectPack(() => packStore.InspectPack("https://example.com/phrases.json")) &&
      RejectPack(() => packStore.InspectPack(Path.Combine(packInputRoot, "pack.zip"))) &&
      RejectPack(() => packStore.RemovePack("../escape")), "external URLs, archives and traversal in public IDs are refused");

var firstUpdateFile = WritePack("updatable", new[] { Entry("独立更新短语", "Before update.") });
var secondUpdateFile = WritePack("updatable", new[] { Entry("独立更新短语", "After update."), Entry("更新新增句", "New entry.") }, version: "2.0.0");
packStore.ImportPack(firstUpdateFile);
var firstUpdateBytes = File.ReadAllBytes(Path.Combine(packStore.PackDirectory, "updatable.json"));
Check(packStore.InspectPack(secondUpdateFile).IsUpdate, "preflight identifies updates by stable pack ID");
packStore.ImportPack(secondUpdateFile);
var updateBackup = packStore.LastPackBackupPath!;
Check(File.ReadAllBytes(updateBackup).SequenceEqual(firstUpdateBytes) &&
      packStore.Exact("独立更新短语")?.En == "After update." && packStore.CouldComplete("更新新增"), "same-ID update atomically replaces the pack and preserves an exact old-file backup");
Check(new PhraseStore(packDefaultsPath, packPersonalPath).Exact("独立更新短语")?.En == "After update.", "updated pack persists across restart without loading the backup as another pack");
packStore.ImportPack(updateBackup);
Check(packStore.Exact("独立更新短语")?.En == "Before update." && packStore.Exact("更新新增句") == null, "an old pack backup can be imported to roll back translations and removed entries");
if (OperatingSystem.IsWindows())
{
    var lockedPackPath = Path.Combine(packStore.PackDirectory, "updatable.json");
    var lockedBytes = File.ReadAllBytes(lockedPackPath);
    var backupCount = Directory.EnumerateFiles(Path.Combine(packStore.PackDirectory, "backups")).Count();
    using (var lockedPack = new FileStream(lockedPackPath, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        Check(FailsIo(() => packStore.ImportPack(secondUpdateFile)) &&
              File.ReadAllBytes(lockedPackPath).SequenceEqual(lockedBytes) &&
              packStore.Exact("独立更新短语")?.En == "Before update." &&
              Directory.EnumerateFiles(Path.Combine(packStore.PackDirectory, "backups")).Count() == backupCount,
              "Windows sharing failure during atomic replacement preserves the original pack, backups and active index");
        Check(FailsIo(() => packStore.RemovePack("updatable")) &&
              File.ReadAllBytes(lockedPackPath).SequenceEqual(lockedBytes) &&
              packStore.Exact("独立更新短语")?.En == "Before update.",
              "Windows sharing failure during removal preserves the original pack and active index");
    }
}

var failureStore = new PhraseStore(packDefaultsPath, Path.Combine(packTestRoot, "failure", "phrases.json"));
failureStore.ImportPack(firstUpdateFile);
var failurePackPath = Path.Combine(failureStore.PackDirectory, "updatable.json");
var failureBytes = File.ReadAllBytes(failurePackPath);
var blockedBackupPath = Path.Combine(failureStore.PackDirectory, "backups");
File.WriteAllText(blockedBackupPath, "An existing file prevents creation of the backup directory.");
Check(FailsIo(() => failureStore.ImportPack(secondUpdateFile)) && failureStore.Exact("独立更新短语")?.En == "Before update." &&
      File.ReadAllBytes(failurePackPath).SequenceEqual(failureBytes), "failed update backup creation leaves memory and original disk bytes unchanged");
Check(FailsIo(() => failureStore.RemovePack("updatable")) && failureStore.Exact("独立更新短语")?.En == "Before update." &&
      File.ReadAllBytes(failurePackPath).SequenceEqual(failureBytes), "failed removal leaves the original pack and its active index unchanged");
var blockedNewFile = WritePack("blocked-new", new[] { Entry("不可写新包", "Blocked.") });
Directory.CreateDirectory(Path.Combine(failureStore.PackDirectory, "blocked-new.json"));
Check(FailsIo(() => failureStore.ImportPack(blockedNewFile)) && failureStore.Exact("不可写新包") == null &&
      !Directory.EnumerateFiles(failureStore.PackDirectory, "*.tmp").Any(), "failed initial pack commit does not activate entries or leave temporary files");

packStore.Remember("不得公开的个人句", "Personal secret never exported.");
var exportedPack = Path.Combine(packTestRoot, "public-developer-common.json");
packStore.ExportPack("developer-common", exportedPack);
using (var exported = JsonDocument.Parse(File.ReadAllText(exportedPack)))
{
    var phrases = exported.RootElement.GetProperty("phrases");
    Check(exported.RootElement.GetProperty("id").GetString() == "developer-common" && phrases.GetArrayLength() == 260 &&
          !exported.RootElement.TryGetProperty("files", out _) &&
          !File.ReadAllText(exportedPack).Contains("Personal secret") && !File.ReadAllText(exportedPack).Contains("My personal correction") &&
          !File.ReadAllText(exportedPack).Contains("Before update"), "public export contains only the selected original pack, without personal corrections, settings or other packs");
}
var exportConsumer = new PhraseStore(packDefaultsPath, Path.Combine(packTestRoot, "consumer", "phrases.json"));
exportConsumer.ImportPack(exportedPack);
Check(exportConsumer.Exact("语包验证短语259")?.En == "Pack phrase 259.", "the exported public JSON is independently importable with the original pack translation");
var personalBytesBeforeExport = File.ReadAllBytes(packPersonalPath);
Check(RejectPack(() => packStore.ExportPack("developer-common", packPersonalPath)) &&
      RejectPack(() => packStore.ExportPack("developer-common", packDefaultsPath)) &&
      RejectPack(() => packStore.ExportPack("developer-common", Path.Combine(packStore.PackDirectory, "updatable.json"))) &&
      personalBytesBeforeExport.SequenceEqual(File.ReadAllBytes(packPersonalPath)), "export cannot overwrite personal data, defaults or managed pack files");
var beforeRemovalBytes = File.ReadAllBytes(Path.Combine(packStore.PackDirectory, "developer-common.json"));
Check(packStore.RemovePack("developer-common") && packStore.Exact("语包验证短语258") == null &&
      packStore.Exact("语包验证短语259")?.En == "My personal correction." &&
      packStore.Exact("语包验证短语0")?.En == "Pack phrase 0." &&
      File.ReadAllBytes(packStore.LastPackBackupPath!).SequenceEqual(beforeRemovalBytes), "removal updates indexes immediately, keeps personal and other-pack phrases, and backs up the removed pack");
Check(stablePackList.Count == 1 && !packStore.RemovePack("missing-pack") &&
      !new PhraseStore(packDefaultsPath, packPersonalPath).ListPacks().Any(pack => pack.Id == "developer-common"), "pack list snapshots are stable and removal persists after restart");
packStore.ImportPack(packStore.LastPackBackupPath!);
Check(packStore.Exact("语包验证短语258") != null, "a removed pack can be restored by importing its backup");

var manualRoot = Path.Combine(packTestRoot, "manual");
var manualPacks = Path.Combine(manualRoot, "phrase-packs");
Directory.CreateDirectory(manualPacks);
File.Copy(WritePack("manual-one", new[] { Entry("手工冲突短语", "First translation.") }), Path.Combine(manualPacks, "manual-one.json"));
File.Copy(WritePack("manual-two", new[] { Entry("手工冲突短语", "Second translation.") }), Path.Combine(manualPacks, "manual-two.json"));
File.WriteAllText(Path.Combine(manualPacks, "broken-pack.json"), "not-json");
var manualStore = new PhraseStore(packDefaultsPath, Path.Combine(manualRoot, "phrases.json"));
Check(manualStore.Exact("手工冲突短语") == null && manualStore.ListPacks().Count == 3 &&
      manualStore.ListPacks().All(pack => !pack.IsActive) && manualStore.PackWarnings.Count == 3 &&
      File.ReadAllText(Path.Combine(manualPacks, "broken-pack.json")) == "not-json", "startup preserves bad JSON and explicitly disables both packs in a differing-translation conflict");
Check(manualStore.ListPacks().Single(pack => pack.Id == "broken-pack") is { IsActive: false, CanExport: false, PhraseCount: 0 } &&
      RejectPack(() => manualStore.ExportPack("broken-pack", Path.Combine(packTestRoot, "invalid-export.json"))),
      "a safely named damaged pack is listed for removal and cannot be exported");
manualStore.Remember("个人仍可保存", "Personal data still works.");
Check(manualStore.RemovePack("manual-two") && manualStore.Exact("手工冲突短语")?.En == "First translation." &&
      manualStore.ListPacks().Single(pack => pack.CanExport).IsActive && manualStore.PackWarnings.Count == 1, "removing a startup conflict activates the remaining pack and pack warnings do not block personal corrections");
Check(manualStore.RemovePack("broken-pack") && manualStore.PackWarnings.Count == 0, "a damaged package can be removed by its safe filename ID with a preserved backup");
var unsafeNameRoot = Path.Combine(packTestRoot, "unsafe-filenames");
Directory.CreateDirectory(Path.Combine(unsafeNameRoot, "phrase-packs"));
var unsafeNamedFile = Path.Combine(unsafeNameRoot, "phrase-packs", "Unsafe Name.json");
File.WriteAllText(unsafeNamedFile, "not-json");
var unsafeNameStore = new PhraseStore(packDefaultsPath, Path.Combine(unsafeNameRoot, "phrases.json"));
Check(unsafeNameStore.ListPacks().Count == 0 && unsafeNameStore.PackWarnings.Count == 1 &&
      RejectPack(() => unsafeNameStore.RemovePack("Unsafe Name")) && File.ReadAllText(unsafeNamedFile) == "not-json",
      "unsafe damaged filenames remain warnings and never become public path-operation IDs");

var protectedRoot = Path.Combine(packTestRoot, "export-protection");
var protectedApp = Path.Combine(protectedRoot, "Application Long Directory");
var protectedData = Path.Combine(protectedRoot, "Personal Data Long Directory");
Directory.CreateDirectory(Path.Combine(protectedApp, "commands"));
Directory.CreateDirectory(protectedData);
var protectedDefaults = Path.Combine(protectedApp, "phrases.json");
var protectedPersonal = Path.Combine(protectedData, "phrases.json");
var protectedCommands = Path.Combine(protectedApp, "commands", "claude-commands.json");
File.WriteAllText(protectedDefaults, "[]");
File.WriteAllText(protectedCommands, "{\"commands\":[\"keep-command\"]}");
var protectedStore = new PhraseStore(protectedDefaults, protectedPersonal);
protectedStore.Remember("保留个人更正", "Preserve this personal correction.");
protectedStore.ImportPack(firstUpdateFile);
var protectedPersonalBytes = File.ReadAllBytes(protectedPersonal);
var protectedCommandBytes = File.ReadAllBytes(protectedCommands);
Check(RejectPack(() => protectedStore.ExportPack("updatable", protectedCommands)) &&
      RejectPack(() => protectedStore.ExportPack("updatable", Path.Combine(protectedApp, "new", "commands.json"))) &&
      RejectPack(() => protectedStore.ExportPack("updatable", Path.Combine(protectedData, "new", "public.json"))) &&
      !Directory.Exists(Path.Combine(protectedData, "new")) && !Directory.Exists(Path.Combine(protectedApp, "new")),
      "export protects the entire application and personal-data trees, including targets whose parent does not exist");
if (OperatingSystem.IsWindows())
{
    void Junction(string alias, string target)
    {
        var info = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "/c", "mklink", "/J", alias, target }) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new Exception("Junction fixture failed: " + output + error);
    }
    var dataAlias = Path.Combine(protectedRoot, "data-alias");
    var appAlias = Path.Combine(protectedRoot, "app-alias");
    Junction(dataAlias, protectedData);
    Junction(appAlias, protectedApp);
    Check(RejectPack(() => protectedStore.ExportPack("updatable", Path.Combine(dataAlias, "phrases.json"))) &&
          RejectPack(() => protectedStore.ExportPack("updatable", Path.Combine(dataAlias, "new-parent", "public.json"))) &&
          RejectPack(() => protectedStore.ExportPack("updatable", Path.Combine(appAlias, "commands", "claude-commands.json"))),
          "directory junction aliases cannot bypass personal-data or application export protection");
    var shortPath = new System.Text.StringBuilder(32768);
    var shortLength = TestNativePaths.GetShortPathNameW(protectedData, shortPath, (uint)shortPath.Capacity);
    if (shortLength > 0 && !string.Equals(shortPath.ToString(), protectedData, StringComparison.OrdinalIgnoreCase))
        Check(RejectPack(() => protectedStore.ExportPack("updatable", Path.Combine(shortPath.ToString(), "phrases.json"))) &&
              RejectPack(() => protectedStore.ExportPack("updatable", Path.Combine(shortPath.ToString(), "not-created", "public.json"))),
              "8.3 directory aliases cannot bypass protection for existing or new export targets");
    else
    {
        // New temporary directories may have 8.3 creation disabled. Reuse an
        // existing system alias for a read-only identity check; never write there.
        bool checkedExistingAlias = false;
        foreach (var existing in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) }.Where(Directory.Exists))
        {
            shortPath.Clear();
            shortLength = TestNativePaths.GetShortPathNameW(existing, shortPath, (uint)shortPath.Capacity);
            if (shortLength == 0 || string.Equals(shortPath.ToString(), existing, StringComparison.OrdinalIgnoreCase)) continue;
            var resolve = typeof(PhraseStore).GetMethod("ResolveWindowsPath", BindingFlags.Static | BindingFlags.NonPublic)!;
            var originalIdentity = (string)resolve.Invoke(null, new object[] { existing })!;
            var aliasIdentity = (string)resolve.Invoke(null, new object[] { shortPath.ToString() })!;
            Check(string.Equals(originalIdentity, aliasIdentity, StringComparison.OrdinalIgnoreCase),
                  "existing 8.3 aliases resolve to the same final identity used by export protection (read-only system-path check)");
            checkedExistingAlias = true;
            break;
        }
        if (!checkedExistingAlias) Console.WriteLine("SKIP: filesystem did not provide an existing 8.3 directory alias; junction and missing-parent paths were verified.");
    }
    var outsidePublic = Path.Combine(protectedRoot, "public-exports", "nested", "shared.json");
    protectedStore.ExportPack("updatable", outsidePublic);
    Check(File.Exists(outsidePublic) && new PhraseStore(protectedDefaults, protectedPersonal).Exact("保留个人更正")?.En == "Preserve this personal correction.",
          "a new unprotected export directory works while personal data survives restart");
    var guardMethod = typeof(PhraseStore).GetMethod("GuardExportTarget", BindingFlags.Instance | BindingFlags.NonPublic)!;
    using (var targetGuard = (IDisposable)guardMethod.Invoke(protectedStore, new object[] { outsidePublic })!)
    {
        var exportParent = Path.GetDirectoryName(outsidePublic)!;
        Check(FailsIo(() => Directory.Move(exportParent, exportParent + "-swapped")) && Directory.Exists(exportParent),
              "the export directory remains pinned against rename between validation and commit");
    }
}
Check(File.ReadAllBytes(protectedPersonal).SequenceEqual(protectedPersonalBytes) &&
      File.ReadAllBytes(protectedCommands).SequenceEqual(protectedCommandBytes),
      "all export alias and configuration regressions preserve personal and command files byte-for-byte");

// Holding the management lock models an arbitrarily slow disk operation deterministically.
// Input readers must finish while that lock is still held, regardless of machine speed.
var managementLock = typeof(PhraseStore).GetField("sync", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(packStore)!;
using (var writerEntered = new ManualResetEventSlim())
using (var releaseWriter = new ManualResetEventSlim())
{
    var writer = Task.Factory.StartNew(() =>
    {
        lock (managementLock) { writerEntered.Set(); releaseWriter.Wait(); }
    }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    writerEntered.Wait();
    var readers = Task.Factory.StartNew(() => packStore.Exact("语包验证短语258") != null &&
        packStore.CouldComplete("语包验证短语25") && packStore.All.Any(),
        CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    var completed = await Task.WhenAny(readers, Task.Delay(1000));
    releaseWriter.Set();
    await writer;
    Check(completed == readers && await readers, "Exact, CouldComplete and All never wait for a held management or disk-operation lock");
}
var immutableAll = (IList<Phrase>)packStore.All;
bool snapshotReadOnly = false;
try { immutableAll.Add(new Phrase("外部修改", "External mutation.")); }
catch (NotSupportedException) { snapshotReadOnly = true; }
Check(snapshotReadOnly, "All exposes a stable read-only snapshot that callers cannot mutate");

var perfStore = new PhraseStore(packDefaultsPath, Path.Combine(packTestRoot, "performance", "phrases.json"));
for (int packIndex = 0; packIndex < 16; packIndex++)
{
    var file = WritePack("perf-" + packIndex, Enumerable.Range(0, 10000)
        .Select(entryIndex => Entry("性能包" + packIndex + "条" + entryIndex, "Performance " + packIndex + ":" + entryIndex + ".")));
    perfStore.ImportPack(file);
}
var concurrentPackFile = WritePack("perf-16", Enumerable.Range(0, 10000)
    .Select(entryIndex => Entry("性能包16条" + entryIndex, "Performance 16:" + entryIndex + ".")));
var priorPerfCount = perfStore.All.Count();
var importer = Task.Run(() => perfStore.ImportPack(concurrentPackFile));
double maxInputMilliseconds = 0;
int inputSamples = 0;
bool completeSnapshots = true;
do
{
    var inputTimer = Stopwatch.StartNew();
    _ = perfStore.CouldComplete("没有这条性能前缀");
    _ = perfStore.Exact("性能包15条9999");
    var count = perfStore.All.Count();
    inputTimer.Stop();
    maxInputMilliseconds = Math.Max(maxInputMilliseconds, inputTimer.Elapsed.TotalMilliseconds);
    completeSnapshots &= count == priorPerfCount || count == priorPerfCount + 10000;
    inputSamples++;
    await Task.Delay(1);
} while (!importer.IsCompleted);
await importer;
Check(completeSnapshots && perfStore.Exact("性能包16条9999") != null && maxInputMilliseconds < 100,
      "170000-entry concurrent import publishes complete indexes and keeps sampled input reads below 100 ms");
Console.WriteLine($"PERF: entries={perfStore.All.Count()}; input_samples={inputSamples}; max_input_ms={maxInputMilliseconds:F3}");

var workerField = typeof(OfflineTranslator).GetField("worker", BindingFlags.Instance | BindingFlags.NonPublic)!;
using (var untouched = new OfflineTranslator(Path.Combine(dir, "does-not-exist")))
{
    for (int index = 0; index < 20; ++index) untouched.TouchIfRunning();
    Check(workerField.GetValue(untouched) == null, "TouchIfRunning alone never starts or creates a worker");
    untouched.Dispose();
    untouched.TouchIfRunning();
    Check(workerField.GetValue(untouched) == null, "TouchIfRunning cannot revive a disposed translator");
}

var workerRoot = Path.Combine(dir, "private-worker");
Directory.CreateDirectory(workerRoot);
var assemblyPath = Assembly.GetExecutingAssembly().Location;
var appHost = Path.ChangeExtension(assemblyPath, ".exe");
File.Copy(appHost, Path.Combine(workerRoot, "worker.exe"));
foreach (var extension in new[] { ".dll", ".deps.json", ".runtimeconfig.json" })
{
    var file = Path.ChangeExtension(assemblyPath, extension);
    File.Copy(file, Path.Combine(workerRoot, Path.GetFileName(file)));
}
using (var translator = new OfflineTranslator(workerRoot, TimeSpan.FromSeconds(1)))
{
    var result = await translator.Translate("继续当前任务", CancellationToken.None);
    Check(result.Text == "Test translation" && File.ReadAllLines(Path.Combine(workerRoot, "requests.log")).SequenceEqual(new[] { "translate" }), "Built-in Chinese phrases are sent directly without model preparation");
    await translator.PrepareAsync();
    Check(File.ReadAllLines(Path.Combine(workerRoot, "requests.log")).SequenceEqual(new[] { "translate", "prepare" }), "explicit preparation remains available");
    var active = (Process)workerField.GetValue(translator)!;
    await Task.Delay(600);
    translator.TouchIfRunning();
    await Task.Delay(600);
    Check(ReferenceEquals(workerField.GetValue(translator), active) && !active.HasExited, "TouchIfRunning renews the existing worker idle deadline");
    await Task.Delay(1500);
    Check(workerField.GetValue(translator) == null, "worker still exits after the renewed idle deadline");
    translator.TouchIfRunning();
    Check(workerField.GetValue(translator) == null, "TouchIfRunning does not restart an idle worker");
}
File.WriteAllText(Path.Combine(workerRoot, "requests.log"), "");
using (var translator = new OfflineTranslator(workerRoot))
{
    await translator.Translate("这是词库之外的新句子", CancellationToken.None);
    Check(File.ReadAllLines(Path.Combine(workerRoot, "requests.log")).SequenceEqual(new[] { "prepare", "translate" }), "Unknown Chinese keeps shared preparation before inference");
}
Console.WriteLine($"{passed} checks passed.");

internal static class TestNativePaths
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern uint GetShortPathNameW(string path, System.Text.StringBuilder shortPath, uint bufferLength);
}
