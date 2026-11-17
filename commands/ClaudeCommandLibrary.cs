using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ClaudeZhInput.Commands;

/// <summary>命令用途分类，值与 catalog JSON 中的 category 对应。</summary>
public enum ClaudeCommandCategory
{
    Daily,
    Context,
    Model,
    Permissions,
    Planning,
    Session,
    Review,
    Diagnostics,
    Extension,
    Automation
}

/// <summary>选择候选后可能影响的范围。这个库只索引和填入命令，不执行命令。</summary>
public enum ClaudeCommandImpact
{
    ReadOnly,
    Session,
    LocalWrite,
    ExternalOrAccount,
    Unknown
}

/// <summary>单个来源链接。</summary>
public sealed record ClaudeCommandSource
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Url { get; init; } = "";
    public string AccessedOn { get; init; } = "";
}

/// <summary>命令在不同版本、平台、套餐或功能开关下的可用性说明。</summary>
public sealed record ClaudeCommandConditions
{
    public string? MinimumVersion { get; init; }
    public string Platform { get; init; } = "Claude Code 会话";
    public string? Plans { get; init; }
    public string? Requires { get; init; }
    public string Notes { get; init; } = "";
}

/// <summary>
/// 可显示给输入法的命令条目。Command 始终保存带斜杠的规范名称，FillText 只返回填入文本。
/// </summary>
public sealed record ClaudeCommandEntry
{
    public string Id { get; init; } = "";
    public string ChineseName { get; init; } = "";
    public string Command { get; init; } = "";
    public IReadOnlyList<string> Aliases { get; init; } = Array.Empty<string>();
    public string Usage { get; init; } = "";
    public string Parameters { get; init; } = "";
    public ClaudeCommandCategory Category { get; init; }
    public IReadOnlyList<string> SearchTerms { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> SourceLinks { get; init; } = Array.Empty<string>();
    public string ChangeEffect { get; init; } = "";
    public ClaudeCommandImpact Impact { get; init; } = ClaudeCommandImpact.Unknown;
    public ClaudeCommandConditions Conditions { get; init; } = new();
    public bool IsBundledSkill { get; init; }
    public bool IsHidden { get; init; }
    public bool RequiresInteractive { get; init; }
    public bool IsUserDefined { get; init; }
    public string? SourcePath { get; init; }
    public string? SourceKind { get; init; }

    /// <summary>供输入法候选选择后填入的文本。调用方仍需让用户自行确认/提交。</summary>
    [JsonIgnore]
    public string FillText => NormalizeCommand(Command);

    /// <summary>固定为 false；此库没有执行器。</summary>
    [JsonIgnore]
    public bool AutoExecute => false;

    internal IEnumerable<string> SearchCorpus()
    {
        yield return ChineseName;
        yield return Command;
        yield return Usage;
        yield return Parameters;
        yield return ChangeEffect;
        yield return Conditions.Notes;
        foreach (var alias in Aliases) yield return alias;
        foreach (var term in SearchTerms) yield return term;
    }

    internal static string NormalizeCommand(string command)
    {
        var value = (command ?? "").Trim();
        return value.StartsWith("/", StringComparison.Ordinal) ? value : "/" + value;
    }
}

/// <summary>目录发现时采用的范围。默认包括当前目录祖先和用户目录，不扫描文件内容正文。</summary>
public sealed record CommandDiscoveryOptions
{
    public string? WorkingDirectory { get; init; }
    public bool IncludeAncestors { get; init; } = true;
    public bool IncludePersonal { get; init; } = true;
    public IReadOnlyList<string> AdditionalRoots { get; init; } = Array.Empty<string>();
    public int MaxFrontMatterLines { get; init; } = 64;
    public int MaxFrontMatterBytes { get; init; } = 16 * 1024;
}

/// <summary>发现结果；Warnings 只报告无法读取的目录或元数据，不会阻止内置命令使用。</summary>
public sealed record CommandDiscoveryResult
{
    public IReadOnlyList<ClaudeCommandEntry> Entries { get; init; } = Array.Empty<ClaudeCommandEntry>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>
/// 输入法打开常用 CC 命令菜单的触发设置。默认 ;; 仅在当前组合文本的开头触发，Ctrl+; 可直接打开。
/// </summary>
public sealed record ClaudeCommandTrigger
{
    public string Prefix { get; init; } = ";;";
    public string KeyGesture { get; init; } = "Ctrl+;";
    public bool RequiresCompositionStart { get; init; } = true;
    public bool ConsumePrefixWhenOpened { get; init; } = true;
    public bool CandidateOnly { get; init; } = true;
    public string Notes { get; init; } = "仅在输入法菜单触发时消费前缀；普通文字中的分号不受影响。";

