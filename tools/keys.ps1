param([string]$chord)
Add-Type @"
using System.Runtime.InteropServices;
public class K { [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, int flags, int extra); }
"@
$vk=@{ctrl=0x11;alt=0x12;shift=0x10;space=0x20;right=0x27;left=0x25;up=0x26;down=0x28;f9=0x78;f10=0x79;m=0x4D}
$keys = $chord.ToLower().Split('+') | ForEach-Object { $vk[$_] }
foreach($k in $keys){ [K]::keybd_event([byte]$k,0,0,0); Start-Sleep -Milliseconds 30 }
Start-Sleep -Milliseconds 80
[array]::Reverse($keys)
foreach($k in $keys){ [K]::keybd_event([byte]$k,0,2,0); Start-Sleep -Milliseconds 30 }
