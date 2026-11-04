param(
  [Parameter(Mandatory=$true)][string]$Ref,
  [Parameter(Mandatory=$true)][string]$Ours
)

function Get-WireKeys([string]$path) {
  $text = [IO.File]::ReadAllText($path)
  $keys = New-Object System.Collections.Generic.List[string]
  foreach ($m in [regex]::Matches($text, '<Class ClassType="CDGMWireXml"[\s\S]*?</Class>')) {
    $v = $m.Value
    $in  = [regex]::Match($v, 'Embed="input" formalParam="([^"]*)" effectiveParam="([^"]*)"')
    $out = [regex]::Match($v, 'Embed="output" formalParam="([^"]*)" effectiveParam="([^"]*)"')
    $pts = New-Object System.Collections.Generic.List[string]
    foreach ($p in [regex]::Matches($v, 'posX="(-?\d+)" posY="(-?\d+)"')) {
      $pts.Add($p.Groups[1].Value + ',' + $p.Groups[2].Value)
    }
    $keys.Add("IN[$($in.Groups[1].Value)/$($in.Groups[2].Value)] OUT[$($out.Groups[1].Value)/$($out.Groups[2].Value)] @" + ($pts -join ';'))
  }
  return $keys
}

$refDirs = Get-ChildItem $Ref -Directory
foreach ($dir in $refDirs) {
  $rf = Get-ChildItem -LiteralPath $dir.FullName -Filter '*.FBD' | Select-Object -First 1
  if (-not $rf) { continue }
  $oursDir = Join-Path $Ours $dir.Name
  if (-not (Test-Path -LiteralPath $oursDir)) { Write-Host ("MISSING POU " + $dir.Name); continue }
  $of = Get-ChildItem -LiteralPath $oursDir -Filter '*.FBD' | Select-Object -First 1
  if (-not $of) { Write-Host ("NO FBD " + $dir.Name); continue }

  $a = Get-WireKeys $rf.FullName
  $b = Get-WireKeys $of.FullName
  $ca = @{}
  foreach ($x in $a) { $ca[$x] = 1 + ($ca[$x] -as [int]) }
  $cb = @{}
  foreach ($x in $b) { $cb[$x] = 1 + ($cb[$x] -as [int]) }
  $onlyRef = @($ca.Keys | Where-Object { -not $cb.ContainsKey($_) })
  $onlyOurs = @($cb.Keys | Where-Object { -not $ca.ContainsKey($_) })
  if ($onlyRef.Count -gt 0 -or $onlyOurs.Count -gt 0) {
    Write-Host ("=== {0}: ref={1} ours={2} refOnly={3} oursOnly={4}" -f $dir.Name, $a.Count, $b.Count, $onlyRef.Count, $onlyOurs.Count)
    $onlyRef | Select-Object -First 4 | ForEach-Object { Write-Host ("  REF  " + $_) }
    $onlyOurs | Select-Object -First 4 | ForEach-Object { Write-Host ("  OURS " + $_) }
  }
}
