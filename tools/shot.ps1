Add-Type -AssemblyName System.Windows.Forms,System.Drawing
Add-Type @"
using System.Runtime.InteropServices;
public class D { [DllImport("user32.dll")] public static extern bool SetProcessDPIAware(); }
"@
[D]::SetProcessDPIAware() | Out-Null
$b=[System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$h=80
$bmp=New-Object System.Drawing.Bitmap $b.Width,$h
$g=[System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($b.Left,$b.Bottom-$h,0,0,(New-Object System.Drawing.Size $b.Width,$h))
$bmp.Save('C:\Users\Public\tb.png')
"$($b.Width)x$($b.Height)"
