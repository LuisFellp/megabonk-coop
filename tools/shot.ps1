param([string]$Out = "C:\MegabonkCoop-DevLab\shots\win.png", [int]$ProcId = 0)
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public class W { 
 public delegate bool EnumProc(IntPtr h, IntPtr l);
 [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, IntPtr l);
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
 [DllImport("user32.dll", CharSet=CharSet.Auto)] public static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);
 [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
 [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
 [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
 [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
 [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
 [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
}
"@
$p = if ($ProcId) { Get-Process -Id $ProcId } else { Get-Process Megabonk | Where-Object { $_.Path -like '*MegabonkCoop-DevLab*' } | Select-Object -First 1 }
$script:h = [IntPtr]::Zero
$pid0 = [uint32]$p.Id
[W]::EnumWindows({ param($hw, $l) $x = [uint32]0; [W]::GetWindowThreadProcessId($hw, [ref]$x) | Out-Null; if ($x -eq $pid0 -and [W]::IsWindowVisible($hw)) { $sb = New-Object System.Text.StringBuilder 256; [W]::GetClassName($hw, $sb, 256) | Out-Null; if ($sb.ToString() -eq 'UnityWndClass') { $script:h = $hw } }; return $true }, [IntPtr]::Zero) | Out-Null
$h = $script:h
[W]::ShowWindow($h, 9) | Out-Null; [W]::SetForegroundWindow($h) | Out-Null; Start-Sleep -Milliseconds 800
$r = New-Object W+RECT; [W]::GetClientRect($h, [ref]$r) | Out-Null
$pt = New-Object W+POINT; [W]::ClientToScreen($h, [ref]$pt) | Out-Null
$w = $r.R - $r.L; $hh = $r.B - $r.T
$bmp = New-Object System.Drawing.Bitmap $w, $hh
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($pt.X, $pt.Y, 0, 0, (New-Object System.Drawing.Size $w, $hh))
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
"saved $Out ${w}x${hh}"
