using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using ClaudeZhInput.Commands;

namespace ClaudeZhInput.Commands.Wpf;

/// <summary>供宿主通过 ResourceDictionary 覆盖的主题资源键。</summary>
public static class CommandPickerResourceKeys
{
    public const string BackgroundBrush = "CommandPickerBackgroundBrush";
    public const string SurfaceBrush = "CommandPickerSurfaceBrush";
    public const string InputBrush = "CommandPickerInputBrush";
    public const string ForegroundBrush = "CommandPickerForegroundBrush";
    public const string SecondaryForegroundBrush = "CommandPickerSecondaryForegroundBrush";
    public const string MutedForegroundBrush = "CommandPickerMutedForegroundBrush";
    public const string AccentBrush = "CommandPickerAccentBrush";
    public const string SelectionBrush = "CommandPickerSelectionBrush";
    public const string HoverBrush = "CommandPickerHoverBrush";
    public const string BorderBrush = "CommandPickerBorderBrush";
    public const string CornerRadius = "CommandPickerCornerRadius";
    public const string ControlBorderThickness = "CommandPickerControlBorderThickness";
}

/// <summary>命令分类筛选项。</summary>
public sealed record CommandPickerCategoryFilter(string DisplayName, ClaudeCommandCategory? Category);

/// <summary>
/// Claude 常用命令选择窗口。窗口只返回用户选中的 FillText，不执行 Claude 命令。
/// </summary>
public partial class CommandPickerWindow : Window
{
    private readonly ClaudeCommandLibrary library;
    private bool refreshing;

    public CommandPickerWindow(ClaudeCommandLibrary library, string? initialQuery = null,
        ResourceDictionary? themeResources = null)
    {
        ArgumentNullException.ThrowIfNull(library);
        this.library = library;
        InitializeComponent();
        DataContext = this;

        Categories.Add(new CommandPickerCategoryFilter("全部分类", null));
        Categories.Add(new CommandPickerCategoryFilter("日常与上下文", ClaudeCommandCategory.Daily));
        Categories.Add(new CommandPickerCategoryFilter("上下文", ClaudeCommandCategory.Context));
        Categories.Add(new CommandPickerCategoryFilter("模型", ClaudeCommandCategory.Model));
        Categories.Add(new CommandPickerCategoryFilter("权限与设置", ClaudeCommandCategory.Permissions));
        Categories.Add(new CommandPickerCategoryFilter("计划与任务", ClaudeCommandCategory.Planning));
        Categories.Add(new CommandPickerCategoryFilter("会话", ClaudeCommandCategory.Session));
        Categories.Add(new CommandPickerCategoryFilter("审查与验证", ClaudeCommandCategory.Review));
        Categories.Add(new CommandPickerCategoryFilter("诊断", ClaudeCommandCategory.Diagnostics));
        Categories.Add(new CommandPickerCategoryFilter("扩展", ClaudeCommandCategory.Extension));
        Categories.Add(new CommandPickerCategoryFilter("自动化", ClaudeCommandCategory.Automation));
        CategoryBox.SelectedIndex = 0;

        if (!string.IsNullOrWhiteSpace(initialQuery)) SearchBox.Text = initialQuery;
        ApplyTheme(themeResources);
        RefreshCommands();
    }

    /// <summary>当前筛选后的命令候选，供 XAML 绑定。</summary>
    public ObservableCollection<ClaudeCommandEntry> VisibleCommands { get; } = new();

    /// <summary>分类筛选项，供 XAML 绑定。</summary>
    public ObservableCollection<CommandPickerCategoryFilter> Categories { get; } = new();

    /// <summary>当前选中条目。</summary>
    public ClaudeCommandEntry? SelectedCommand { get; set; }

    /// <summary>确认后的斜杠命令文本，例如 /model；取消时为 null。</summary>
    public string? PickedCommand { get; private set; }

    /// <summary>确认后的完整条目；取消时为 null。</summary>
    public ClaudeCommandEntry? PickedEntry { get; private set; }

