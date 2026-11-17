using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace ClaudeZhInput;

public partial class MainWindow
{
    private PhraseStore? phraseStore;
    public sealed record PhrasePackRow(string Label, PhrasePackInfo Pack);

    public void AttachPhraseStore(PhraseStore store)
    {
        phraseStore = store;
        RefreshPhrasePacks();
    }
    private void RefreshPhrasePacks(string? selectedId = null)
    {
        selectedId ??= (PhrasePackList.SelectedItem as PhrasePackRow)?.Pack.Id;
        var packs = phraseStore?.ListPacks() ?? [];
        PhrasePackList.ItemsSource = packs.Select(p => new PhrasePackRow($"{p.Name} · {p.Version} · {p.PhraseCount}句" + (p.IsActive ? "" : "（未启用）"), p)).ToArray();
        PhrasePackList.SelectedItem = PhrasePackList.Items.Cast<PhrasePackRow>().FirstOrDefault(p => p.Pack.Id == selectedId)
            ?? PhrasePackList.Items.Cast<PhrasePackRow>().FirstOrDefault();
        if (phraseStore?.PackWarnings.Count > 0) PhrasePackNotice.Text = string.Join("\n", phraseStore.PackWarnings);
        else if (packs.Count == 0) PhrasePackNotice.Text = "尚未导入语包。内置常用语仍可使用；导入后立即生效。";
        RefreshPhraseTable();
    }
    private void RefreshPhraseTable()
    {
        if (PhraseTable == null || PhraseSearch == null) return;
        var all = (phraseStore?.All ?? BuiltInPhrases.Create()).ToArray();
        var search = PhraseSearch.Text.Trim();
        var matches = all.Where(p => search.Length == 0 || p.Zh.Contains(search, StringComparison.OrdinalIgnoreCase)
            || p.En.Contains(search, StringComparison.OrdinalIgnoreCase)).ToArray();
        PhraseTable.ItemsSource = matches;
        PhraseCountLabel.Text = search.Length == 0 ? $"共 {all.Length} 句 · 下方可按中文或英文查找" : $"找到 {matches.Length} 句 / 共 {all.Length} 句";
    }
    private void PhraseSearchChanged(object sender, TextChangedEventArgs e) => RefreshPhraseTable();
    private void PhrasePackSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ExportPhrasePackButton == null || RemovePhrasePackButton == null) return;
        var selected = PhrasePackList.SelectedItem as PhrasePackRow;
        ExportPhrasePackButton.IsEnabled = phraseStore != null && selected?.Pack.CanExport == true;
        RemovePhrasePackButton.IsEnabled = phraseStore != null && selected != null;
    }
    private void RunPhrasePackAction(Action action)
    {
        if (phraseStore == null) { PhrasePackNotice.Text = "常用语库尚未连接。"; return; }
        try { action(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException)
        { PhrasePackNotice.Text = "操作未完成：" + ex.Message; }
    }
    private void ImportPhrasePackClicked(object sender, RoutedEventArgs e) => RunPhrasePackAction(() =>
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "中英常用语包|*.json", Title = "选择要导入的常用语包" };
        if (dialog.ShowDialog(this) != true) return;
        var inspection = phraseStore!.InspectPack(dialog.FileName);
        var info = inspection.Pack;
        var action = inspection.IsUpdate ? "更新" : "导入";
        var summary = $"{info.Name}\n版本 {info.Version} · {info.PhraseCount} 句 · 中文 → 英文\n\n" +
            (inspection.IsUpdate ? "会更新同名语包，并保留旧包备份。" : "会加入常用语库，立即生效。") +
            (inspection.PersonalOverrideCount > 0 ? $"\n其中 {inspection.PersonalOverrideCount} 句仍优先使用你的个人译法。" : "") +
            $"\n\n确认{action}？";
        if (System.Windows.MessageBox.Show(this, summary, action + "常用语包", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        phraseStore.ImportPack(dialog.FileName);
        RefreshPhrasePacks(info.Id);
        PhrasePackNotice.Text = $"已{action}“{info.Name}”，立即生效。导出只包含这个语包的词句。";
    });
    private void ExportPhrasePackClicked(object sender, RoutedEventArgs e) => RunPhrasePackAction(() =>
    {
        if (PhrasePackList.SelectedItem is not PhrasePackRow row) return;
        var dialog = new Microsoft.Win32.SaveFileDialog { FileName = row.Pack.Id + ".json", Filter = "中英常用语包|*.json", Title = "导出选中语包" };
        if (dialog.ShowDialog(this) != true) return;
        phraseStore!.ExportPack(row.Pack.Id, dialog.FileName);
        PhrasePackNotice.Text = $"已导出“{row.Pack.Name}”。只含所选语包，请确认其中词句适合分享。";
    });
    private void RemovePhrasePackClicked(object sender, RoutedEventArgs e) => RunPhrasePackAction(() =>
    {
        if (PhrasePackList.SelectedItem is not PhrasePackRow row) return;
        if (System.Windows.MessageBox.Show(this, $"移除“{row.Pack.Name}”？\n个人短语和内置常用语会保留，原包另存备份。", "移除常用语包", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        phraseStore!.RemovePack(row.Pack.Id);
        RefreshPhrasePacks();
        PhrasePackNotice.Text = $"已移除“{row.Pack.Name}”，原包已保留备份。";
    });
}
