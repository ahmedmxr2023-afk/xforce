#requires -version 5
<#
  Summarises a Convert-Matrix.ps1 run: groups failures by TriStation error code so
  each class can be fixed once. Also lists the first failing project per code.

  Usage:
    .\Summarize-Matrix.ps1 [-Csv <matrix.csv>]
#>
param(
  [string]$Csv = (Join-Path $env:TEMP "msw-matrix\matrix.csv")
)

if (-not (Test-Path $Csv)) { throw "Matrix file not found: $Csv" }

$rows = Import-Csv $Csv
$clean = $rows | Where-Object { $_.OursBuilt -eq 'True' -and [int]$_.OursErrors -eq 0 }
$failed = $rows | Where-Object { $_.OursBuilt -ne 'True' -or [int]$_.OursErrors -gt 0 }

Write-Host ("Projects: {0}   clean: {1}   failing: {2}" -f $rows.Count, $clean.Count, $failed.Count)
Write-Host ""

if ($clean.Count -gt 0) {
  Write-Host "Clean conversions:"
  $clean | ForEach-Object { Write-Host ("  OK   " + $_.Project) }
  Write-Host ""
}

if ($failed.Count -gt 0) {
  Write-Host "Failures by error code:"
  $codes = @{}
  foreach ($r in $failed) {
    if ($r.OursBuilt -ne 'True') {
      if (-not $codes.ContainsKey('CONVERSION')) { $codes['CONVERSION'] = @() }
      $codes['CONVERSION'] += $r.Project
      continue
    }
    foreach ($c in ($r.OursCodes -split '\s+' | Where-Object { $_ })) {
      $code = $c -replace 'x\d+$', ''
      if (-not $codes.ContainsKey($code)) { $codes[$code] = @() }
      $codes[$code] += $r.Project
    }
  }
  foreach ($k in ($codes.Keys | Sort-Object)) {
    $list = $codes[$k]
    Write-Host ("  {0,-10} {1} project(s)  e.g. {2}" -f $k, $list.Count, ($list | Select-Object -First 3) -join ', ')
  }
  Write-Host ""
  Write-Host "Per project:"
  $failed | ForEach-Object {
    Write-Host ("  FAIL {0,-32} errors={1}  {2}" -f $_.Project, $_.OursErrors, $_.OursCodes)
  }
}
