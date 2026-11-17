using System.IO;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace ClaudeZhInput;

public sealed record PhrasePackInfo(string Id, string Name, string Version, string SourceLanguage,
    string TargetLanguage, int PhraseCount, bool IsActive = true, bool CanExport = true);

public sealed record PhrasePackInspection(PhrasePackInfo Pack, bool IsUpdate,
    int PersonalOverrideCount, int SharedPhraseCount);

public sealed partial class PhraseStore
{
    public const int MaxPackBytes = 4 * 1024 * 1024;
    public const int MaxPackPhrases = 10000;
    public const int MaxInstalledPacks = 128;

    private sealed record PackEntry(string Zh, string En, string Keys, string? Id)
    {
        public Phrase Phrase => new(Zh, En, Keys);
    }

    private sealed record PackDocument(string Id, string Name, string Version,
        string SourceLanguage, string TargetLanguage, PackEntry[] Entries)
    {
        public IEnumerable<Phrase> Phrases => Entries.Select(entry => entry.Phrase);
        public PhrasePackInfo Info(bool active = true) => new(Id, Name, Version,
            SourceLanguage, TargetLanguage, Entries.Length, active);
    }

    private readonly List<PackDocument> packs = [];
    private PackDocument[] activePacks = [];
    private readonly Dictionary<string, string> packFileWarnings = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> unreadablePacks = new(StringComparer.Ordinal);
    private string[] packWarnings = [];
    private string? lastPackBackupPath;

