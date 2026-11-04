param([Parameter(Mandatory=$true)][string]$Ref,[Parameter(Mandatory=$true)][string]$Ours)
function Get-Decls([string]$path) {
  $text = [IO.File]::ReadAllText($path)
  $i = $text.IndexOf('<Collection Name="declarations"')
  if ($i -lt 0) { return @() }
  $e = $text.IndexOf('</Collection>', $i)
  $seg = $text.Substring($i, $e - $i)
  return [regex]::Matches($seg, '<Class name="([^"]*)"[^>]*/>') | ForEach-Object { $_.Value -replace 'localId="[^"]*"','' }
}
foreach ($dir in (Get-ChildItem $Ref -Directory)) {
  $rf = Get-ChildItem -LiteralPath $dir.FullName -Filter '*.FBD' | Select-Object -First 1
  if (-not $rf) { continue }
  $of = Get-ChildItem -LiteralPath (Join-Path $Ours $dir.Name) -Filter '*.FBD' -ErrorAction SilentlyContinue | Select-Object -First 1
  if (-not $of) { continue }
  $a = Get-Decls $rf.FullName
  $b = Get-Decls $of.FullName
  $ca = @{}; foreach ($x in $a) { $ca[$x] = 1 + ($ca[$x] -as [int]) }
  $cb = @{}; foreach ($x in $b) { $cb[$x] = 1 + ($cb[$x] -as [int]) }
  $oa = @($ca.Keys | Where-Object { -not $cb.ContainsKey($_) })
  $ob = @($cb.Keys | Where-Object { -not $ca.ContainsKey($_) })
  if ($oa.Count -or $ob.Count) {
    Write-Host ("=== $($dir.Name): ref=$($a.Count) ours=$($b.Count) refOnly=$($oa.Count) oursOnly=$($ob.Count)")
    $oa | Select-Object -First 3 | ForEach-Object { Write-Host ("  REF  " + $_) }
    $ob | Select-Object -First 3 | ForEach-Object { Write-Host ("  OURS " + $_) }
  }
}
