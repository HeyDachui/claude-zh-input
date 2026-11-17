using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Diagnostics;
using System.Windows.Threading;

namespace ClaudeZhInput;
public partial class MainWindow:Window
{
    private bool ready;
    private Preferences preferences;
    private readonly bool preview;
    private TranslationReviewStore? reviews;
    private BehaviorLog? behavior;
    private DispatcherTimer? reviewTimer;
    public sealed record ReviewRow(string Label, TranslationReview Entry);
    public event Action? PreferencesChanged;
    public void SetStatus(string text) => StatusLabel.Text = text;
    public void SetInputComponents(IEnumerable<string> versions)
    {
        var values=versions.Distinct().Take(3).ToArray();
        var status=values.Length==0 ? "输入组件未连接" : "输入组件 "+string.Join(" / ",values);
        if(values.Any(v=>v!=ProductMaintenance.InputComponentVersion))status+="（请重开旧应用）";
        VersionLabel.Text=$"后台 {ProductMaintenance.Version} · {status} · 小范围试用版";
    }
    public MainWindow(bool preview = false)
    {
        this.preview=preview;
        InitializeComponent();preferences=preview ? new() : Preferences.Load();
        VersionLabel.Text=$"{ProductMaintenance.Version} · Windows x64 · 小范围试用版";
        ApplySkin();
        NormalMode.IsChecked=preferences.Mode=="normal";ClaudeMode.IsChecked=preferences.Mode!="normal";
        ready=true;if(!preview)SavePreferences();RefreshPhraseTable();
        SourceInitialized+=(_,_)=>Native.StyleWindow(new WindowInteropHelper(this).Handle);
    }
    public void ToggleMode()
    {if(preferences.Mode=="claude")NormalMode.IsChecked=true;else ClaudeMode.IsChecked=true;}
    public void SetMode(string mode)
    {if(mode=="normal")NormalMode.IsChecked=true;else ClaudeMode.IsChecked=true;}
    private void ModeChanged(object sender,RoutedEventArgs e)
    {
        if(!ready)return;
        preferences.Mode=NormalMode.IsChecked==true?"normal":"claude";
        SavePreferences();StatusLabel.Text=preferences.Mode=="normal"?"普通模式，直接输入中文":"整句英文模式，句末自动输出英文";
    }
    private void SavePreferences()
    {
        if(preview)return;
        preferences.Save();
        var statePath=Path.Combine(App.DataRoot,"ime-state.json");var tmp=statePath+".tmp";
        File.WriteAllText(tmp,JsonSerializer.Serialize(new {mode=preferences.Mode,theme=preferences.Skin,candidateLimit=9,enabled=true}));
        File.Move(tmp,statePath,true);PreferencesChanged?.Invoke();
    }
    private void ApplySkin()
    {
        var skin=SkinCatalog.Find(preferences.Skin);SkinStyling.Apply(this,skin);
        Scene.Background=SkinCatalog.Pattern(skin);SceneVeil.Background=SkinCatalog.Brush(skin.Background);SceneVeil.Opacity=0.82;
        SkinDescription.Text=skin.Name+" · "+skin.Description;
    }
    private void SkinClicked(object sender,RoutedEventArgs e)
        => ShowSkinPicker(this);
    public void ShowSkinPicker(Window? owner = null)
    {
        var picker=new SkinPicker(preferences.Skin){Owner=owner,WindowStartupLocation=owner==null?WindowStartupLocation.CenterScreen:WindowStartupLocation.CenterOwner};
        if(picker.ShowDialog()==true && picker.SelectedSkin!=null){preferences.Skin=picker.SelectedSkin;ApplySkin();SavePreferences();}
    }
    private void CommandsClicked(object sender,RoutedEventArgs e)
    {App.ShowCommands(this);}
    public void AttachMaintenance(TranslationReviewStore store, BehaviorLog? log)
    {
        reviews=store;behavior=log;ready=false;Recording.IsChecked=log?.Enabled==true;ready=true;
        if (!preview) reviewTimer=new DispatcherTimer(TimeSpan.FromSeconds(5),DispatcherPriority.Background,(_,_)=>{if(Pages.SelectedIndex==1)RefreshReviews();},Dispatcher);
    }
    public void ShowReviewPage() { Pages.SelectedIndex=1;RefreshReviews(); }
    public void SetPreviewSkin(InputSkin skin) { SkinStyling.Apply(this,skin);Scene.Background=SkinCatalog.Pattern(skin);SceneVeil.Background=SkinCatalog.Brush(skin.Background);SkinDescription.Text=skin.Name+" · "+skin.Description; }
    public void SetPreviewPage(int page) { Pages.SelectedIndex=page; }
    private void TabChanged(object sender,SelectionChangedEventArgs e) { if(ready && e.Source==Pages && Pages.SelectedIndex==1)RefreshReviews(); }
    private void RefreshReviews()
    {
        var previous=(RecentList.SelectedItem as ReviewRow)?.Entry;
        var entries=reviews?.Snapshot()??[];
        RecentList.ItemsSource=entries.Select((entry,i)=>new ReviewRow($"{entry.CreatedAt.LocalDateTime:HH:mm:ss} · {(entry.Held?"已保留原文":"生成译文")} · 第 {i+1} 条",entry)).ToArray();
        RecentList.SelectedIndex=entries.Length==0 ? -1 : Math.Max(0,Array.IndexOf(entries,previous));
        if(entries.Length==0){SourceText.Text=DraftText.Text="";ReviewNotice.Text="暂无记录。生成结果不代表已写入或发送到原应用。";}
    }
    private void ReviewSelected(object sender,SelectionChangedEventArgs e)
    {
        if(RecentList.SelectedItem is not ReviewRow row)return;
        SourceText.Text=row.Entry.Source;DraftText.Text=row.Entry.Draft;
        ReviewNotice.Text=string.Join(" ",row.Entry.Warnings);
        if(ReviewNotice.Text.Length==0)ReviewNotice.Text="需要时可复制原文或英文；列表不记录原应用是否已接收或发送。";
    }
    private void RunMaintenance(Action action)
    { try{action();}catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or System.Runtime.InteropServices.ExternalException or System.ComponentModel.Win32Exception or KeyNotFoundException or FormatException){SetStatus("操作未完成："+ex.Message);} }
    private void CopySourceClicked(object sender,RoutedEventArgs e)=>RunMaintenance(()=>{if(SourceText.Text.Length>0){System.Windows.Clipboard.SetText(SourceText.Text);SetStatus("原文已复制，请回到需要的输入框自行粘贴。");}});
    private void CopyDraftClicked(object sender,RoutedEventArgs e)=>RunMaintenance(()=>{if(DraftText.Text.Length>0){System.Windows.Clipboard.SetText(DraftText.Text);SetStatus("英文已复制，请回到需要的输入框自行粘贴。");}});
    private void ClearReviewsClicked(object sender,RoutedEventArgs e){reviews?.Clear();RefreshReviews();}
    private void RecordingChanged(object sender,RoutedEventArgs e){if(ready&&!preview)RunMaintenance(()=>behavior?.SetEnabled(Recording.IsChecked==true));}
    private static void OpenPath(string path) => Process.Start(new ProcessStartInfo(path){UseShellExecute=true});
    private void GuideClicked(object sender,RoutedEventArgs e)=>RunMaintenance(()=>OpenPath(Path.Combine(App.Root,"使用说明.txt")));
    private void InstallFolderClicked(object sender,RoutedEventArgs e)=>RunMaintenance(()=>OpenPath(App.Root));
    private void DataFolderClicked(object sender,RoutedEventArgs e)=>RunMaintenance(()=>OpenPath(App.DataRoot));
    private void FeedbackClicked(object sender,RoutedEventArgs e)=>RunMaintenance(()=>
    {
        var dialog=new Microsoft.Win32.SaveFileDialog{FileName=$"Claude输入法反馈-{DateTime.Now:yyyyMMdd-HHmm}.txt",Filter="文本文件|*.txt"};
        if(dialog.ShowDialog(this)==true){ProductMaintenance.WriteAtomic(dialog.FileName,ProductMaintenance.Feedback(behavior?.Enabled==true));SetStatus("反馈文件已生成，请填写后自行发给提供安装包的人。");OpenPath(dialog.FileName);}
    });
    private void ExportClicked(object sender,RoutedEventArgs e)=>RunMaintenance(()=>
    {
        var dialog=new Microsoft.Win32.SaveFileDialog{FileName=$"Claude输入法设置-{DateTime.Now:yyyyMMdd}.json",Filter="设置备份|*.json"};
        if(dialog.ShowDialog(this)==true){ProductMaintenance.ExportSettings(App.DataRoot,dialog.FileName);SetStatus("设置与短语已备份；包含个人内容，请妥善保管。不含 Rime 学习词频。");}
    });
    private void ImportClicked(object sender,RoutedEventArgs e)=>RunMaintenance(()=>
    {
        var dialog=new Microsoft.Win32.OpenFileDialog{Filter="设置备份|*.json"};
        if(dialog.ShowDialog(this)!=true)return;
        if(System.Windows.MessageBox.Show(this,"将覆盖当前设置与个人短语；原设置会另存一份备份。继续恢复？",ProductMaintenance.Name,MessageBoxButton.OKCancel)!=MessageBoxResult.OK)return;
        ProductMaintenance.ImportSettings(dialog.FileName,App.DataRoot);
        ready=false;preferences=Preferences.Load();ApplySkin();NormalMode.IsChecked=preferences.Mode=="normal";ClaudeMode.IsChecked=!NormalMode.IsChecked;ready=true;SavePreferences();
        SetStatus("已恢复并保存导入前备份。完成当前输入后关闭助手，再切走、切回输入法，个人短语与记录设置将生效。");
    });
    private void DoneClicked(object sender,RoutedEventArgs e)=>Hide();
    private void WindowClosing(object? sender,CancelEventArgs e){if(!App.Exiting){e.Cancel=true;Hide();}}
    public void SaveSnapshot(string path)
    {
        UpdateLayout();var dpi=VisualTreeHelper.GetDpi(Scene);
        var target=new RenderTargetBitmap((int)(Scene.ActualWidth*dpi.DpiScaleX),(int)(Scene.ActualHeight*dpi.DpiScaleY),dpi.PixelsPerInchX,dpi.PixelsPerInchY,PixelFormats.Pbgra32);target.Render(Scene);
        var enc=new PngBitmapEncoder();enc.Frames.Add(BitmapFrame.Create(target));using var stream=File.Create(path);enc.Save(stream);
    }
}
public static class SkinStyling
{
    public static void Apply(FrameworkElement element,InputSkin skin)
    {
        foreach(var(key,value) in new[]{("Ink",skin.Ink),("Muted",skin.Muted),("Surface",skin.Surface),("Stroke",skin.Stroke),("Accent",skin.Accent),("AccentText",skin.AccentText),("Hover",skin.Background)})element.Resources[key]=SkinCatalog.Brush(value);
        element.SetValue(System.Windows.Controls.Control.ForegroundProperty,SkinCatalog.Brush(skin.Ink));
        if(element is Window window){window.Background=SkinCatalog.Brush(skin.Background);window.FontFamily=SkinCatalog.UiFont;}
    }
}