    /// <summary>
    /// 解析宿主输入法传来的组合文本。返回的 query 不含触发前缀；不会提交或执行任何命令。
    /// </summary>
    public bool TryGetQuery(string? composition, bool ctrlSemicolon, out string query)
    {
        query = "";
        if (ctrlSemicolon)
            return true;

        var value = composition ?? "";
        if (RequiresCompositionStart && !value.StartsWith(Prefix, StringComparison.Ordinal))
            return false;
        if (!value.StartsWith(Prefix, StringComparison.Ordinal))
            return false;

        query = value[Prefix.Length..];
        return true;
    }
}

/// <summary>JSON 顶层结构。</summary>
public sealed record ClaudeCommandCatalog
{
    public int SchemaVersion { get; init; } = 1;
    public string CatalogVersion { get; init; } = "";
    public string Description { get; init; } = "";
    public ClaudeCommandVerification Verification { get; init; } = new();
    public ClaudeCommandTrigger Trigger { get; init; } = new();
    public IReadOnlyList<ClaudeCommandSource> Sources { get; init; } = Array.Empty<ClaudeCommandSource>();
    public IReadOnlyList<ClaudeCommandEntry> Commands { get; init; } = Array.Empty<ClaudeCommandEntry>();
}

/// <summary>记录官方文档、本机 --help 和本机版本核对范围。</summary>
public sealed record ClaudeCommandVerification
{
    public string OfficialDocsCheckedOn { get; init; } = "";
    public string LocalExecutable { get; init; } = "";
    public string LocalVersion { get; init; } = "";
    public string LocalHelpCommand { get; init; } = "claude --help";
    public string LocalHelpScope { get; init; } = "";
}

/// <summary>命中候选；输入法只应使用 FillText 插入，不应把它当作执行请求。</summary>
public sealed record ClaudeCommandSuggestion
{
    public ClaudeCommandEntry Command { get; init; } = new();
    public int Score { get; init; }
    public string Match { get; init; } = "";
    public string FillText => Command.FillText;
    public bool AutoExecute => false;
}

/// <summary>
/// 读取内置目录、按中文/拼音/缩写/英文过滤，并只索引用户自定义命令与 skill 的元数据。
/// 不包含 claude.exe 启动器，也不执行任何命令、脚本或 Markdown 正文。
/// </summary>
public sealed class ClaudeCommandLibrary
{
    private readonly IReadOnlyList<ClaudeCommandEntry> entries;
    private readonly Dictionary<ClaudeCommandEntry, string[]> corpus;

    private ClaudeCommandLibrary(ClaudeCommandCatalog catalog, IEnumerable<ClaudeCommandEntry> discovered,
        IEnumerable<string> warnings)
    {
        Catalog = catalog;
        Trigger = catalog.Trigger ?? new();
        Warnings = warnings.ToArray();

        var all = new List<ClaudeCommandEntry>();
        all.AddRange(catalog.Commands ?? Array.Empty<ClaudeCommandEntry>());
        all.AddRange(discovered);
        entries = new ReadOnlyCollection<ClaudeCommandEntry>(Deduplicate(all).ToList());
        corpus = entries.ToDictionary(e => e, e => e.SearchCorpus()
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(Normalize)
            .Distinct(StringComparer.Ordinal)
            .ToArray());
    }

    public ClaudeCommandCatalog Catalog { get; }
    public ClaudeCommandTrigger Trigger { get; }
    public IReadOnlyList<ClaudeCommandEntry> All => entries;
    public IReadOnlyList<string> Warnings { get; }

    public static JsonSerializerOptions JsonOptions { get; } = CreateJsonOptions();

