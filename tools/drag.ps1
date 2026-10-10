param([int]$x1,[int]$y1,[int]$x2,[int]$y2)
# Left button down at (x1,y1), pointer moved to (x2,y2) in steps, released there. For the seek bar.
Add-Type @"
using System.Runtime.InteropServices;
public class D { [DllImport("user32.dll")] public static extern bool SetProcessDPIAware(); [DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y); [DllImport("user32.dll")] public static extern void mouse_event(int f,int dx,int dy,int d,int e);}
"@
[D]::SetProcessDPIAware()|Out-Null
[D]::SetCursorPos($x1,$y1)|Out-Null; Start-Sleep -Milliseconds 200
[D]::mouse_event(2,0,0,0,0); Start-Sleep -Milliseconds 100
for ($i=1; $i -le 10; $i++) {
  [D]::SetCursorPos([int]($x1+($x2-$x1)*$i/10),[int]($y1+($y2-$y1)*$i/10))|Out-Null; Start-Sleep -Milliseconds 40
}
Start-Sleep -Milliseconds 100
[D]::mouse_event(4,0,0,0,0)
