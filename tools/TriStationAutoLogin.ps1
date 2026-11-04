#requires -version 5
<#
  TriStation 1131 auto-login helper.

  TriStation's project security dialog (CSecLoginDlg - "Name / Password / Remember")
  otherwise has to be filled by hand every time a project is opened. This script keeps
  the credentials locally, encrypted with Windows DPAPI, and fills+dismisses the dialog
  automatically before it becomes visible.

  First use (type the credentials once, hidden input):

      .\tools\TriStationAutoLogin.ps1 -Save

  Then either keep a watcher running:

      .\tools\TriStationAutoLogin.ps1 -Watch

  or start TriStation and auto-login in one step:

      .\tools\TriStationAutoLogin.ps1 -Launch "C:\path\project.pt2x"

  Options:
      -Save           prompt for user name + password and store them (DPAPI, current user)
      -Clear          delete the stored credentials
      -Watch          watch for the login dialog until Ctrl+C
      -Launch <pt2x>  start TS1131 (optionally with a project) and watch until it exits
      -TimeoutMinutes how long -Launch waits before exiting the watcher (default 0 = no limit)
#>
param(
  [switch]$Save,
  [switch]$Clear,
  [switch]$Watch,
  [switch]$Install,
  [string]$Launch,
  [int]$TimeoutMinutes = 0
)

$ErrorActionPreference = 'Stop'
$store = Join-Path $env:LOCALAPPDATA 'XforceMsw'
$credFile = Join-Path $store 'tristation-login.txt'
$userFile = Join-Path $store 'tristation-user.txt'
$logFile = Join-Path $store 'tristation-autologin.log'
$tsExe = Join-Path ${env:ProgramFiles(x86)} 'Triconex\TriStation 1131 5.8.0\Programs\TS1131.exe'

New-Item -ItemType Directory -Force -Path $store | Out-Null

function Write-Log([string]$msg) {
  $line = "{0:yyyy-MM-dd HH:mm:ss}  {1}" -f (Get-Date), $msg
  Add-Content -LiteralPath $logFile -Value $line
  Write-Host $line
}

if ($Clear) {
  Remove-Item -LiteralPath $credFile, $userFile -Force -ErrorAction SilentlyContinue
  Write-Log "Stored TriStation credentials deleted."
  return
}