    /// <summary>从命令库 JSON 加载；如果 options 不为 null，同时发现用户命令与 skills。</summary>
    public static ClaudeCommandLibrary LoadFromJson(string jsonPath, CommandDiscoveryOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(jsonPath)) throw new ArgumentException("必须提供 JSON 路径。", nameof(jsonPath));
        var json = File.ReadAllText(jsonPath, Encoding.UTF8);
        var catalog = JsonSerializer.Deserialize<ClaudeCommandCatalog>(json, JsonOptions)
            ?? throw new InvalidDataException("命令库 JSON 为空或格式无效。\n" + jsonPath);
        var discovery = options == null ? new CommandDiscoveryResult() : DiscoverUserCommands(options);
        return new ClaudeCommandLibrary(catalog, discovery.Entries, discovery.Warnings);
    }

    /// <summary>从已反序列化的目录创建库，便于 WPF 宿主缓存或测试。</summary>
    public static ClaudeCommandLibrary FromCatalog(ClaudeCommandCatalog catalog,
        CommandDiscoveryOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var discovery = options == null ? new CommandDiscoveryResult() : DiscoverUserCommands(options);
        return new ClaudeCommandLibrary(catalog, discovery.Entries, discovery.Warnings);
    }

    /// <summary>按中文、拼音、缩写、英文命令及用途过滤，返回已排序的条目列表。</summary>
    public IReadOnlyList<ClaudeCommandEntry> Search(string? query, int limit = 12,
        ClaudeCommandCategory? category = null)
    {
        if (limit <= 0) return Array.Empty<ClaudeCommandEntry>();
        return Suggest(query, limit, category).Select(hit => hit.Command).ToArray();
    }

    /// <summary>返回带匹配分数的候选；每个候选都标明只填入而不自动执行。</summary>
    public IReadOnlyList<ClaudeCommandSuggestion> Suggest(string? query, int limit = 12,
        ClaudeCommandCategory? category = null)
    {
        if (limit <= 0) return Array.Empty<ClaudeCommandSuggestion>();
        var normalizedQuery = NormalizeQuery(query);
        var candidates = entries
            .Where(entry => category == null || entry.Category == category.Value)
            .Select(entry => Score(entry, normalizedQuery))
            .Where(hit => normalizedQuery.Length == 0 || hit.Score > 0)
            .OrderByDescending(hit => hit.Score)
            .ThenBy(hit => hit.Command.Command.Command, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToArray();
        return candidates.Select(hit => hit.Command).ToArray();
    }

    /// <summary>返回规范填入文本，例如 /model；不会附加参数，也不会触发 Claude Code。</summary>
    public static string FillOnly(ClaudeCommandEntry command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return command.FillText;
    }

    /// <summary>
    /// 从当前目录祖先、用户目录或额外目录发现 .claude/commands/*.md 与 .claude/skills/**/SKILL.md。
    /// 只读取 YAML frontmatter 的 name/description，不读取或执行正文。
    /// </summary>
    public static CommandDiscoveryResult DiscoverUserCommands(CommandDiscoveryOptions? options = null)
    {
        options ??= new CommandDiscoveryOptions();
        var warnings = new List<string>();
        var roots = ResolveRoots(options, warnings);
        var found = new List<ClaudeCommandEntry>();

        foreach (var root in roots)
        {
            DiscoverFolder(root, "commands", false, options, found, warnings);
            DiscoverFolder(root, "skills", true, options, found, warnings);
        }

        return new CommandDiscoveryResult
        {
            Entries = Deduplicate(found).ToArray(),
            Warnings = warnings.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
        };
    }

    private static IEnumerable<ClaudeCommandEntry> Deduplicate(IEnumerable<ClaudeCommandEntry> values)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in values)
        {
            var command = ClaudeCommandEntry.NormalizeCommand(entry.Command);
            var key = entry.IsUserDefined
                ? command + "|" + (entry.SourcePath ?? "")
                : command;
            if (seen.Add(key)) yield return entry with { Command = command };
        }
    }

