$ErrorActionPreference = 'SilentlyContinue'
Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public class Win {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr p, EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern int GetDlgCtrlID(IntPtr h);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
}
"@
function TextOf([IntPtr]$h) { $sb = New-Object Text.StringBuilder 512; [void][Win]::GetWindowText($h, $sb, 512); return $sb.ToString() }
function ClassOf([IntPtr]$h) { $sb = New-Object Text.StringBuilder 256; [void][Win]::GetClassName($h, $sb, 256); return $sb.ToString() }

$ts = "C:\Program Files (x86)\Triconex\TriStation 1131 5.8.0\Programs\TS1131.exe"
$psi = New-Object Diagnostics.ProcessStartInfo
$psi.FileName = $ts
$psi.WorkingDirectory = Split-Path $ts -Parent
$psi.UseShellExecute = $false
$psi.WindowStyle = 'Hidden'
$p = [Diagnostics.Process]::Start($psi)
Start-Sleep -Seconds 6
"TS1131 pid=$($p.Id) hasExited=$($p.HasExited)"

$wins = New-Object System.Collections.ArrayList
$cb = [Win+EnumProc]{
  param($h,$l)
  $pid2 = 0
  [void][Win]::GetWindowThreadProcessId($h, [ref]$pid2)
  if ($pid2 -eq $p.Id) { [void]$wins.Add($h) }
  return $true
}
[void][Win]::EnumWindows($cb, [IntPtr]::Zero)
"top-level windows: $($wins.Count)"
foreach ($h in $wins) {
  "  hwnd=$h class=[$(ClassOf $h)] visible=$([Win]::IsWindowVisible($h)) text=[$(TextOf $h)]"
  $kids = @()
  $cb2 = [Win+EnumProc]{
    param($c,$l)
    $script:kids += ,$c
    return $true
  }
  $script:kids = @()
  [void][Win]::EnumChildWindows($h, $cb2, [IntPtr]::Zero)
  foreach ($c in $script:kids) {
    if ((TextOf $c).Length -gt 0 -or (ClassOf $c) -match 'Edit|Button|Combo') {
      "      child id=$([Win]::GetDlgCtrlID($c)) class=[$(ClassOf $c)] text=[$(TextOf $c)]"
    }
  }
}
$p.Kill()