    /// <summary>
    /// 简单入口：打开模态选择窗口，Enter 确认后返回 FillText，取消或 Esc 返回 null。
    /// </summary>
    public static string? ShowDialog(Window? owner, ClaudeCommandLibrary library,
        string? initialQuery = null, ResourceDictionary? themeResources = null)
    {
        var picker = new CommandPickerWindow(library, initialQuery, themeResources)
        {
            Owner = owner
        };
        picker.WindowStartupLocation = owner == null
            ? WindowStartupLocation.CenterScreen
            : WindowStartupLocation.CenterOwner;
        picker.ShowDialog();
        return picker.PickedCommand;
    }

    /// <summary>
    /// 把调用方的主题资源复制到窗口资源；支持背景、前景、强调色和圆角覆盖。
    /// </summary>
    public void ApplyTheme(ResourceDictionary? themeResources)
    {
        if (themeResources == null) return;
        foreach (var key in themeResources.Keys.Cast<object>().ToArray())
            Resources[key] = themeResources[key];
    }

    private void RefreshCommands()
    {
        if (refreshing) return;
        refreshing = true;
        try
        {
            var previousCommand = SelectedCommand?.FillText;
            var filter = (CategoryBox.SelectedItem as CommandPickerCategoryFilter)?.Category;
            var items = library.Search(SearchBox.Text, limit: 200, category: filter);

            VisibleCommands.Clear();
            foreach (var item in items) VisibleCommands.Add(item);

            var retained = previousCommand == null
                ? null
                : VisibleCommands.FirstOrDefault(item => item.FillText == previousCommand);
            SelectedCommand = retained ?? VisibleCommands.FirstOrDefault();
            CommandList.SelectedItem = SelectedCommand;
            if (SelectedCommand != null) CommandList.ScrollIntoView(SelectedCommand);
        }
        finally
        {
            refreshing = false;
        }
    }

    private void ConfirmSelection()
    {
        var selected = SelectedCommand ?? CommandList.SelectedItem as ClaudeCommandEntry;
        if (selected == null) return;

        // 只保存填入文本；没有 Process、SendKeys 或回车提交逻辑。
        PickedEntry = selected;
        PickedCommand = selected.FillText;
        CloseWithDialogResult(true);
    }

    private void CancelSelection()
    {
        PickedEntry = null;
        PickedCommand = null;
        CloseWithDialogResult(false);
    }

    private void CloseWithDialogResult(bool result)
    {
        try
        {
            // ShowDialog 模式下设置 DialogResult 会关闭窗口并返回给调用方。
            DialogResult = result;
        }
        catch (InvalidOperationException)
        {
            // 允许宿主在调试或嵌入场景以 Show() 打开；仍只保留选择结果。
            Close();
        }
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
        if (SelectedCommand != null) CommandList.ScrollIntoView(SelectedCommand);
    }

    private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        => RefreshCommands();

    private void CategoryBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        => RefreshCommands();

    private void CancelButton_Click(object sender, RoutedEventArgs e) => CancelSelection();

    private void CommandList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        => ConfirmSelection();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                if (!e.IsRepeat) ConfirmSelection();
                break;
            case Key.Escape:
                e.Handled = true;
                if (!e.IsRepeat) CancelSelection();
                break;
            case Key.Down:
                e.Handled = true;
                MoveSelection(1);
                break;
            case Key.Up:
                e.Handled = true;
                MoveSelection(-1);
                break;
        }
    }

    private void MoveSelection(int offset)
    {
        if (VisibleCommands.Count == 0) return;
        var current = CommandList.SelectedIndex;
        if (current < 0) current = 0;
        var next = Math.Clamp(current + offset, 0, VisibleCommands.Count - 1);
        CommandList.SelectedIndex = next;
        SelectedCommand = VisibleCommands[next];
        CommandList.ScrollIntoView(SelectedCommand);
    }
}