    private (ClaudeCommandSuggestion Command, int Score, string Match) Score(ClaudeCommandEntry entry,
        string query)
    {
        if (query.Length == 0)
            return (new ClaudeCommandSuggestion { Command = entry, Score = 0, Match = "默认" }, 0, "默认");

        var normalizedCommand = Normalize(entry.Command);
        var score = 0;
        var match = "用途";

        if (normalizedCommand == query)
        {
            score = 1200;
            match = "命令精确";
        }
        else if (normalizedCommand.StartsWith(query, StringComparison.Ordinal))
        {
            score = 1000;
            match = "命令前缀";
        }
        else if (entry.Aliases.Any(alias => Normalize(alias) == query))
        {
            score = 1100;
            match = "别名精确";
        }
        else if (entry.Aliases.Any(alias => Normalize(alias).StartsWith(query, StringComparison.Ordinal)))
        {
            score = 900;
            match = "别名前缀";
        }
        else if (Normalize(entry.ChineseName).StartsWith(query, StringComparison.Ordinal))
        {
            score = 850;
            match = "中文前缀";
        }
        else if (Normalize(entry.ChineseName).Contains(query, StringComparison.Ordinal))
        {
            score = 700;
            match = "中文包含";
        }
        else if (entry.SearchTerms.Any(term => Normalize(term) == query))
        {
            score = 820;
            match = "拼音/缩写精确";
        }
        else if (entry.SearchTerms.Any(term => Normalize(term).StartsWith(query, StringComparison.Ordinal)))
        {
            score = 740;
            match = "拼音/缩写前缀";
        }
        else
        {
            var index = Array.FindIndex(corpus[entry], value => value.Contains(query, StringComparison.Ordinal));
            if (index >= 0)
            {
                score = index == 0 ? 520 : 400;
                match = index == 0 ? "用途包含" : "关键词包含";
            }
        }

        return (new ClaudeCommandSuggestion { Command = entry, Score = score, Match = match }, score, match);
    }

