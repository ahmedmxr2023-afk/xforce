#requires -version 5
<#
  Validates a TriStation .pt2/.pt2x project the way the editor does: opens it with
  TriStation's own engine, runs a full build and prints every error (WF0018, WF0050,
  TN0141, ...).

  Implementation note: the TS2API COM coclass cannot be activated from PowerShell on
  this host (CO_E_DLLNOTFOUND / 0x8007045A), so the check drives the supported
  XMLImportExport.exe action-command interface instead, which uses the same engine.

  Important: TS1131.exe must NOT be running. A running GUI takes the session and the
  engine then answers every open with WB0173 "Invalid logon!".

  Usage:
    .\Verify-TriStationPt2x.ps1 -Project <file.pt2x> [-KeepCopy] [-Version 5.8.0]

  Exit code 0 = clean build, 1 = errors, 2 = could not run the check.
#>
param(
  [Parameter(Mandatory = $true)][string]$Project,
  [string]$Version = "5.8.0",
  [switch]$KeepCopy
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $Project)) { throw "Project not found: $Project" }

$root = Join-Path ${env:ProgramFiles(x86)} "Triconex\TriStation 1131 $Version"
$programs = Join-Path $root "Programs"
$tool = Join-Path $programs "XMLImportExport.exe"
if (-not (Test-Path -LiteralPath $tool)) { throw "TriStation XML Import/Export not found: $tool" }

$gui = Get-Process -Name TS1131 -ErrorAction SilentlyContinue
if ($gui) {
  throw "TS1131.exe is running (pid $($gui.Id -join ',')). Close it first: a running GUI makes every open fail with WB0173 'Invalid logon'."
}

$work = Join-Path ([IO.Path]::GetTempPath()) ("tscheck_" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $work | Out-Null
$copy = Join-Path $work "check.pt2x"
Copy-Item -LiteralPath $Project -Destination $copy

$commands = Join-Path $work "commands.xml"
$log = Join-Path $work "commands_log.xml"
$projectXml = [Security.SecurityElement]::Escape($copy)

@"
<?xml version="1.0" encoding="utf-8"?>
<XmlImportExportCommands xmlns="http://www.triconex.com/xmlimpexp/03/2008">
  <Command Name="Open"><Param Name="Pt2xFileName" Value="$projectXml" /></Command>
  <Command Name="Build"><Param Name="ReBuildAll" Value="True" /></Command>
  <Command Name="Save" />
  <Command Name="Close" />
</XmlImportExportCommands>
"@ | Set-Content -LiteralPath $commands -Encoding UTF8

$env:TS58PROGRAMSDIR = $programs
Push-Location $work
try {
  $process = Start-Process -FilePath $tool -ArgumentList @('-actioncmd', $commands) `
    -WorkingDirectory $work -PassThru -Wait `
    -RedirectStandardOutput (Join-Path $work 'out.txt') -RedirectStandardError (Join-Path $work 'err.txt')
}
finally { Pop-Location }

if (-not (Test-Path -LiteralPath $log)) {
  Write-Host "No result log produced (exit code $($process.ExitCode))."
  exit 2
}

$text = [IO.File]::ReadAllText($log)
foreach ($m in [regex]::Matches($text, '<CommandRunResult Command="([^"]+)" Result="([^"]+)"')) {
  Write-Host ("  {0,-8} {1}" -f $m.Groups[1].Value, $m.Groups[2].Value)
}

$errors = [regex]::Matches($text, '<!\[CDATA\[([^\]]*)\]\]>') |
  ForEach-Object { $_.Groups[1].Value } |
  Where-Object { $_ -match '^\s*Error detected' }

Write-Host "Errors reported: $($errors.Count)"
foreach ($e in $errors) { Write-Host ("  " + $e.Trim()) }

$codes = $errors | ForEach-Object { [regex]::Match($_, '\b(TN|WF|WB|WP)\d{4}').Value } | Where-Object { $_ }
if ($codes) {
  Write-Host "By code:"
  $codes | Group-Object | Sort-Object Count -Descending | ForEach-Object { Write-Host ("  {0} x{1}" -f $_.Name, $_.Count) }
}

if (-not $KeepCopy) { Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue }

if ($errors.Count -eq 0) { Write-Host "CLEAN BUILD"; exit 0 }
Write-Host "BUILD HAS ERRORS"; exit 1
