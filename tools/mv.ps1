param([int]$x,[int]$y)
Add-Type @"
using System.Runtime.InteropServices;
public class H { [DllImport("user32.dll")] public static extern bool SetProcessDPIAware(); [DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y);}
"@
[H]::SetProcessDPIAware()|Out-Null; [H]::SetCursorPos($x,$y)|Out-Null
