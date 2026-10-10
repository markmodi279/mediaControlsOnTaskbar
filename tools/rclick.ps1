param([int]$x,[int]$y)
Add-Type @"
using System.Runtime.InteropServices;
public class R { [DllImport("user32.dll")] public static extern bool SetProcessDPIAware(); [DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y); [DllImport("user32.dll")] public static extern void mouse_event(int f,int dx,int dy,int d,int e);}
"@
[R]::SetProcessDPIAware()|Out-Null; [R]::SetCursorPos($x,$y)|Out-Null; Start-Sleep -Milliseconds 200
[R]::mouse_event(8,0,0,0,0); [R]::mouse_event(16,0,0,0,0)
