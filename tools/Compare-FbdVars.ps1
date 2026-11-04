param([Parameter(Mandatory=$true)][string]$Ref,[Parameter(Mandatory=$true)][string]$Ours)
function Get-VarKeys([string]$path) {
  $text = [IO.File]::ReadAllText($path)
  $keys = New-Object System.Collections.Generic.List[string]
  foreach ($m in [regex]::Matches($text, '<Class ClassType="CFBD(InOut|Out)VarXml"[^>]*/>')) {
    $v = $m.Value
    $n = [regex]::Match($v, 'varName="([^"]*)"').Groups[1].Value
    $x = [regex]::Match($v, 'posX="(-?\d+)"').Groups[1].Value
    $y = [regex]::Match($v, 'posY="(-?\d+)"').Groups[1].Value
    $ty = [regex]::Match($v, 'varType="([^"]*)"').Groups[1].Value
    $in = [regex]::Match($v, 'inConnection="([^"]*)"').Groups[1].Value
    $out = [regex]::Match($v, 'outConnection="([^"]*)"').Groups[1].Value
    $keys.Add("$n|$x,$y|$ty|in=$in|out=$out")
  }
  return $keys
}
foreach ($dir in (Get-ChildItem $Ref -Directory)) {
  $rf = Get-ChildItem -LiteralPath $dir.FullName -Filter '*.FBD' | Select-Object -First 1
  if (-not $rf) { continue }
  $od = Join-Path $Ours $dir.Name
  if (-not (Test-Path -LiteralPath $od)) { Write-Host ("MISSING " + $dir.Name); continue }
  $of = Get-ChildItem -LiteralPath $od -Filter '*.FBD' | Select-Object -First 1
  if (-not $of) { continue }
  $a = Get-VarKeys $rf.FullName
  $b = Get-VarKeys $of.FullName
  $ca = @{}; foreach ($x in $a) { $ca[$x] = 1 + ($ca[$x] -as [int]) }
  $cb = @{}; foreach ($x in $b) { $cb[$x] = 1 + ($cb[$x] -as [int]) }
  $oa = @($ca.Keys | Where-Object { -not $cb.ContainsKey($_) })
  $ob = @($cb.Keys | Where-Object { -not $ca.ContainsKey($_) })
  if ($oa.Count -gt 0 -or $ob.Count -gt 0) {
    Write-Host ("=== {0}: ref={1} ours={2} refOnly={3} oursOnly={4}" -f $dir.Name, $a.Count, $b.Count, $oa.Count, $ob.Count)
    $oa | Select-Object -First 5 | ForEach-Object { Write-Host ("  REF  " + $_) }
    $ob | Select-Object -First 5 | ForEach-Object { Write-Host ("  OURS " + $_) }
  }
}
