Add-Type @"
using System; using System.Text; using System.Runtime.InteropServices;
public class TB {
 [StructLayout(LayoutKind.Sequential)] public struct RC { public int L,T,Rt,B; }
 public delegate bool EP(IntPtr h, IntPtr l);
 [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
 [DllImport("user32.dll")] public static extern IntPtr FindWindow(string c,string t);
 [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr p, EP cb, IntPtr l);
 [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr h);
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RC r);
 [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
}
"@
[TB]::SetProcessDPIAware() | Out-Null
$tray=[TB]::FindWindow("Shell_TrayWnd",[NullString]::Value)
$r=New-Object TB+RC; [TB]::GetWindowRect($tray,[ref]$r)|Out-Null
"Shell_TrayWnd  L=$($r.L) T=$($r.T) R=$($r.Rt) B=$($r.B)"
$script:lines=New-Object System.Collections.ArrayList
$cb=[TB+EP]{ param($h,$l)
  $c=New-Object System.Text.StringBuilder 256; $t=New-Object System.Text.StringBuilder 256
  [TB]::GetClassName($h,$c,256)|Out-Null; [TB]::GetWindowText($h,$t,256)|Out-Null
  $rc=New-Object TB+RC; [TB]::GetWindowRect($h,[ref]$rc)|Out-Null
  $p=[TB]::GetParent($h); $pc=New-Object System.Text.StringBuilder 256; [TB]::GetClassName($p,$pc,256)|Out-Null
  $pid2=0; [TB]::GetWindowThreadProcessId($h,[ref]$pid2)|Out-Null
  $vis=[TB]::IsWindowVisible($h)
  [void]$script:lines.Add(("{0,-34} parent={1,-18} vis={2,-5} x={3,5}..{4,-5} y={5}..{6}  pid={7} '{8}'" -f $c,$pc,$vis,$rc.L,$rc.Rt,$rc.T,$rc.B,$pid2,$t))
  return $true }
[TB]::EnumChildWindows($tray,$cb,[IntPtr]::Zero)|Out-Null
$script:lines
