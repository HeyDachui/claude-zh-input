using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Pen = System.Windows.Media.Pen;

namespace ClaudeZhInput;
public record InputSkin(string Id,string Name,string Description,string Layout,string Background,string Surface,string Ink,string Muted,string Stroke,string Accent,string AccentText,int Corner);
public static class SkinCatalog
{
    public static System.Windows.Media.FontFamily UiFont => new(new Uri("pack://application:,,,/ClaudeZhInput;component/"), "./Fonts/#Noto Sans SC");
    public static IReadOnlyList<InputSkin> All {get;} = [
        new("paper","纸笺","纸角图案 · 横排候选 · 折纸边框","stacked","#EFEAE1","#FBF8F1","#332F2A","#72685F","#D9CCBC","#98482F","#FBF5EC",8),
        new("orbit","夜航","轨道星图 · 竖排候选 · 硬边面板","split","#172333","#202F42","#DFE7EE","#A7B6C8","#465E77","#82C4E8","#182A3B",2),
        new("cloud","云朵","云形图案 · 胶囊候选 · 大圆角","stacked","#E8F1F3","#F4F9F8","#2E4850","#617E84","#BDD7DC","#376F80","#F1F8F6",24)
    ];
    public static InputSkin Find(string? id)=>All.FirstOrDefault(s=>s.Id==id)??All[0];
    public static SolidColorBrush Brush(string s)=>new((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(s));
    public static DrawingBrush Pattern(InputSkin skin)
    {
        var g=new DrawingGroup();g.Children.Add(new GeometryDrawing(Brush(skin.Background),null,new RectangleGeometry(new Rect(0,0,800,750))));
        if(skin.Id=="paper")
        {
            for(int y=60;y<750;y+=34)g.Children.Add(new GeometryDrawing(null,new Pen(Brush("#DCCFBF"),1),new LineGeometry(new System.Windows.Point(0,y),new System.Windows.Point(800,y))));
            g.Children.Add(new GeometryDrawing(Brush("#D9C5A8"),null,Geometry.Parse("M 693,0 L 800,0 L 800,108 Z")));
            g.Children.Add(new GeometryDrawing(Brush("#F7F1E5"),new Pen(Brush("#CBB693"),1),Geometry.Parse("M 693,0 L 693,108 L 800,108 Z")));
        }
        else if(skin.Id=="orbit")
        {
            for(int i=0;i<3;i++)g.Children.Add(new GeometryDrawing(null,new Pen(Brush("#35526B"),1),new EllipseGeometry(new System.Windows.Point(760,20),100+i*50,68+i*41)));
            int[] xs=[65,167,303,472,612,752,717,43,606];int[] ys=[30,51,23,52,35,141,260,488,695];
            for(int i=0;i<xs.Length;i++)g.Children.Add(new GeometryDrawing(Brush("#7293AD"),null,new EllipseGeometry(new System.Windows.Point(xs[i],ys[i]),i%2+1,i%2+1)));
            g.Children.Add(new GeometryDrawing(Brush("#273E53"),null,Geometry.Parse("M 0,730 L 165,610 L 250,750 L 0,750 Z")));
        }
        else
        {
            foreach(var(x,y,r) in new[]{(675,8,70),(740,35,78),(798,8,60),(16,710,70),(90,751,90)})g.Children.Add(new GeometryDrawing(Brush("#D1E4E7"),null,new EllipseGeometry(new System.Windows.Point(x,y),r,r*0.72)));
            g.Children.Add(new GeometryDrawing(Brush("#F1DFA9"),null,new EllipseGeometry(new System.Windows.Point(750,665),27,27)));
        }
        g.ClipGeometry=new RectangleGeometry(new Rect(0,0,800,750));
        return new DrawingBrush(g){ViewboxUnits=BrushMappingMode.Absolute,Viewbox=new Rect(0,0,800,750),Stretch=Stretch.Fill};
    }
}
public sealed class Preferences
{
    public string Skin{get;set;}="paper";public string Mode{get;set;}="normal";public bool Pinned{get;set;}
    private static string FilePath=>Path.Combine(App.DataRoot,"preferences.json");
    public static Preferences Load(){try{return JsonSerializer.Deserialize<Preferences>(File.ReadAllText(FilePath))??new();}catch{return new();}}
    public void Save(){var temp=FilePath+".tmp";File.WriteAllText(temp,JsonSerializer.Serialize(this));File.Move(temp,FilePath,true);}
}
public sealed class SkinPicker:Window
{
    public string? SelectedSkin{get;private set;}
    public SkinPicker(string current)
    {
        Title="选择皮肤";Width=570;Height=415;ResizeMode=ResizeMode.NoResize;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        Background=SkinCatalog.Brush("#EEEAE3");FontFamily=new System.Windows.Media.FontFamily("Noto Sans SC, Segoe UI");
        var root=new StackPanel{Margin=new Thickness(22)};
        root.Children.Add(new TextBlock{Text="换一种输入手感",FontSize=23,FontWeight=FontWeights.SemiBold,Foreground=SkinCatalog.Brush("#332F2A")});
        root.Children.Add(new TextBlock{Text="背景图案、候选形状和面板布局一起改变",Margin=new Thickness(0,5,0,18),FontSize=12,Foreground=SkinCatalog.Brush("#72685F")});
        foreach(var skin in SkinCatalog.All)
        {
            var grid=new Grid{Background=SkinCatalog.Pattern(skin),Margin=new Thickness(0,0,0,10),Height=77};
            grid.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(100)});grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.Children.Add(new Border{Background=SkinCatalog.Brush(skin.Surface),BorderBrush=SkinCatalog.Brush(skin.Stroke),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(skin.Corner),Margin=new Thickness(12),Child=new TextBlock{Text="中 → EN",HorizontalAlignment=System.Windows.HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center,Foreground=SkinCatalog.Brush(skin.Accent),FontWeight=FontWeights.SemiBold}});
            var label=new StackPanel{VerticalAlignment=VerticalAlignment.Center};Grid.SetColumn(label,1);
            label.Children.Add(new TextBlock{Text=skin.Name+(skin.Id==current?"  · 当前":""),FontWeight=FontWeights.SemiBold,FontSize=16,Foreground=SkinCatalog.Brush(skin.Ink)});
            label.Children.Add(new TextBlock{Text=skin.Description,FontSize=12,Foreground=SkinCatalog.Brush(skin.Muted),Margin=new Thickness(0,4,0,0)});grid.Children.Add(label);
            var button=new System.Windows.Controls.Button{Content=grid,Padding=new Thickness(0),HorizontalContentAlignment=System.Windows.HorizontalAlignment.Stretch};
            button.Click+=(_,_)=>{SelectedSkin=skin.Id;DialogResult=true;};root.Children.Add(button);
        }
        Content=root;
    }
}
