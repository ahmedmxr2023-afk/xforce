#requires -version 5
<#
  Conversion regression matrix.

  For every XML project given, this:
    1. runs  pt2conv check            (pre-flight capability report)
    2. runs  pt2conv buildxml         (our conversion)
    3. has TriStation import the XML  (oracle / reference project)
    4. has TriStation build BOTH      (ours and the oracle's)
    5. records the error codes of each build

  The point is to measure "can the tool handle this XML" instead of guessing.
  TriStation (XMLImportExport.exe) is used only as a test oracle here; the
  product pipeline does not call it.

  Usage:
    .\Convert-Matrix.ps1 -Xml <file1.xml>,<file2.xml> [-OutDir <dir>] [-SkipOracle]
#>
param(
  [Parameter(Mandatory = $true)][string[]]$Xml,
  [string]$OutDir = (Join-Path $env:TEMP "msw-matrix"),
  [switch]$SkipOracle
)

$ErrorActionPreference = "Continue"
# Accept a comma/newline separated list in a single argument too.
$Xml = @($Xml | ForEach-Object { $_ -split '[,\r\n]+' } | Where-Object { $_.Trim().Length -gt 0 } | ForEach-Object { $_.Trim() })
$tool = "C:\XforceMsw\pt2-conv\src\Pt2Conv\bin\Release\net7.0\pt2conv.dll"
$ts   = Join-Path ${env:ProgramFiles(x86)} "Triconex\TriStation 1131 5.8.0\Programs\XMLImportExport.exe"
if (-not (Test-Path $tool)) { throw "pt2conv not built: $tool" }
$env:TS58PROGRAMSDIR = Split-Path $ts -Parent

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$results = @()

function Stop-TriStation {
  Get-Process | Where-Object { $_.ProcessName -match 'TS1131|XMLImportExport' } |
    ForEach-Object { try { $_.Kill() } catch { } }
  Start-Sleep -Milliseconds 700
}

function Invoke-Ts([string[]]$arguments, [string]$work) {
  $out = Join-Path $work "ts_out.txt"
  $err = Join-Path $work "ts_err.txt"
  $p = Start-Process -FilePath $ts -ArgumentList $arguments -WorkingDirectory $work -PassThru `
    -RedirectStandardOutput $out -RedirectStandardError $err
  if (-not $p.WaitForExit(900000)) { try { $p.Kill() } catch { }; return "timeout" }
  return $p.ExitCode
}

function Read-Codes([string]$logPath) {
  if (-not (Test-Path $logPath)) { return @() }
  $t = [IO.File]::ReadAllText($logPath)
  $msgs = [regex]::Matches($t, '<!\[CDATA\[([^\]]*)\]\]>') | ForEach-Object { $_.Groups[1].Value }
  # Two shapes: compiler errors ("Error detected :XX0000: ...") and load/validation errors
  # ("System :XX0000: ..."). Counting only the first hides PB/PB/PC-class failures.
  $errs = $msgs | Where-Object { $_ -match 'Error detected|ERROR DETECTED|System\s*:' }
  return $errs | ForEach-Object { [regex]::Matches($_, '\b[A-Z]{2}\d{4}\b') } |
    ForEach-Object { $_.Value } | Where-Object { $_ }
}

foreach ($xmlPath in $Xml) {
  if (-not (Test-Path $xmlPath)) { Write-Host "MISSING $xmlPath"; continue }
  $name = [IO.Path]::GetFileNameWithoutExtension($xmlPath)
  # Same file name can exist in several folders (PACK_Replicated, TEMPLATES_DI3_Replicated…),
  # so tag each run with a short hash of the full path.
  $tag = $name + "-" + ([BitConverter]::ToString(
    [Security.Cryptography.SHA1]::Create().ComputeHash([Text.Encoding]::UTF8.GetBytes($xmlPath.ToLowerInvariant()))
  ) -replace '-', '').Substring(0, 6)
  $work = Join-Path $OutDir $tag
  if (Test-Path $work) { Remove-Item $work -Recurse -Force }
  New-Item -ItemType Directory -Path $work | Out-Null
  Copy-Item $xmlPath (Join-Path $work "s.xml")
  $local = Join-Path $work "s.xml"

  Write-Host "=== $name ==="

  # 1. pre-flight
  $check = & dotnet $tool check $local 2>&1 | Out-String
  $convertible = $check -match 'Result: convertible'

  # 2. our conversion
  $ours = Join-Path $work "ours.pt2x"
  $build = & dotnet $tool buildxml $local $ours 2>&1 | Out-String
  $oursOk = Test-Path $ours

  $oursCodes = @(); $oracleCodes = @(); $oracleOk = $false

  if ($oursOk) {
    Stop-TriStation
    $cmd = Join-Path $work "c_ours.xml"
    @"
<?xml version="1.0" encoding="utf-8"?>
<XmlImportExportCommands xmlns="http://www.triconex.com/xmlimpexp/03/2008">
  <Command Name="Open"><Param Name="Pt2xFileName" Value="$ours" /></Command>
  <Command Name="Build"><Param Name="ReBuildAll" Value="True" /></Command>
</XmlImportExportCommands>
"@ | Set-Content -LiteralPath $cmd -Encoding UTF8
    Invoke-Ts @('-actioncmd', $cmd, '-suppresslog') $work | Out-Null
    $oursCodes = Read-Codes (Join-Path $work "c_ours_log.xml")
  }

  # 3. oracle: TriStation imports the XML into a fresh project, then builds it
  if (-not $SkipOracle) {
    Stop-TriStation
    Invoke-Ts @('-import', $local, (Join-Path $work "oracle.pt2x"), '-suppresslog') $work | Out-Null
    $oracle = Join-Path $work "oracle.pt2x"
    # import leaves <name>_log.xml next to the input
    $importCodes = Read-Codes (Join-Path $work "s_log.xml")
    if (Test-Path $oracle) {
      $oracleOk = $true
      Stop-TriStation
      $cmd2 = Join-Path $work "c_oracle.xml"
      @"
<?xml version="1.0" encoding="utf-8"?>
<XmlImportExportCommands xmlns="http://www.triconex.com/xmlimpexp/03/2008">
  <Command Name="Open"><Param Name="Pt2xFileName" Value="$oracle" /></Command>
  <Command Name="Build"><Param Name="ReBuildAll" Value="True" /></Command>
</XmlImportExportCommands>
"@ | Set-Content -LiteralPath $cmd2 -Encoding UTF8
      Invoke-Ts @('-actioncmd', $cmd2, '-suppresslog') $work | Out-Null
      $oracleCodes = Read-Codes (Join-Path $work "c_oracle_log.xml")
    }
  }

  $row = [pscustomobject]@{
    Project      = $tag
    Preflight    = if ($convertible) { "ok" } else { "blocked" }
    OursBuilt    = $oursOk
    OursErrors   = $oursCodes.Count
    OursCodes    = (($oursCodes | Group-Object | Sort-Object Count -Descending | Select-Object -First 4 | ForEach-Object { "$($_.Name)x$($_.Count)" }) -join ' ')
    OracleBuilt  = $oracleOk
    OracleErrors = $oracleCodes.Count
    OracleCodes  = (($oracleCodes | Group-Object | Sort-Object Count -Descending | Select-Object -First 4 | ForEach-Object { "$($_.Name)x$($_.Count)" }) -join ' ')
    WorkDir      = $work
  }
  $results += $row
  $row | Format-List | Out-String | Write-Host
}

$csv = Join-Path $OutDir "matrix.csv"
$results | Export-Csv -Path $csv -NoTypeInformation -Encoding UTF8
# Golden record: stable, machine-readable comparison of both builds per input.
$golden = $results | ForEach-Object {
  [pscustomobject]@{
    project = $_.Project
    preflight = $_.Preflight
    ours = @{ built = [bool]$_.OursBuilt; errors = [int]$_.OursErrors; codes = $_.OursCodes }
    oracle = @{ built = [bool]$_.OracleBuilt; errors = [int]$_.OracleErrors; codes = $_.OracleCodes }
  }
}
$golden | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutDir "golden.json") -Encoding UTF8
Write-Host ""
Write-Host "Matrix written to $csv"
$results | Format-Table Project, Preflight, OursBuilt, OursErrors, OursCodes, OracleErrors, OracleCodes -AutoSize | Out-String -Width 200 | Write-Host
