using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace ClaudeZhInput;
internal static class Native
{
    public const int HotkeyId = 9041;
    public const int ShowMessage = 0x8001;
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string? className, string title);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd,IntPtr after,int x,int y,int width,int height,uint flags);
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread,ref GuiThreadInfo info);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd,ref Point point);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd,IntPtr pid);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd,out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent,IntPtr after,string? className,string title);
    [DllImport("user32.dll")] private static extern IntPtr SendMessageTimeout(IntPtr hwnd,uint message,IntPtr wParam,IntPtr lParam,uint flags,uint timeout,out UIntPtr result);
    [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd,int index);
    [DllImport("user32.dll",EntryPoint="SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd,int index,IntPtr value);
    public static void MakeNonActivating(IntPtr hwnd) => SetWindowLongPtr(hwnd,-20,new IntPtr(GetWindowLongPtr(hwnd,-20).ToInt64()|0x08000000|0x80));

    // Query the foreground application's TSF thread. A connected pipe alone does not mean this IME is selected.
    public static bool TryGetActiveInput(out IntPtr wakeWindow, out int processId)
    {
        wakeWindow=IntPtr.Zero; processId=0;
        var foreground=GetForegroundWindow();
        if(foreground==IntPtr.Zero)return false;
        var thread=GetWindowThreadProcessId(foreground,out var pid);
        var info=new GuiThreadInfo{Size=Marshal.SizeOf<GuiThreadInfo>()};
        if(GetGUIThreadInfo(thread,ref info)&&info.Focus!=IntPtr.Zero)
            thread=GetWindowThreadProcessId(info.Focus,out pid);
        var window=IntPtr.Zero;
        while((window=FindWindowEx(new IntPtr(-3),window,null,"ClaudeZhInput NativeCandidate wake"))!=IntPtr.Zero)
        {
            var ownerThread=GetWindowThreadProcessId(window,out var ownerPid);
            if(ownerPid!=pid||ownerThread!=thread)continue;
            if(SendMessageTimeout(window,0x8051,IntPtr.Zero,IntPtr.Zero,0x0002,40,out var result)!=IntPtr.Zero&&result.ToUInt64()==1)
            {wakeWindow=window;processId=(int)pid;return true;}
        }
        return false;
    }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd,out Rect rect);
    [StructLayout(LayoutKind.Sequential)]private struct Rect{public int Left,Top,Right,Bottom;}
    [StructLayout(LayoutKind.Sequential)]private struct Point{public int X,Y;}
    [StructLayout(LayoutKind.Sequential)]private struct GuiThreadInfo{public int Size;public uint Flags;public IntPtr Active,Focus,Capture,MenuOwner,MoveSize,Caret;public Rect CaretRect;}
    public static (int X,int Y) CaretPosition()
    {
        var foreground=GetForegroundWindow();
        var info=new GuiThreadInfo{Size=Marshal.SizeOf<GuiThreadInfo>()};
        if(GetGUIThreadInfo(GetWindowThreadProcessId(foreground,IntPtr.Zero),ref info)&&info.Caret!=IntPtr.Zero)
        {var point=new Point{X=info.CaretRect.Left,Y=info.CaretRect.Bottom+6};if(ClientToScreen(info.Caret,ref point))return(point.X,point.Y);}
        if(GetWindowRect(foreground,out var rect))return(rect.Left+24,rect.Bottom-100);
        return(24,300);
    }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    public static void StyleWindow(IntPtr hwnd)
    {
        try { int rounded = 2; DwmSetWindowAttribute(hwnd, 33, ref rounded, sizeof(int)); } catch { }
    }
}
