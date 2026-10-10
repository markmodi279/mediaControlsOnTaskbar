Add-Type -AssemblyName System.Windows.Forms,System.Drawing
Add-Type @"
using System.Runtime.InteropServices;
public class D2 { [DllImport("user32.dll")] public static extern bool SetProcessDPIAware(); }
"@
[D2]::SetProcessDPIAware()|Out-Null
$b=[System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$h=360; $w=800
$bmp=New-Object System.Drawing.Bitmap $w,$h
$g=[System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($b.Right-$w,$b.Bottom-$h,0,0,(New-Object System.Drawing.Size $w,$h))
$bmp.Save('C:\Users\Public\tb2.png')
