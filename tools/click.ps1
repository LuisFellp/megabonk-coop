param([int]$ProcId, [int]$X, [int]$Y)   # X,Y in CLIENT pixels of the Unity window
Add-Type @"
using System; using System.Runtime.InteropServices;
public class C {
 public delegate bool EnumProc(IntPtr h, IntPtr l);
 [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, IntPtr l);
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
 [DllImport("user32.dll", CharSet=CharSet.Auto)] public static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);
 [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
 [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
 [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
 [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint x, uint y, uint d, UIntPtr e);
 [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
}
"@
$script:h = [IntPtr]::Zero; $pid0 = [uint32]$ProcId
[C]::EnumWindows({ param($hw, $l) $x = [uint32]0; [C]::GetWindowThreadProcessId($hw, [ref]$x) | Out-Null; if ($x -eq $pid0 -and [C]::IsWindowVisible($hw)) { $sb = New-Object System.Text.StringBuilder 256; [C]::GetClassName($hw, $sb, 256) | Out-Null; if ($sb.ToString() -eq 'UnityWndClass') { $script:h = $hw } }; return $true }, [IntPtr]::Zero) | Out-Null
[C]::SetForegroundWindow($script:h) | Out-Null; Start-Sleep -Milliseconds 500
$pt = New-Object C+POINT; $pt.X = $X; $pt.Y = $Y; [C]::ClientToScreen($script:h, [ref]$pt) | Out-Null
[C]::SetCursorPos($pt.X, $pt.Y) | Out-Null; Start-Sleep -Milliseconds 400
[C]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 120; [C]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
"clicked at screen $($pt.X),$($pt.Y)"
