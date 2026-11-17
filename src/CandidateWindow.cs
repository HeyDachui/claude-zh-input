using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Runtime.InteropServices;
using System.IO;
using System.Windows.Media.Imaging;

namespace ClaudeZhInput;
public sealed class CandidateWindow:Window
{
    private readonly Border frame=new();private readonly StackPanel root=new();private readonly TextBlock sentence=new(),preedit=new(),footer=new();
    private readonly WrapPanel candidates=new();
    private InputSkin skin=SkinCatalog.All[0];
    public event Action<int>? Selected;
    public CandidateWindow()
    {
        WindowStyle=WindowStyle.None;AllowsTransparency=true;Background=System.Windows.Media.Brushes.Transparent;ShowInTaskbar=false;Topmost=true;ShowActivated=false;Focusable=false;
        SizeToContent=SizeToContent.WidthAndHeight;MaxWidth=800;FontFamily=new System.Windows.Media.FontFamily("Noto Sans SC, Segoe UI");FontSize=16;
        sentence.TextWrapping=TextWrapping.Wrap;sentence.MaxWidth=660;sentence.Margin=new Thickness(0,0,0,7);sentence.FontSize=16;
        preedit.FontSize=15;preedit.Margin=new Thickness(0,0,0,6);
        candidates.MaxWidth=720;footer.FontSize=11;footer.Margin=new Thickness(0,7,0,0);
        root.Children.Add(sentence);root.Children.Add(preedit);root.Children.Add(candidates);root.Children.Add(footer);frame.Child=root;Content=frame;
        SourceInitialized+=(_,_)=>{var h=new WindowInteropHelper(this).Handle;SetWindowLongPtr(h,-20,new IntPtr(GetWindowLongPtr(h,-20).ToInt64()|0x08000000|0x00000080));};
        RefreshSkin();
    }
    [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")]private static extern IntPtr GetWindowLongPtr(IntPtr h,int index);
    [DllImport("user32.dll",EntryPoint="SetWindowLongPtrW")]private static extern IntPtr SetWindowLongPtr(IntPtr h,int index,IntPtr value);
    public void RefreshSkin(InputSkin? previewSkin=null)
    {
        skin=previewSkin??SkinCatalog.Find(Preferences.Load().Skin);frame.Background=SkinCatalog.Pattern(skin);frame.BorderBrush=SkinCatalog.Brush(skin.Stroke);frame.BorderThickness=new Thickness(skin.Id=="orbit"?2:1);
        frame.CornerRadius=new CornerRadius(skin.Corner);frame.Padding=new Thickness(skin.Id=="cloud"?20:14,12,18,10);
        sentence.Foreground=SkinCatalog.Brush(skin.Ink);preedit.Foreground=SkinCatalog.Brush(skin.Accent);footer.Foreground=SkinCatalog.Brush(skin.Muted);
        candidates.Orientation=skin.Id=="orbit"?System.Windows.Controls.Orientation.Vertical:System.Windows.Controls.Orientation.Horizontal;
    }
    public void RenderState(string chinese,string pinyin,IReadOnlyList<string> items,int selected,string mode,double? x,double? y,string? status=null,InputSkin? previewSkin=null,bool showWindow=true)
    {
        RefreshSkin(previewSkin);sentence.Text=chinese;sentence.Visibility=string.IsNullOrEmpty(chinese)?Visibility.Collapsed:Visibility.Visible;
        preedit.Text=pinyin;preedit.Visibility=string.IsNullOrEmpty(pinyin)?Visibility.Collapsed:Visibility.Visible;
        candidates.Children.Clear();
        for(int i=0;i<items.Count;i++)
        {
            int index=i;var panel=new Border{CornerRadius=new CornerRadius(skin.Corner),Background=SkinCatalog.Brush(i==selected?skin.Accent:skin.Surface),BorderBrush=SkinCatalog.Brush(skin.Stroke),BorderThickness=new Thickness(1),Padding=new Thickness(skin.Id=="cloud"?14:9,6,12,6),Margin=new Thickness(0,0,6,4)};
            panel.Child=new TextBlock{Text=$"{i+1}  {items[i]}",Foreground=SkinCatalog.Brush(i==selected?skin.AccentText:skin.Ink),FontSize=17};
            panel.MouseLeftButtonDown+=(_,e)=>{Selected?.Invoke(index);e.Handled=true;};candidates.Children.Add(panel);
        }
        footer.Text=status??(mode=="claude"?"Claude · 句末转英文 · ;; 指令":"中文 · 空格选词 · 数字选候选");
        if (!showWindow) return; // Offscreen verification never creates a desktop surface.
        if(!IsVisible)Show();UpdateLayout();
        var fallback=Native.CaretPosition();
        var rawX=(int)(x??fallback.X);var rawY=(int)(y??fallback.Y);
        var area=System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(rawX,rawY)).WorkingArea;
        var dpi=VisualTreeHelper.GetDpi(this);
        var width=(int)Math.Ceiling(ActualWidth*dpi.DpiScaleX);var height=(int)Math.Ceiling(ActualHeight*dpi.DpiScaleY);
        var left=Math.Clamp(rawX,area.Left,Math.Max(area.Left,area.Right-width));
        var top=rawY+height>area.Bottom?Math.Max(area.Top,rawY-height-24):Math.Max(area.Top,rawY);
        Native.SetWindowPos(new WindowInteropHelper(this).Handle,IntPtr.Zero,left,top,0,0,0x0001|0x0004|0x0010);
    }
    internal void SaveSnapshot(string path)
    {
        UpdateLayout();var dpi=VisualTreeHelper.GetDpi(frame);
        var bitmap=new RenderTargetBitmap((int)Math.Ceiling(frame.ActualWidth*dpi.DpiScaleX),(int)Math.Ceiling(frame.ActualHeight*dpi.DpiScaleY),dpi.PixelsPerInchX,dpi.PixelsPerInchY,PixelFormats.Pbgra32);
        bitmap.Render(frame);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var stream=File.Create(path);encoder.Save(stream);
    }
}
