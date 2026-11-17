using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using ClaudeZhInput.Commands;
using ClaudeZhInput.Commands.Wpf;
using Forms = System.Windows.Forms;

namespace ClaudeZhInput;

public partial class App : System.Windows.Application
{
    private static string? root;
    public static string Root => root ??= FindRoot();
    public static string DataRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeZhInput");
    public static bool Exiting { get; private set; }
    private Mutex? instance;
    private BridgeServer? bridge;
    private CandidateWindow? candidate;
    private MainWindow? settings;
    private Forms.NotifyIcon? tray;
    private HwndSource? messages;
    private ClaudeCommandLibrary? commands;
    private ImeConnection? active;
    private string activeRequest = "";
    private CommandPickerWindow? commandPicker;
    private StatusBarWindow? statusBar;
    private DispatcherTimer? statusTimer;
    private ImeConnection? toolbarConnection;
    private readonly Dictionary<Guid, ToolbarState> toolbarStates = new();
    private BehaviorLog? behavior;
    private DateTimeOffset lastTranslationNotice;
    private (bool Selected, int Pid, Guid? Connection)? tracedForeground;
    private sealed record ToolbarState(string Mode, bool Ascii, bool Pending, bool Composing, bool Literal, string TranslationNotice);
    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 7 && directory != null; i++, directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "phrases.json")) || File.Exists(Path.Combine(directory.FullName, "phrases.dat"))) return directory.FullName;
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
    }
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Directory.CreateDirectory(DataRoot);
        instance = new Mutex(true, "Local\\ClaudeZhInput.UiService", out var first);
        if (!first)
        {
            if (!e.Args.Contains("--ime-service")) Native.PostMessage(Native.FindWindow(null, "ClaudeZhInput.Service"), Native.ShowMessage, IntPtr.Zero, IntPtr.Zero);
            Shutdown(); return;
        }
        DispatcherUnhandledException += (_, args) =>
        {
            behavior?.Error("ui_dispatcher", args.Exception);
            Notify("操作未完成。请先查看原输入框内容，再重试。" + args.Exception.Message);
            args.Handled = true;
        };
        behavior = new BehaviorLog(DataRoot);
        behavior.Write("service_started", new { mode = Preferences.Load().Mode, inputText = false,
            version = typeof(App).Assembly.GetName().Version?.ToString(),
            launchSource = e.Args.Contains("--auto-start") ? "input-selection" : "direct" });
        settings = new MainWindow();
        MainWindow = settings;
        candidate = new CandidateWindow();
        statusBar = new StatusBarWindow();
        statusBar.ModeSelected += mode => settings.SetMode(mode);
        statusBar.CommandsRequested += OpenToolbarCommands;
        statusBar.SettingsRequested += ShowSettings;
        statusBar.ReviewRequested += () => { ShowSettings(); settings.ShowReviewPage(); };
        statusBar.SkinRequested += () =>
        {
            var previous = Native.GetForegroundWindow();
            statusBar.Hide();
            settings.ShowSkinPicker();
            if (previous != IntPtr.Zero) Native.SetForegroundWindow(previous);
            UpdateToolbar();
        };
        candidate.Selected += async index =>
        {
            if (active is { IsConnected: true } connection)
                await connection.Send(new { op = "select", requestId = activeRequest, index });
        };
        commands = ClaudeCommandLibrary.LoadFromJson(Path.Combine(Root, "commands", "claude-commands.json"),
            new CommandDiscoveryOptions { WorkingDirectory = Root });
        bridge = new(Root, behavior: behavior);
        settings.AttachMaintenance(bridge.Reviews, behavior);
        settings.AttachPhraseStore(bridge.Phrases);
        bridge.State += (connection, data) => Dispatcher.BeginInvoke(() => HandleState(connection, data));
        bridge.MessageSent += (connection, data) =>
        {
            if (BridgeServer.GetString(data, "type") != "engine-state") return;
            var state = new ToolbarState(BridgeServer.GetString(data, "mode"),
                Flag(data, "asciiMode"), Flag(data, "pendingTranslation"),
                BridgeServer.GetString(data, "composition").Length > 0,
                Flag(data, "literalWord"), DescribeTranslation(data));
            Dispatcher.BeginInvoke(() => { toolbarStates[connection.Id] = state; });
        };
        bridge.Disconnected += connection => Dispatcher.BeginInvoke(() =>
        {
            toolbarStates.Remove(connection.Id);
            if (active == connection) { active = null; candidate.Hide(); }
            if (toolbarConnection == connection) { toolbarConnection = null; statusBar.Hide(); }
        });
        bridge.Start();
        settings.PreferencesChanged += async () =>
        {
            behavior?.Write("preferences_changed", new { mode = Preferences.Load().Mode, skin = Preferences.Load().Skin });
            candidate.RefreshSkin(); statusBar.RefreshSkin();
            await bridge.SetMode(Preferences.Load().Mode); UpdateTray(); UpdateToolbar();
        };
        statusTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background,
            (_, _) => UpdateToolbar(), Dispatcher);
        messages = new HwndSource(new HwndSourceParameters("ClaudeZhInput.Service") { Width = 0, Height = 0, WindowStyle = 0 });
        messages.AddHook(OnMessage);
        var hotkey = Native.RegisterHotKey(messages.Handle, Native.HotkeyId, 0x0002 | 0x0004 | 0x4000, 0x4D);
        tray = new Forms.NotifyIcon { Text = "Claude 中文输入法", Icon = System.Drawing.SystemIcons.Application, Visible = true };
        tray.DoubleClick += (_, _) => ShowSettings();
        UpdateTray();
        if (!e.Args.Contains("--ime-service")) ShowSettings();
        if (!hotkey) settings.SetStatus("Ctrl+Shift+M 已被其他程序占用；可从托盘切换模式。");
        if (e.Args.Length == 2 && e.Args[0] == "--snapshot")
            Dispatcher.BeginInvoke(async () => { ShowSettings(); await Task.Delay(400); settings.SaveSnapshot(e.Args[1]); ExitService(); });
        if (e.Args.Length == 2 && e.Args[0] == "--snapshot-skins")
            Dispatcher.BeginInvoke(async () =>
            {
                settings.Hide();Directory.CreateDirectory(e.Args[1]);
                foreach(var skin in SkinCatalog.All)
                {
                    candidate.RenderState("先检查登录失败的原因，", "buyaoxiugai", ["不要修改", "不要修改代码", "不要修改配置", "不要修该"], 0, "claude", 240, 180, previewSkin:skin);
                    await Task.Delay(120);candidate.SaveSnapshot(Path.Combine(e.Args[1],skin.Id+"-candidate.png"));
                }
                ExitService();
            });
    }
    private IntPtr OnMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0312 && wParam.ToInt32() == Native.HotkeyId) { settings?.ToggleMode(); handled = true; }
        else if (msg == Native.ShowMessage) { ShowSettings(); handled = true; }
        return IntPtr.Zero;
    }
    private void ShowSettings() { settings?.Show(); if (settings != null) { settings.WindowState = WindowState.Normal; settings.Activate(); } }
    private static bool Flag(JsonElement value, string name) => value.TryGetProperty(name, out var flag) && flag.ValueKind == JsonValueKind.True;
    public static string DescribeTranslation(JsonElement data)
    {
        var status = BridgeServer.GetString(data, "translationStatus");
        if (status == "fallback") return "转换未完成 · 保留中文";
        return "";
    }
    private void UpdateToolbar()
    {
        if (statusBar == null || bridge == null || commandPicker != null) { statusBar?.Hide(); return; }
        settings?.SetInputComponents(bridge.Connections.Where(c=>c.IsConnected && c.Engine=="rime").Select(c=>c.ClientVersion));
        if (!Native.TryGetActiveInput(out var wake, out var pid))
        {
            TraceForeground(false, 0, null);
            statusBar.Hide(); toolbarConnection = null; candidate?.Hide(); return;
        }
        var connections = bridge.Connections.Where(c => c.IsConnected && c.ProcessId == pid && c.Engine == "rime").ToArray();
        toolbarConnection = connections.FirstOrDefault(c => c.NativeWindow == wake)
            ?? (connections.Length == 1 && connections[0].NativeWindow == IntPtr.Zero ? connections[0] : null);
        var connection = toolbarConnection;
        TraceForeground(true, pid, connection?.Id);
        toolbarStates.TryGetValue(connection?.Id ?? Guid.Empty, out var state);
        var mode = state?.Mode ?? Preferences.Load().Mode;
        var status = connection == null ? "正在连接输入服务" : connection.RimeError.Length > 0 ? "输入引擎未就绪"
            : state?.TranslationNotice.Length > 0 ? state.TranslationNotice
            : state?.Pending == true ? "正在翻译 · Esc 保留"
            : state?.Ascii == true ? "字母直输 · Shift 切回"
            : state?.Literal == true ? "英文原样 · 空格确认"
            : mode == "claude" ? "等待句末" : "拼音输入";
        var canOpen = connection?.SupportsToolbarCommands == true && state?.Composing != true && state?.Pending != true;
        var commandHint = connection == null ? "等待输入服务连接"
            : !connection.SupportsToolbarCommands ? "此窗口仍使用旧输入组件；重新打开应用后可点击。当前可按 Ctrl+; 打开指令库。"
            : !canOpen ? "先完成或取消当前输入，再打开指令库" : "选择指令并填入当前光标位置 · Ctrl+;";
        statusBar.Render(mode, status, canOpen, commandHint);
    }
    private async void OpenToolbarCommands()
    {
        behavior?.Write("toolbar_commands_clicked", new { });
        UpdateToolbar();
        if (toolbarConnection is { IsConnected: true, SupportsToolbarCommands: true } connection)
            await connection.Send(new { op = "show-commands" });
    }
    private void TraceForeground(bool selected, int pid, Guid? connection)
    {
        var state = (selected, pid, connection);
        if (tracedForeground == state) return;
        tracedForeground = state;
        behavior?.Write("foreground_ime", new { selected, hostPid = pid, connection });
    }
    private void UpdateTray()
    {
        if (tray == null) return;
        var menu = new Forms.ContextMenuStrip();
        var mode = Preferences.Load().Mode;
        menu.Items.Add(mode == "claude" ? "当前：整句英文" : "当前：普通中文", null, (_, _) => settings?.ToggleMode());
        menu.Items.Add("设置与皮肤", null, (_, _) => ShowSettings());
        menu.Items.Add("最近翻译", null, (_, _) => { ShowSettings(); settings?.ShowReviewPage(); });
        var recording = menu.Items.Add("本地后台记录（不含输入原文）", null, (_, _) =>
        {
            if (behavior != null) behavior.SetEnabled(!behavior.Enabled);
            UpdateTray();
        });
        if (recording is Forms.ToolStripMenuItem item) item.Checked = behavior?.Enabled == true;
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("关闭助手（切回输入法可唤醒）", null, (_, _) => ExitService());
        var previous = tray.ContextMenuStrip; tray.ContextMenuStrip = menu; previous?.Dispose();
        tray.Text = mode == "claude" ? "Claude 中文输入法 · 整句英文" : "Claude 中文输入法 · 普通中文";
    }
    private void HandleState(ImeConnection connection, JsonElement data)
    {
        if (candidate == null) return;
        var type = BridgeServer.GetString(data, "type");
        var request = BridgeServer.GetString(data, "requestId");
        if (type is "composition" or "translate")
        {
            if (request != connection.LatestRequest) return;
            active = connection; activeRequest = request;
            var list = data.TryGetProperty("candidates", out var values) && values.ValueKind == JsonValueKind.Array
                ? values.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "").Take(9).ToArray() : [];
            var sentence = BridgeServer.GetString(data, "sentence");
            var pinyin = BridgeServer.GetString(data, "pinyin");
            if (type == "translate" && sentence.Length == 0 && pinyin.Length == 0)
                sentence = BridgeServer.GetString(data, "text");
            if (sentence.Length == 0 && pinyin.Length == 0 && list.Length == 0) { candidate.Hide(); return; }
            double? Number(string name) => data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) ? number : null;
            int Integer(string name,int fallback) => data.TryGetProperty(name,out var value)&&value.ValueKind==JsonValueKind.Number&&value.TryGetInt32(out var number)?number:fallback;
            var mode=BridgeServer.GetString(data,"mode");
            var ascii=data.TryGetProperty("asciiMode",out var asciiValue)&&asciiValue.ValueKind==JsonValueKind.True;
            var literal=Flag(data,"literalWord");
            var pending=Flag(data,"pendingTranslation")||type=="translate";
            var status=pending?(pinyin.Length>0?"前句正在翻译 · 当前可继续选词":"正在翻译 · 可继续输入 · Esc 保留"):literal?"英文原样 · 空格确认后继续拼音":ascii?"英文 · Shift 切回中文":mode=="claude"?"Claude · 句末转英文 · ;; 指令":"中文 · 空格选词 · 数字选候选";
            var pages=Integer("pageCount",1);
            if(type!="translate"&&pages>1)status+=$" · {Integer("page",0)+1}/{pages} 页 · PgUp/PgDn";
            candidate.RenderState(sentence, pinyin, list, Integer("selectedIndex",0), mode, Number("screenX"), Number("screenY"),status);
        }
        else if (type == "commands")
        {
            active = connection; activeRequest = request;
            candidate.Hide();
            OpenCommands(connection, request, BridgeServer.GetString(data, "text"));
        }
        else if (type == "toolbar-command-rejected")
            Notify("请先点击可输入文字的位置，完成或取消当前输入后再打开指令库。");
        else if (type is "commit" or "cancel" or "deactivate" or "blur")
        {
            if (active == connection) candidate.Hide();
            if (data.TryGetProperty("warnings", out var warnings) && warnings.ValueKind == JsonValueKind.Array)
            {
                var warning = string.Join("；", warnings.EnumerateArray().Select(v => v.GetString()).Where(v => !string.IsNullOrEmpty(v)));
                if (warning.Length > 0)
                {
                    settings?.SetStatus(warning);
                    if (DateTimeOffset.UtcNow - lastTranslationNotice > TimeSpan.FromMinutes(1))
                    { lastTranslationNotice = DateTimeOffset.UtcNow; Notify(warning); }
                }
            }
        }
    }
    public static void ShowCommands(Window owner)
    {
        if (Current is App app) app.OpenCommands(null, "", "", owner);
    }
    private async void OpenCommands(ImeConnection? connection, string request, string query, Window? owner = null)
    {
        if (commands == null || commandPicker != null) return;
        var previous = Native.GetForegroundWindow();
        var skin = SkinCatalog.Find(Preferences.Load().Skin);
        var theme = new ResourceDictionary
        {
            [CommandPickerResourceKeys.BackgroundBrush] = SkinCatalog.Brush(skin.Background),
            [CommandPickerResourceKeys.SurfaceBrush] = SkinCatalog.Brush(skin.Surface),
            [CommandPickerResourceKeys.InputBrush] = SkinCatalog.Brush(skin.Surface),
            [CommandPickerResourceKeys.ForegroundBrush] = SkinCatalog.Brush(skin.Ink),
            [CommandPickerResourceKeys.SecondaryForegroundBrush] = SkinCatalog.Brush(skin.Muted),
            [CommandPickerResourceKeys.MutedForegroundBrush] = SkinCatalog.Brush(skin.Muted),
            [CommandPickerResourceKeys.AccentBrush] = SkinCatalog.Brush(skin.Accent),
            [CommandPickerResourceKeys.SelectionBrush] = SkinCatalog.Brush(skin.Background),
            [CommandPickerResourceKeys.HoverBrush] = SkinCatalog.Brush(skin.Background),
            [CommandPickerResourceKeys.BorderBrush] = SkinCatalog.Brush(skin.Stroke),
            [CommandPickerResourceKeys.CornerRadius] = new CornerRadius(skin.Corner)
        };
        commandPicker = new CommandPickerWindow(commands, query, theme) { Owner = owner, FontFamily = SkinCatalog.UiFont };
        statusBar?.Hide();
        commandPicker.ShowDialog();
        var picked = commandPicker.PickedCommand;
        behavior?.Write("command_picker_closed", new { cancelled = picked == null, characters = picked?.Length ?? 0, connected = connection != null });
        commandPicker = null;
        if (connection != null)
        {
            if (previous != IntPtr.Zero) Native.SetForegroundWindow(previous);
            await connection.Send(new { op = "command", requestId = request, text = picked ?? "" });
        }
        else if (picked != null) settings?.SetStatus("已选 " + picked + "。在输入位置按 Ctrl+; 可直接填入。");
    }
    private void Notify(string message)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => Notify(message)); return; }
        settings?.SetStatus(message);
        tray?.ShowBalloonTip(5000, "Claude 中文输入法", message, Forms.ToolTipIcon.Info);
    }
    private void ExitService()
    {
        behavior?.Write("service_exit_requested", new { reason = "application-exit" });
        Exiting = true;
        Shutdown();
    }
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        behavior?.Write("service_exit_requested", new { reason = "session-ending" });
        base.OnSessionEnding(e);
    }
    protected override void OnExit(ExitEventArgs e)
    {
        Exiting = true;
        statusTimer?.Stop();
        if (messages != null) { Native.UnregisterHotKey(messages.Handle, Native.HotkeyId); messages.Dispose(); }
        bridge?.Dispose(); tray?.Dispose();
        behavior?.Dispose();
        instance?.Dispose();
        base.OnExit(e);
    }
}
