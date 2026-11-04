param([Parameter(Mandatory=$true)][string]$Ref,[Parameter(Mandatory=$true)][string]$Ours)
function Get-Blocks([string]$path) {
  $text = [IO.File]::ReadAllText($path)
  $map = @{}
  foreach ($m in [regex]::Matches($text, '<Class ClassType="CFBD(Function|Block)Xml"[\s\S]*?</Class>')) {
    $v = $m.Value
    $ty = [regex]::Match($v, 'typeName="([^"]*)"').Groups[1].Value
    $inst = [regex]::Match($v, 'instanceName="([^"]*)"').Groups[1].Value
    $x = [regex]::Match($v, 'posX="(-?\d+)"').Groups[1].Value
    $y = [regex]::Match($v, 'posY="(-?\d+)"').Groups[1].Value
    $pins = New-Object System.Collections.Generic.List[string]
    foreach ($p in [regex]::Matches($v, '<Class (?:invertedPin="True" )?formalParam="([^"]*)"(?: effectiveParam="([^"]*)")?(?: connectId="(\d+)")?')) {
      $pins.Add($p.Groups[1].Value + '/' + $p.Groups[2].Value + '/' + ($(if ($p.Groups[3].Success) { 'c' } else { '-' })))
    }
    $key = "$ty|$inst|$x|$y"
    $map[$key] = ($pins -join ';')
  }
  return $map
}
foreach ($dir in (Get-ChildItem $Ref -Directory)) {
  $rf = Get-ChildItem -LiteralPath $dir.FullName -Filter '*.FBD' | Select-Object -First 1
  if (-not $rf) { continue }
  $od = Join-Path $Ours $dir.Name
  if (-not (Test-Path -LiteralPath $od)) { continue }
  $of = Get-ChildItem -LiteralPath $od -Filter '*.FBD' | Select-Object -First 1
  if (-not $of) { continue }
  $a = Get-Blocks $rf.FullName
  $b = Get-Blocks $of.FullName
  foreach ($k in $a.Keys) {
    if (-not $b.ContainsKey($k)) { Write-Host ("$($dir.Name) MISSING BLOCK $k"); continue }
    if ($a[$k] -ne $b[$k]) {
      Write-Host ("$($dir.Name) DIFF BLOCK $k")
      Write-Host ("  ref : " + $a[$k])
      Write-Host ("  ours: " + $b[$k])
    }
  }
  foreach ($k in $b.Keys) { if (-not $a.ContainsKey($k)) { Write-Host ("$($dir.Name) EXTRA BLOCK $k") } }
}