    public string PackDirectory => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "phrase-packs");
    public IReadOnlyList<string> PackWarnings { get { lock (sync) return packWarnings.ToArray(); } }
    public string? LastPackBackupPath { get { lock (sync) return lastPackBackupPath; } }

    public IReadOnlyList<PhrasePackInfo> ListPacks()
    {
        lock (sync)
        {
            var active = activePacks.Select(pack => pack.Id).ToHashSet(StringComparer.Ordinal);
            return packs.Select(pack => pack.Info(active.Contains(pack.Id)))
                .Concat(unreadablePacks.Keys.Select(id => new PhrasePackInfo(id, "无法读取的语包（" + id + "）",
                    "未知", "zh-CN", "en", 0, IsActive: false, CanExport: false)))
                .OrderBy(pack => pack.Id, StringComparer.Ordinal).ToArray();
        }
    }

    public PhrasePackInspection InspectPack(string sourceFile)
    {
        var candidate = ReadPack(sourceFile);
        lock (sync) return InspectDocument(candidate);
    }

    public PhrasePackInfo ImportPack(string sourceFile)
    {
        var candidate = ReadPack(sourceFile);
        var serialized = SerializePack(candidate);
        lock (sync)
        {
            _ = InspectDocument(candidate);
            var destination = PackPath(candidate.Id);
            var backup = File.Exists(destination) ? NewPackBackupPath(candidate.Id) : null;
            WritePackAtomic(destination, serialized, backup, overwriteExisting: false);
            packs.RemoveAll(pack => pack.Id == candidate.Id);
            packs.Add(candidate);
            packFileWarnings.Remove(Path.GetFileName(destination));
            unreadablePacks.Remove(candidate.Id);
            lastPackBackupPath = backup;
            RecalculatePackState();
            RebuildIndex();
            return candidate.Info();
        }
    }

    public void ExportPack(string id, string destination)
    {
        PackDocument pack;
        lock (sync)
        {
            ValidateId(id, "语包 ID");
            if (unreadablePacks.ContainsKey(id))
                throw new InvalidDataException("所选语包无法读取，不能导出。可以移除，原文件会保留在备份目录。");
            pack = packs.FirstOrDefault(pack => pack.Id == id)
                ?? throw new InvalidDataException("找不到所选语包，请刷新列表。");
        }
        // The selected immutable document can be serialized without blocking writers.
        // Resolve aliases, then pin the real directory chain until the atomic rename finishes.
        using var target = GuardExportTarget(LocalJsonPath(destination));
        WritePackAtomic(target.Destination, SerializePack(pack), null);
    }

    public bool RemovePack(string id)
    {
        lock (sync)
        {
            ValidateId(id, "语包 ID");
            var file = unreadablePacks.TryGetValue(id, out var unreadableName)
                ? Path.Combine(PackDirectory, unreadableName) : PackPath(id);
            var exists = File.Exists(file);
            if (!exists && !packs.Any(pack => pack.Id == id) && !unreadablePacks.ContainsKey(id)) return false;
            string? backup = null;
            if (exists)
            {
                backup = NewPackBackupPath(id);
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                File.Move(file, backup);
            }
            packs.RemoveAll(pack => pack.Id == id);
            packFileWarnings.Remove(Path.GetFileName(file));
            unreadablePacks.Remove(id);
            lastPackBackupPath = backup;
            RecalculatePackState();
            RebuildIndex();
            return true;
        }
    }

    private void LoadPacks()
    {
        if (!Directory.Exists(PackDirectory)) return;
        try
        {
            var files = Directory.EnumerateFiles(PackDirectory, "*.json", SearchOption.TopDirectoryOnly)
                .OrderBy(file => file, StringComparer.Ordinal).ToArray();
            if (files.Length > MaxInstalledPacks)
                packFileWarnings[""] = $"语包文件超过 {MaxInstalledPacks} 个；超出部分未启用，请移走多余文件。";
            for (var fileIndex = 0; fileIndex < files.Length; fileIndex++)
            {
                var file = files[fileIndex];
                var name = Path.GetFileName(file);
                try
                {
                    if (fileIndex >= MaxInstalledPacks)
                        throw new InvalidDataException("超过可启用的语包数量上限；可以从列表移除多余语包。");
                    var pack = ReadPack(file);
                    if (!string.Equals(name, pack.Id + ".json", StringComparison.Ordinal))
                        throw new InvalidDataException("文件名必须与语包 ID 一致；请通过导入安装。");
                    packs.Add(pack);
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
                {
                    packFileWarnings[name] = $"语包文件“{ShortText(name)}”未启用：" +
                        (ex is InvalidDataException ? ex.Message : "文件暂时无法读取，原文件已保留。");
                    var fileId = Path.GetFileNameWithoutExtension(name);
                    try
                    {
                        ValidateId(fileId, "语包 ID");
                        unreadablePacks.TryAdd(fileId, name);
                    }
                    catch (InvalidDataException) { } // Unsafe filenames remain warnings only.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            packFileWarnings[""] = "语包目录暂时无法读取；本次仍可翻译，原文件已保留。";
        }
        RecalculatePackState();
    }

    // Files manually placed in the data directory receive the same checks as imports.
    // Reject all participants in a conflict, so file order never chooses a translation.
    private void RecalculatePackState()
    {
        var warnings = packFileWarnings.Values.ToList();
        var rejected = new HashSet<string>(StringComparer.Ordinal);
        var defaultIndex = defaults.GroupBy(phrase => PhraseMatcher.Key(phrase.Zh), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().En.Trim(), StringComparer.Ordinal);
        var owners = new Dictionary<string, List<(PackDocument Pack, PackEntry Entry)>>(StringComparer.Ordinal);
        foreach (var pack in packs)
        {
            foreach (var entry in pack.Entries)
            {
                var key = PhraseMatcher.Key(entry.Zh);
                if (defaultIndex.TryGetValue(key, out var en) && en != entry.En)
                {
                    if (rejected.Add(pack.Id))
                        warnings.Add($"语包“{pack.Id}”未启用：短语“{ShortText(entry.Zh)}”与内置短语译文不同。");
                }
                if (!owners.TryGetValue(key, out var list)) owners[key] = list = [];
                list.Add((pack, entry));
            }
        }
        foreach (var group in owners.Values.Where(group => group.Select(item => item.Entry.En).Distinct(StringComparer.Ordinal).Skip(1).Any()))
        {
            foreach (var item in group)
                if (rejected.Add(item.Pack.Id))
                    warnings.Add($"语包“{item.Pack.Id}”未启用：短语“{ShortText(item.Entry.Zh)}”与其他语包译文不同。");
        }
        activePacks = packs.Where(pack => !rejected.Contains(pack.Id)).OrderBy(pack => pack.Id, StringComparer.Ordinal).ToArray();
        packWarnings = warnings.ToArray();
    }

    private PhrasePackInspection InspectDocument(PackDocument candidate)
    {
        var isUpdate = packs.Any(pack => pack.Id == candidate.Id) || File.Exists(PackPath(candidate.Id));
        var fileCount = Directory.Exists(PackDirectory)
            ? Directory.EnumerateFiles(PackDirectory, "*.json", SearchOption.TopDirectoryOnly).Take(MaxInstalledPacks + 1).Count()
            : 0;
        if (fileCount > MaxInstalledPacks || !isUpdate && fileCount >= MaxInstalledPacks)
            throw new InvalidDataException($"最多可安装 {MaxInstalledPacks} 个语包，请先移除不需要的语包。");
        var known = new Dictionary<string, (string En, string Owner)>(StringComparer.Ordinal);
        var candidateKeys = candidate.Entries.Select(entry => PhraseMatcher.Key(entry.Zh)).ToHashSet(StringComparer.Ordinal);
        foreach (var phrase in defaults)
            known.TryAdd(PhraseMatcher.Key(phrase.Zh), (phrase.En.Trim(), "内置短语"));
        foreach (var pack in packs.Where(pack => pack.Id != candidate.Id))
        {
            foreach (var entry in pack.Entries)
            {
                var key = PhraseMatcher.Key(entry.Zh);
                if (known.TryGetValue(key, out var current) && current.En != entry.En &&
                    candidateKeys.Contains(key))
                    throw new InvalidDataException($"短语“{ShortText(entry.Zh)}”在已安装语包中有不同译文；请先移除冲突语包。");
                known.TryAdd(key, (entry.En, $"语包“{pack.Id}”"));
            }
        }
        var personalKeys = personal.Select(phrase => PhraseMatcher.Key(phrase.Zh)).ToHashSet(StringComparer.Ordinal);
        int shared = 0, overrides = 0;
        foreach (var entry in candidate.Entries)
        {
            var key = PhraseMatcher.Key(entry.Zh);
            if (known.TryGetValue(key, out var current))
            {
                if (current.En != entry.En)
                    throw new InvalidDataException($"短语“{ShortText(entry.Zh)}”与{current.Owner}译文不同，未导入。请修改语包，或用个人短语保留自己的译法。");
                shared++;
            }
            if (personalKeys.Contains(key)) overrides++;
        }
        return new(candidate.Info(), isUpdate, overrides, shared);
    }

    private string PackPath(string id)
    {
        ValidateId(id, "语包 ID");
        return Path.Combine(PackDirectory, id + ".json");
    }

    private string NewPackBackupPath(string id) => Path.Combine(PackDirectory, "backups",
        id + "-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff") + "-" + Guid.NewGuid().ToString("N")[..8] + ".json");

    private static PackDocument ReadPack(string sourceFile)
    {
        var fullPath = LocalJsonPath(sourceFile);
        try
        {
            using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaxPackBytes) throw new InvalidDataException("语包超过 4 MiB，未导入。");
            // The bounded copy also catches a file growing while it is being read.
            using var bytes = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = stream.Read(buffer)) != 0)
            {
                if (bytes.Length + read > MaxPackBytes) throw new InvalidDataException("语包超过 4 MiB，未导入。");
                bytes.Write(buffer, 0, read);
            }
            var content = new UTF8Encoding(false, true).GetString(bytes.ToArray()).TrimStart('\uFEFF');
            using var document = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            CheckFields(root, ["schemaVersion", "id", "name", "version", "sourceLanguage", "targetLanguage", "phrases"], "语包");
            if (!root.TryGetProperty("schemaVersion", out var schema) || !schema.TryGetInt32(out var schemaVersion) || schemaVersion != 1)
                throw new InvalidDataException("不支持此语包格式版本；仅支持 schemaVersion 1。");
            var id = TextField(root, "id", 64);
            ValidateId(id, "语包 ID");
            var name = TextField(root, "name", 80);
            var version = TextField(root, "version", 40);
            if (!Regex.IsMatch(version, @"^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$", RegexOptions.CultureInvariant))
                throw new InvalidDataException("语包 version 必须使用如 1.0.0 的版本号。");
            var sourceLanguage = TextField(root, "sourceLanguage", 16);
            var targetLanguage = TextField(root, "targetLanguage", 16);
            if (sourceLanguage != "zh-CN" || targetLanguage != "en")
                throw new InvalidDataException("目前只支持 zh-CN → en 的中英语包。");
            if (!root.TryGetProperty("phrases", out var phrases) || phrases.ValueKind != JsonValueKind.Array ||
                phrases.GetArrayLength() is < 1 or > MaxPackPhrases)
                throw new InvalidDataException($"语包 phrases 必须包含 1 至 {MaxPackPhrases} 条短语。");
            var entries = new List<PackEntry>();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in phrases.EnumerateArray())
            {
                CheckFields(entry, ["id", "zh", "en", "keys"], "短语");
                var zh = TextField(entry, "zh", 4000);
                var en = TextField(entry, "en", 4000);
                var aliases = TextField(entry, "keys", 512, optional: true);
                var phraseId = entry.TryGetProperty("id", out _) ? TextField(entry, "id", 64) : null;
                if (phraseId != null)
                {
                    ValidateId(phraseId, "短语 ID");
                    if (!ids.Add(phraseId)) throw new InvalidDataException("语包包含重复的短语 ID，未导入。");
                }
                var key = PhraseMatcher.Key(zh);
                if (key.Length == 0) throw new InvalidDataException("短语 zh 不能只有空白或句号。");
                if (!keys.Add(key)) throw new InvalidDataException($"语包包含重复中文键“{ShortText(zh)}”，未导入。");
                entries.Add(new(zh, en, aliases, phraseId));
            }
            var result = new PackDocument(id, name, version, sourceLanguage, targetLanguage, entries.ToArray());
            if (Encoding.UTF8.GetByteCount(SerializePack(result)) > MaxPackBytes)
                throw new InvalidDataException("语包整理为标准格式后超过 4 MiB，请拆分为多个语包。");
            return result;
        }
        catch (Exception ex) when (ex is JsonException or DecoderFallbackException or InvalidOperationException)
        {
            throw new InvalidDataException("语包不是有效的 UTF-8 JSON，或字段类型不正确。原文件未改动。", ex);
        }
    }

    private static void CheckFields(JsonElement element, string[] allowed, string context)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new InvalidDataException(context + "必须是 JSON 对象。");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in element.EnumerateObject())
        {
            if (!seen.Add(field.Name)) throw new InvalidDataException(context + "包含重复 JSON 字段，未导入。");
            if (!allowed.Contains(field.Name, StringComparer.Ordinal))
                throw new InvalidDataException(context + "含有不支持的字段；仅接受标准语包内容。");
        }
    }

    private static string TextField(JsonElement parent, string name, int maxLength, bool optional = false)
    {
        if (optional && !parent.TryGetProperty(name, out _)) return "";
        if (!parent.TryGetProperty(name, out var field) || field.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"语包字段 {name} 必须是文字。");
        var value = field.GetString()!;
        if (value.Length > maxLength || value.Any(char.IsControl) || value.Any(character => character is '\u2028' or '\u2029'))
            throw new InvalidDataException($"语包字段 {name} 过长或包含控制字符，未导入。");
        value = value.Trim();
        if (!optional && value.Length == 0) throw new InvalidDataException($"语包字段 {name} 不能为空。");
        return value;
    }

    private static void ValidateId(string id, string field)
    {
        if (id == null || id.Length > 64 || !Regex.IsMatch(id, @"^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant) ||
            Regex.IsMatch(id, @"^(con|prn|aux|nul|com[0-9]|lpt[0-9])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            throw new InvalidDataException(field + "必须是 1 至 64 位小写英文字母、数字和单个连字符，不能使用路径或系统保留名称。");
    }

    private static string LocalJsonPath(string file)
    {
        if (string.IsNullOrWhiteSpace(file) ||
            Uri.TryCreate(file, UriKind.Absolute, out var uri) && !uri.IsFile ||
            !string.Equals(Path.GetExtension(file), ".json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("请选择本地 .json 语包文件；不接受外链或压缩包。");
        try { return Path.GetFullPath(file); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        { throw new InvalidDataException("语包文件路径不正确。", ex); }
    }

    private static bool SamePath(string first, string second) =>
        string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);

    private static bool IsInside(string file, string directory) =>
        SamePath(file, directory) || file.StartsWith(Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private sealed class ExportTarget(string destination) : IDisposable
    {
        public string Destination { get; } = destination;
        public List<SafeFileHandle> Pins { get; } = [];
        public void Dispose() { foreach (var pin in Pins) pin.Dispose(); }
    }

    private ExportTarget GuardExportTarget(string destination)
    {
        if (!OperatingSystem.IsWindows())
            throw new InvalidDataException("语包安全导出需要 Windows 本地文件系统。");
        var protectedDirectories = new[] { Path.GetDirectoryName(defaultsFilePath)!,
            Path.GetDirectoryName(Path.GetFullPath(path))! };
        var resolved = ResolveWindowsPath(destination);
        CheckProtectedTarget(resolved, protectedDirectories);
        var target = new ExportTarget(resolved);
        try
        {
            var parent = Path.GetDirectoryName(resolved)!;
            var root = Path.GetPathRoot(parent)!;
            var current = root;
            PinDirectory(current, target.Pins);
            foreach (var component in parent[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, component);
                // Parents are already pinned. Check each child immediately after creation;
                // a concurrently inserted reparse point must never redirect the next step.
                Directory.CreateDirectory(current);
                PinDirectory(current, target.Pins);
            }
            var verified = ResolveWindowsPath(resolved);
            if (!SameResolvedPath(verified, resolved))
                throw new IOException("导出目标在检查期间改变，请重新选择文件。");
            CheckProtectedTarget(verified, protectedDirectories);
            return target;
        }
        catch { target.Dispose(); throw; }
    }

    private static void CheckProtectedTarget(string resolved, string[] protectedDirectories)
    {
        foreach (var directory in protectedDirectories)
            if (IsInside(resolved, ResolveWindowsPath(directory)))
                throw new InvalidDataException("请选择应用目录和个人数据目录以外的导出文件，现有数据未改动。");
    }

    private static bool SameResolvedPath(string first, string second) => string.Equals(
        first.TrimEnd('\\'), second.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    // GetFinalPathNameByHandle normalizes junctions, symlinks and 8.3 names.
    // A volume-GUID path also avoids later drive-letter or DOS-device alias changes.
    // For a new target, resolve its nearest existing ancestor and append only missing components.
    private static string ResolveWindowsPath(string pathToResolve)
    {
        var current = Path.GetFullPath(pathToResolve);
        var missing = new Stack<string>();
        while (true)
        {
            try
            {
                var attributes = File.GetAttributes(current);
                if (missing.Count > 0 && !attributes.HasFlag(FileAttributes.Directory))
                    throw new IOException("导出目标的父路径不是文件夹。");
                break;
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                missing.Push(Path.GetFileName(current));
                current = Path.GetDirectoryName(current)
                    ?? throw new IOException("无法确认导出目标的真实位置。");
            }
        }
        using var handle = OpenPathHandle(current, FileShare.Read | FileShare.Write | FileShare.Delete);
        var resolved = FinalHandlePath(handle);
        while (missing.Count > 0) resolved = Path.Combine(resolved, missing.Pop());
        return resolved;
    }

    private static void PinDirectory(string directory, List<SafeFileHandle> pins)
    {
        // Denying delete on each real directory prevents rename/replacement while
        // retaining the sharing required to create and rename its child file.
        // FILE_LIST_DIRECTORY makes this a real read handle. An attributes-only
        // or zero-access handle does not participate in Windows share/delete checks.
        var handle = OpenPathHandle(directory, FileShare.Read | FileShare.Write, desiredAccess: 1);
        try
        {
            if (!SameResolvedPath(FinalHandlePath(handle), directory))
                throw new IOException("导出目录在检查期间改变，请重新选择文件夹。");
            pins.Add(handle);
        }
        catch { handle.Dispose(); throw; }
    }

    private static SafeFileHandle OpenPathHandle(string pathToOpen, FileShare share, uint desiredAccess = 0)
    {
        var handle = CreateFileW(pathToOpen, desiredAccess, (uint)share, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw new IOException("无法确认或锁定导出目录的真实位置；请选择其他本地文件夹。", new Win32Exception(error));
    }

    private static string FinalHandlePath(SafeFileHandle handle)
    {
        var buffer = new StringBuilder(512);
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 1);
        if (length >= buffer.Capacity)
        {
            buffer = new StringBuilder(checked((int)length + 1));
            length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 1);
        }
        if (length == 0 || length >= buffer.Capacity)
            throw new IOException("无法解析导出目录的真实位置；请选择其他本地文件夹。", new Win32Exception(Marshal.GetLastWin32Error()));
        return buffer.ToString();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path,
        uint pathLength, uint flags);

    private static string ShortText(string value) => new string(value.Where(character => !char.IsControl(character))
        .Take(48).ToArray()) + (value.Length > 48 ? "…" : "");

    private static string SerializePack(PackDocument pack) => JsonSerializer.Serialize(new
    {
        schemaVersion = 1, id = pack.Id, name = pack.Name, version = pack.Version,
        sourceLanguage = pack.SourceLanguage, targetLanguage = pack.TargetLanguage,
        phrases = pack.Entries.Select(entry => new { id = entry.Id, zh = entry.Zh, en = entry.En, keys = entry.Keys })
    }, new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });

    private static void WritePackAtomic(string destination, string contents, string? backup, bool overwriteExisting = true)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(destination))!;
        Directory.CreateDirectory(directory);
        if (backup != null) Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        var temporary = Path.Combine(directory, ".phrase-pack-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = new UTF8Encoding(false).GetBytes(contents);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            if (backup != null) File.Replace(temporary, destination, backup);
            else File.Move(temporary, destination, overwrite: overwriteExisting);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
