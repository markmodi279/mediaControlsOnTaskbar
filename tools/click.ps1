param([int]$x,[int]$y)
Add-Type @"
using System.Runtime.InteropServices;
public class M {
 [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
 [DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y);
 [DllImport("user32.dll")] public static extern void mouse_event(int f,int dx,int dy,int d,int e);
}
"@
[M]::SetProcessDPIAware() | Out-Null
[M]::SetCursorPos($x,$y) | Out-Null
Start-Sleep -Milliseconds 200
[M]::mouse_event(2,0,0,0,0); [M]::mouse_event(4,0,0,0,0)
