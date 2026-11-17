using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using Point = System.Windows.Point;
using Brushes = System.Windows.Media.Brushes;

namespace ClaudeZhInput;

public partial class StatusBarWindow : Window
{
    public event Action<string>? ModeSelected;
    public event Action? CommandsRequested;
    public event Action? SkinRequested;
    public event Action? SettingsRequested;
    public event Action? ReviewRequested;
    private string mode = "claude";
    private bool positioned;
    private Point? dragOrigin;
    private Point dragWindow;
    private static string PositionPath => Path.Combine(App.DataRoot, "toolbar-position.json");

    public StatusBarWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            Native.MakeNonActivating(handle);
            HwndSource.FromHwnd(handle)?.AddHook(WindowMessage);
        };
        RefreshSkin();
    }

    private static IntPtr WindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0021) { handled = true; return new IntPtr(3); } // MA_NOACTIVATE: keep typing in the host.
        return IntPtr.Zero;
    }

    public void RefreshSkin(InputSkin? previewSkin = null)
    {
        var skin = previewSkin ?? SkinCatalog.Find(Preferences.Load().Skin);
        SkinStyling.Apply(this, skin);
        Background = Brushes.Transparent;
        Resources["ButtonCorner"] = new CornerRadius(Math.Min(skin.Corner, 16));
        Frame.Background = SkinCatalog.Brush(skin.Surface);
        Frame.BorderBrush = SkinCatalog.Brush(skin.Stroke);
        Frame.CornerRadius = new CornerRadius(skin.Corner);
        UpdateModeButtons();
    }

    public void Render(string currentMode, string status, bool canOpenCommands, string commandHint, bool showWindow = true)
    {
        if (mode != currentMode) { mode = currentMode; UpdateModeButtons(); }
        Status.Text = status;
        Status.ToolTip = status == "字母直输 · Shift 切回" ? "按一下 Shift 恢复拼音输入" : EnglishButton.ToolTip;
        CommandsButton.IsEnabled = canOpenCommands;
        CommandsButton.ToolTip = commandHint;
        System.Windows.Controls.ToolTipService.SetShowOnDisabled(CommandsButton, true);
        if (!showWindow) return;
        if (!IsVisible) Show();
        if (!positioned)
        {
            UpdateLayout();
            var area = System.Windows.Forms.Screen.FromHandle(Native.GetForegroundWindow()).WorkingArea;
            var dpi = VisualTreeHelper.GetDpi(this);
            var position = ToolbarPositionStore.Load(PositionPath,
                new System.Drawing.Point(area.Right - (int)(ActualWidth * dpi.DpiScaleX) - 24,
                                         area.Bottom - (int)(ActualHeight * dpi.DpiScaleY) - 18));
            MoveClamped(position.X, position.Y);
            positioned = true;
        }
    }

    private void UpdateModeButtons()
    {
        void Select(Button button, bool selected)
        {
            button.SetResourceReference(BackgroundProperty, selected ? "Accent" : "Surface");
            button.SetResourceReference(ForegroundProperty, selected ? "AccentText" : "Ink");
            button.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
            System.Windows.Automation.AutomationProperties.SetItemStatus(button, selected ? "已选中" : "未选中");
        }
        Select(ChineseButton, mode == "normal");
        Select(EnglishButton, mode == "claude");
    }

    private void MoveClamped(double x, double y)
    {
        var area = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((int)x, (int)y)).WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(this);
        int width = (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX), height = (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY);
        Native.SetWindowPos(new WindowInteropHelper(this).Handle, IntPtr.Zero,
            (int)Math.Clamp(x, area.Left, Math.Max(area.Left, area.Right - width)),
            (int)Math.Clamp(y, area.Top, Math.Max(area.Top, area.Bottom - height)), 0, 0, 0x0015);
    }

    private void DragStarted(object sender, MouseButtonEventArgs e)
    {
        dragOrigin = PointToScreen(e.GetPosition(this));
        dragWindow = PointToScreen(new Point());
        Grip.CaptureMouse(); e.Handled = true;
    }
    private void DragMoved(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (dragOrigin is not Point start || e.LeftButton != MouseButtonState.Pressed) return;
        var current = PointToScreen(e.GetPosition(this));
        MoveClamped(dragWindow.X + current.X - start.X, dragWindow.Y + current.Y - start.Y);
    }
    private void DragFinished(object sender, MouseButtonEventArgs e)
    {
        dragOrigin = null; Grip.ReleaseMouseCapture();
        var point = PointToScreen(new Point());
        try
        {
            ToolbarPositionStore.Save(PositionPath, new System.Drawing.Point((int)point.X, (int)point.Y));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        e.Handled = true;
    }
    private void ChineseClicked(object sender, RoutedEventArgs e) => ModeSelected?.Invoke("normal");
    private void EnglishClicked(object sender, RoutedEventArgs e) => ModeSelected?.Invoke("claude");
    private void CommandsClicked(object sender, RoutedEventArgs e) => CommandsRequested?.Invoke();
    private void SkinClicked(object sender, RoutedEventArgs e) => SkinRequested?.Invoke();
    private void SettingsClicked(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();
    private void ReviewClicked(object sender, RoutedEventArgs e) => ReviewRequested?.Invoke();
}