if ($Install) {
  $self = $MyInvocation.MyCommand.Path
  $taskName = 'XforceMsw-TriStationAutoLogin'
  $existing = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
  if ($existing) { Unregister-ScheduledTask -TaskName $taskName -Confirm:$false }
  $taskAction = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$self`" -Watch"
  $trigger = New-ScheduledTaskTrigger -AtLogOn -User ([Security.Principal.WindowsIdentity]::GetCurrent().Name)
  $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero)
  Register-ScheduledTask -TaskName $taskName -Action $taskAction -Trigger $trigger -Settings $settings -Description 'Fills the TriStation 1131 login dialog automatically.' -Force | Out-Null
  Start-ScheduledTask -TaskName $taskName
  Write-Log "Auto-login watcher installed and started (task '$taskName')."
  if (-not (Test-Path $credFile)) {
    Write-Host "No credentials stored yet: run  .\tools\TriStationAutoLogin.ps1 -Save  once."
  }
  return
}

if ($Save) {
  while ($true) {
    $user = Read-Host "TriStation user name"
    if ($user.Trim().Length -gt 0) { break }
    Write-Host "User name cannot be empty."
  }
  $secure = Read-Host "TriStation password" -AsSecureString
  $encrypted = ConvertFrom-SecureString -SecureString $secure
  Set-Content -LiteralPath $credFile -Value $encrypted -Encoding UTF8
  Set-Content -LiteralPath $userFile -Value $user -Encoding UTF8
  # Ask TriStation itself to remember the login as well.
  $key = 'HKCU:\Software\Triconex\TriStation 1131\5.8.0\States\Auto Login'
  if (Test-Path $key) { Set-ItemProperty -Path $key -Name 'Remember' -Value 1 -Type DWord }
  Write-Log "Credentials stored for '$user' (DPAPI, current Windows user)."
  # Restart a running watcher so it picks up the new credentials.
  $taskName = 'XforceMsw-TriStationAutoLogin'
  if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) {
    Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
    Start-ScheduledTask -TaskName $taskName
    Write-Log "Auto-login watcher restarted with the stored credentials."
  }
  if (-not $Watch -and -not $Launch) { return }
}

if (-not (Test-Path $credFile) -or -not (Test-Path $userFile)) {
  if (-not $Watch -and -not $Launch) {
    Write-Host "No stored credentials yet. Run:  .\tools\TriStationAutoLogin.ps1 -Save"
    return
  }
  Write-Log "No stored credentials; the watcher will leave the login dialog to you."
}

$userName = if (Test-Path $userFile) { (Get-Content -LiteralPath $userFile -Raw).Trim() } else { '' }
$password = if (Test-Path $credFile) {
  $sec = ConvertTo-SecureString (Get-Content -LiteralPath $credFile -Raw).Trim()
  $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($sec)
  try { [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) }
  finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
} else { '' }

if ($userName.Length -gt 0) { Write-Log "Loaded stored credentials for '$userName' ($($password.Length) characters)." }

if (-not ('TsAutoLogin.Win32' -as [type])) {
  Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
namespace TsAutoLogin {
  public static class Win32 {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr p, EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern int GetDlgCtrlID(IntPtr h);
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, string l);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern IntPtr GetDlgItem(IntPtr h, int id);
    [DllImport("user32.dll")] public static extern IntPtr SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  }
}
"@
}

function Get-WindowText([IntPtr]$h) {
  $sb = New-Object Text.StringBuilder 512
  [void][TsAutoLogin.Win32]::GetWindowText($h, $sb, 512)
  $sb.ToString()
}
function Get-WindowClass([IntPtr]$h) {
  $sb = New-Object Text.StringBuilder 256
  [void][TsAutoLogin.Win32]::GetClassName($h, $sb, 256)
  $sb.ToString()
}
function Get-Children([IntPtr]$h) {
  $list = New-Object System.Collections.ArrayList
  $cb = [TsAutoLogin.Win32+EnumProc]{
    param($c, $l)
    [void]$list.Add($c)
    return $true
  }
  [void][TsAutoLogin.Win32]::EnumChildWindows($h, $cb, [IntPtr]::Zero)
  return $list
}

function Test-TriStationProcess([uint32]$targetPid) {
  $p = Get-Process -Id $targetPid -ErrorAction SilentlyContinue
  if (-not $p) { return $false }
  return ($p.ProcessName -match '^(TS1131|XMLImportExport)$')
}

# Fill and close the security dialog. Returns $true when a dialog was handled.
function Invoke-LoginDialog([IntPtr]$hwnd) {
  $children = Get-Children $hwnd
  $edits = @()
  $buttons = @()
  $statics = @()
  foreach ($c in $children) {
    $cls = Get-WindowClass $c
    if ($cls -match 'Edit') { $edits += $c }
    elseif ($cls -match 'Button') { $buttons += ,@($c, (Get-WindowText $c)) }
    elseif ($cls -match 'Static|Label') { $statics += (Get-WindowText $c) }
  }
  if ($edits.Count -eq 0) { return $false }

  # Prefer UI Automation: it sets values and invokes buttons reliably across processes.
  try {
    Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes -ErrorAction SilentlyContinue
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
    if ($root) {
      $editCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Edit)
      $editsUia = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $editCond)
      $pwEdit = $null; $nameEditUia = $null
      foreach ($e in $editsUia) {
        $isPw = $false
        try { $isPw = $e.Current.IsPassword } catch { }
        if ($isPw -and -not $pwEdit) { $pwEdit = $e }
        elseif (-not $isPw -and -not $nameEditUia) { $nameEditUia = $e }
      }
      if (-not $nameEditUia -and $editsUia.Count -gt 0) { $nameEditUia = $editsUia[0] }
      $setValue = {
        param($el, $value)
        if (-not $el -or $value.Length -eq 0) { return }
        try {
          $vp = $el.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
          $vp.SetValue($value)
        } catch { }
      }
      & $setValue $nameEditUia $userName
      & $setValue $pwEdit $password

      # Hide it only after the fields are filled: move it off-screen (keep it "visible" so
      # both UI Automation and the dialog's own message loop keep working).
      if ($env:TSAUTOLOGIN_SHOW -ne '1') {
        [void][TsAutoLogin.Win32]::SetWindowPos($hwnd, [IntPtr]::Zero, -30000, -30000, 0, 0, 0x0001 -bor 0x0004 -bor 0x0010)
      }

      $btnCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button)
      $buttonsUia = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $btnCond)
      $target = $null; $remember = $null
      foreach ($b in $buttonsUia) {
        $n = ($b.Current.Name -replace '&', '').Trim()
        if ($n -match '^(Remember)') { $remember = $b }
        if ($n -match '^(OK|Login|Log On|Logon)$') { $target = $b; break }
      }
      if ($remember) { try { $remember.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle() } catch { } }
      if ($target) {
        try {
          $target.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
          Write-Log "Filled and dismissed the TriStation login dialog."
          return $true
        } catch { }
      }
    }
  } catch { }

  if ($userName.Length -gt 0 -or $password.Length -gt 0) {
    $passwordEdit = $null
    $nameEdit = $null
    foreach ($e in $edits) {
      $style = [TsAutoLogin.Win32]::GetWindowLong($e, -16) # GWL_STYLE
      if (($style -band 0x0020) -ne 0) { if (-not $passwordEdit) { $passwordEdit = $e } }
      elseif (-not $nameEdit) { $nameEdit = $e }
    }
    if (-not $nameEdit) { $nameEdit = $edits[0] }
    if ($nameEdit -and $userName.Length -gt 0) {
      [void][TsAutoLogin.Win32]::SendMessage($nameEdit, 0x000C, [IntPtr]::Zero, $userName) # WM_SETTEXT
    }
    if ($passwordEdit -and $password.Length -gt 0) {
      [void][TsAutoLogin.Win32]::SendMessage($passwordEdit, 0x000C, [IntPtr]::Zero, $password)
    }
  }

  # Click "Remember" if present, so TriStation also stores the session.
  foreach ($b in $buttons) {
    $text = ($b[1] -replace '&', '')
    if ($text -match '^Remember') { [void][TsAutoLogin.Win32]::SendMessage($b[0], 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero) }
  }

  $ok = [TsAutoLogin.Win32]::GetDlgItem($hwnd, 1)
  if ($ok -eq [IntPtr]::Zero) {
    foreach ($b in $buttons) {
      if ((($b[1] -replace '&', '') -match '^(OK|Login|Log On|Logon)$')) { $ok = $b[0]; break }
    }
  }
  if ($ok -ne [IntPtr]::Zero) {
    [void][TsAutoLogin.Win32]::SendMessage($ok, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero) # BM_CLICK
    Write-Log "Filled and dismissed the TriStation login dialog."
    return $true
  }

  # No OK button: restore the window so the user can finish manually.
  [void][TsAutoLogin.Win32]::SetWindowPos($hwnd, [IntPtr]::Zero, 200, 200, 0, 0, 0x0001 -bor 0x0004)
  return $false
}

function Find-AndHandleLogin {
  $handled = $false
  $cb = [TsAutoLogin.Win32+EnumProc]{
    param($h, $l)
    $cls = Get-WindowClass $h
    $pid2 = 0
    [void][TsAutoLogin.Win32]::GetWindowThreadProcessId($h, [ref]$pid2)
    if (-not (Test-TriStationProcess $pid2)) { return $true }
    $title = Get-WindowText $h
    $kids = Get-Children $h
    $hasPassword = $false
    $hasUser = $false
    foreach ($c in $kids) {
      $cc = Get-WindowClass $c
      if ($cc -match 'Static|Label') {
        $t = Get-WindowText $c
        if ($t -match 'Password|Passwd') { $hasPassword = $true }
        if ($t -match 'Name|User|Login|Logon') { $hasUser = $true }
      }
    }
    if ($hasPassword -and ($hasUser -or $title -match 'Login|Logon|Security|Sign')) {
      Write-Log ("Detected login dialog '$title'.")
      $script:handled = (Invoke-LoginDialog $h) -or $script:handled
    }
    return $true
  }
  [void][TsAutoLogin.Win32]::EnumWindows($cb, [IntPtr]::Zero)
  return $script:handled
}

$script:handled = $false
$proc = $null
if ($Launch) {
  if (-not (Test-Path $tsExe)) { throw "TS1131.exe not found: $tsExe" }
  $tsArgs = @()
  if ($Launch.Trim().Length -gt 0 -and (Test-Path -LiteralPath $Launch)) { $tsArgs += ('"' + (Resolve-Path -LiteralPath $Launch).Path + '"') }
  $proc = Start-Process -FilePath $tsExe -ArgumentList $tsArgs -PassThru
  Write-Log "Started TS1131 (pid $($proc.Id))."
}

if (-not $Watch -and -not $Launch) {
  Write-Host "Nothing to do. Use -Save, -Watch or -Launch <pt2x>."
  return
}

Write-Log "Watching for the TriStation login dialog..."
$deadline = if ($TimeoutMinutes -gt 0) { (Get-Date).AddMinutes($TimeoutMinutes) } else { [DateTime]::MaxValue }
while ((Get-Date) -lt $deadline) {
  $tsRunning = $false
  try {
    # Poll cheaply: only inspect windows when a TriStation process is running.
    if (Get-Process -Name TS1131, XMLImportExport -ErrorAction SilentlyContinue) {
      $tsRunning = $true
      [void](Find-AndHandleLogin)
    }
  } catch { Write-Log ("warn: " + $_.Exception.Message) }
  if ($proc) {
    $proc.Refresh()
    if ($proc.HasExited) { Write-Log "TS1131 exited."; break }
  }
  Start-Sleep -Milliseconds $(if ($tsRunning) { 80 } else { 1000 })
}
Write-Log "Watcher stopped."
