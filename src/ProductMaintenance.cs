using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace ClaudeZhInput;

public static class ProductMaintenance
{
    public const string Name = "Claude 中文输入法";
    public const string InputComponentVersion = "1.4.0";
    public static string Version => typeof(App).Assembly.GetName().Version?.ToString(3) ?? "未知";
    private static readonly string[] BackupFiles = ["preferences.json", "phrases.json", "toolbar-position.json", "translation-options.json", "behavior-recording.json"];
    public static void ExportSettings(string dataRoot, string destination)
    {
        var files = new Dictionary<string, JsonElement>();
        foreach (var name in BackupFiles)
        {
            var path = Path.Combine(dataRoot, name);
            if (!File.Exists(path)) continue;
            try { using var document = JsonDocument.Parse(File.ReadAllText(path)); files.Add(name, document.RootElement.Clone()); }
            catch(JsonException) { throw new InvalidDataException($"{name} 格式损坏，原文件未动。请从数据目录保留原文件，或恢复已有的有效备份。"); }
        }
        WriteAtomic(destination, JsonSerializer.Serialize(new {format="ClaudeZhInput.Settings",version=1,files}, new JsonSerializerOptions {WriteIndented=true}));
    }
    public static string ImportSettings(string source, string dataRoot)
    {
        if (new FileInfo(source).Length > 16*1024*1024) throw new InvalidDataException("备份超过 16 MiB，未导入。");
        using var document = JsonDocument.Parse(File.ReadAllText(source));
        var root = document.RootElement;
        if (root.GetProperty("format").GetString() != "ClaudeZhInput.Settings" || root.GetProperty("version").GetInt32() != 1)
            throw new InvalidDataException("不是此输入法的设置备份。");
        var files = root.GetProperty("files").EnumerateObject().ToArray();
        foreach (var file in files)
        {
            if (!BackupFiles.Contains(file.Name, StringComparer.Ordinal)) throw new InvalidDataException("备份包含不支持的文件，未导入。");
            if (file.Value.ValueKind != (file.Name=="phrases.json" ? JsonValueKind.Array : JsonValueKind.Object))
                throw new InvalidDataException("备份字段格式不正确，未导入。");
            if (file.Name=="phrases.json")
            {
                var phrases=JsonSerializer.Deserialize<Phrase[]>(file.Value.GetRawText(), PhraseStore.JsonOptions);
                if(phrases==null||phrases.Length>200||phrases.Any(p=>p==null||string.IsNullOrWhiteSpace(p.Zh)||string.IsNullOrWhiteSpace(p.En)||p.Zh.Length>16000||p.En.Length>16000))
                    throw new InvalidDataException("个人短语格式不正确，未导入。");
            }
            if (file.Name=="preferences.json")
            {
                var preferences=JsonSerializer.Deserialize<Preferences>(file.Value.GetRawText());
                if(preferences==null||preferences.Mode is not ("normal" or "claude")||!SkinCatalog.All.Any(s=>s.Id==preferences.Skin))
                    throw new InvalidDataException("模式或皮肤配置不正确，未导入。");
            }
        }
        Directory.CreateDirectory(dataRoot);
        var previous = BackupFiles.ToDictionary(n=>n, n=>File.Exists(Path.Combine(dataRoot,n)) ? File.ReadAllBytes(Path.Combine(dataRoot,n)) : null);
        // A visible, recoverable backup is kept even if an I/O error prevents
        // restoring every file. Never destroy the sole previous settings.
        var backupDirectory=Path.Combine(dataRoot,"导入前备份-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(backupDirectory);
        foreach(var file in previous) if(file.Value is byte[] bytes) File.WriteAllBytes(Path.Combine(backupDirectory,file.Key),bytes);
        try { foreach (var file in files) WriteAtomic(Path.Combine(dataRoot,file.Name),file.Value.GetRawText()); }
        catch
        {
            foreach(var file in files)
                try { if (previous[file.Name] is byte[] bytes) File.WriteAllBytes(Path.Combine(dataRoot,file.Name),bytes); else File.Delete(Path.Combine(dataRoot,file.Name)); } catch { }
            throw new IOException("导入未完成；原文件逐字节保留在数据目录的“"+Path.GetFileName(backupDirectory)+"”文件夹。");
        }
        return backupDirectory;
    }
    public static string Feedback(bool recording) =>
        $"{Name} 问题反馈\r\n版本：{Version}（小范围试用版）\r\nWindows：{Environment.OSVersion.Version}\r\n系统架构：{RuntimeInformation.OSArchitecture}\r\n进程架构：{RuntimeInformation.ProcessArchitecture}\r\n本地元数据记录：{(recording?"开启":"关闭")}\r\n\r\n请填写：\r\n应用名称和版本：\r\n操作步骤：\r\n期望结果：\r\n实际结果：\r\n发生时间：\r\n切换输入法后是否恢复：\r\n\r\n此文件没有附加输入内容、个人短语、文件路径、窗口标题或日志。请检查你补充的内容，再自行发给提供安装包的人；软件不会自动上传。\r\n";
    public static void WriteAtomic(string path, string text)
    {
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try { File.WriteAllText(temporary,text,new System.Text.UTF8Encoding(false)); File.Move(temporary,path,true); }
        finally { if(File.Exists(temporary))File.Delete(temporary); }
    }
}