    private static string NormalizeQuery(string? value)
    {
        var query = (value ?? "").Trim();
        while (query.StartsWith("/", StringComparison.Ordinal)) query = query[1..];
        return Normalize(query);
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var normalized = value.Trim().ToLower(CultureInfo.InvariantCulture).Normalize(NormalizationForm.FormKC);
        var builder = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            if (char.IsLetterOrDigit(ch) || ch >= '\u3400' && ch <= '\u9fff') builder.Append(ch);
        }
        return builder.ToString();
    }

    private static List<string> ResolveRoots(CommandDiscoveryOptions options, List<string> warnings)
    {
        var roots = new List<string>();
        var working = options.WorkingDirectory;
        if (string.IsNullOrWhiteSpace(working)) working = Environment.CurrentDirectory;
        try { working = Path.GetFullPath(working); }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            warnings.Add($"无法解析工作目录：{working}");
            working = Environment.CurrentDirectory;
        }

        if (options.IncludeAncestors)
        {
            var current = new DirectoryInfo(working);
            while (current != null)
            {
                roots.Add(current.FullName);
                current = current.Parent;
            }
        }
        else
        {
            roots.Add(working);
        }

        if (options.IncludePersonal)
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(profile)) roots.Add(profile);
        }

        roots.AddRange(options.AdditionalRoots ?? Array.Empty<string>());
        return roots.Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path =>
            {
                try { return Path.GetFullPath(path); }
                catch { return ""; }
            })
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void DiscoverFolder(string root, string folderName, bool skillFolder,
        CommandDiscoveryOptions options, List<ClaudeCommandEntry> target, List<string> warnings)
    {
        var folder = Path.Combine(root, ".claude", folderName);
        if (!Directory.Exists(folder)) return;

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(folder, "*.md", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                ReturnSpecialDirectories = false
            }).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"无法扫描 {folder}：{ex.Message}");
            return;
        }

        foreach (var file in files)
        {
            if (skillFolder && !string.Equals(Path.GetFileName(file), "SKILL.md", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!skillFolder && string.Equals(Path.GetFileName(file), "SKILL.md", StringComparison.OrdinalIgnoreCase))
                continue;

            var metadata = ReadFrontMatter(file, options, warnings);
            var commandName = GetCommandName(folder, file, skillFolder, metadata.Name);
            if (string.IsNullOrWhiteSpace(commandName)) continue;

            var command = ClaudeCommandEntry.NormalizeCommand(commandName);
            target.Add(new ClaudeCommandEntry
            {
                Id = (skillFolder ? "user-skill:" : "user-command:") + command.TrimStart('/'),
                ChineseName = "用户指令：" + command.TrimStart('/'),
                Command = command,
                Aliases = Array.Empty<string>(),
                Usage = command + (string.IsNullOrWhiteSpace(metadata.Description) ? "" : " [参数]"),
                Parameters = string.IsNullOrWhiteSpace(metadata.Description) ? "" : metadata.Description,
                Category = ClaudeCommandCategory.Extension,
                SearchTerms = new[] { command.TrimStart('/'), metadata.Name ?? "", metadata.Description ?? "" },
                SourceLinks = new[] { "https://code.claude.com/docs/zh-CN/skills" },
                ChangeEffect = "只索引名称和 frontmatter description；选择后仅填入命令文本，实际行为由用户自己的文件决定。",
                Impact = ClaudeCommandImpact.Unknown,
                Conditions = new ClaudeCommandConditions
                {
                    Platform = "按当前 Claude Code 的 personal/project/nested skill 或 commands 发现规则",
                    Requires = "只读取 YAML frontmatter；未读取正文，也未执行文件。",
                    Notes = skillFolder
                        ? "项目或个人 .claude/skills/**/SKILL.md"
                        : "项目或个人 .claude/commands/**/*.md"
                },
                IsBundledSkill = false,
                IsHidden = false,
                RequiresInteractive = false,
                IsUserDefined = true,
                SourcePath = file,
                SourceKind = skillFolder ? "userSkill" : "userCommand"
            });
        }
    }

    private static string GetCommandName(string folder, string file, bool skillFolder, string? frontMatterName)
    {
        if (skillFolder)
        {
            if (!string.IsNullOrWhiteSpace(frontMatterName))
                return frontMatterName.Trim();
            var directory = Path.GetDirectoryName(file);
            if (directory == null) return "";
            var relative = Path.GetRelativePath(folder, directory);
            return relative == "." ? "" : relative.Replace(Path.DirectorySeparatorChar, ':')
                .Replace(Path.AltDirectorySeparatorChar, ':');
        }

        var relativeFile = Path.GetRelativePath(folder, file);
        var withoutExtension = Path.ChangeExtension(relativeFile, null) ?? relativeFile;
        return withoutExtension.Replace(Path.DirectorySeparatorChar, ':')
            .Replace(Path.AltDirectorySeparatorChar, ':');
    }

    private static FrontMatter ReadFrontMatter(string file, CommandDiscoveryOptions options,
        List<string> warnings)
    {
        string? name = null;
        string? description = null;
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                bufferSize: 4096, options: FileOptions.SequentialScan);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var consumedBytes = 0;
            var lineCount = 0;
            var first = reader.ReadLine();
            if (first == null || first.Trim() != "---") return new FrontMatter();
            consumedBytes += Encoding.UTF8.GetByteCount(first) + 2;

            while (lineCount++ < options.MaxFrontMatterLines && consumedBytes <= options.MaxFrontMatterBytes)
            {
                var line = reader.ReadLine();
                if (line == null || line.Trim() == "---") break;
                consumedBytes += Encoding.UTF8.GetByteCount(line) + 2;
                var match = Regex.Match(line, "^\\s*(name|description)\\s*:\\s*(.*?)\\s*$",
                    RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
                if (!match.Success) continue;
                var key = match.Groups[1].Value.ToLowerInvariant();
                var value = UnquoteYamlScalar(match.Groups[2].Value);
                if (key == "name") name ??= value;
                if (key == "description") description ??= value;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            warnings.Add($"无法读取命令元数据 {file}：{ex.Message}");
        }
        return new FrontMatter(name, description);
    }

    private static string UnquoteYamlScalar(string value)
    {
        var text = value.Trim();
        if (text.Length >= 2 && ((text[0] == '"' && text[^1] == '"') || (text[0] == '\'' && text[^1] == '\'')))
            return text[1..^1].Trim();
        return text;
    }

    private readonly record struct FrontMatter(string? Name = null, string? Description = null);

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            WriteIndented = true
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
