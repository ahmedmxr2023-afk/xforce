param([Parameter(Mandatory=$true)][string]$Ref,[Parameter(Mandatory=$true)][string]$Ours)
function Get-Snaps([string]$path) {
  $text = [IO.File]::ReadAllText($path)
  $i = $text.IndexOf('<Collection Name="snapshots"')
  if ($i -lt 0) { return @{} }
  $e = $text.IndexOf('</Collection>', $text.IndexOf('</Collection>', $i) + 1)
  # crude: take the whole snapshots collection block by tag depth
  $map = @{}
  foreach ($m in [regex]::Matches($text.Substring($i), '<Class name="([^"]*)" dogTag="(\d+)"[^>]*>[\s\S]*?<Collection Name="declarations"[^>]*>([\s\S]*?)</Collection>')) {
    $name = $m.Groups[1].Value
    $decls = [regex]::Matches($m.Groups[3].Value, '<Class name="([^"]*)"[^>]*/>') | ForEach-Object { $_.Value -replace ' dogTag="[^"]*"','' }
    $map[$name] = ($decls -join "`n")
  }
  return $map
}
foreach ($dir in (Get-ChildItem $Ref -Directory)) {
  $rf = Get-ChildItem -LiteralPath $dir.FullName -Filter '*.FBD' | Select-Object -First 1
  if (-not $rf) { continue }
  $of = Get-ChildItem -LiteralPath (Join-Path $Ours $dir.Name) -Filter '*.FBD' -ErrorAction SilentlyContinue | Select-Object -First 1
  if (-not $of) { continue }
  $a = Get-Snaps $rf.FullName
  $b = Get-Snaps $of.FullName
  foreach ($k in $a.Keys) {
    if (-not $b.ContainsKey($k)) { Write-Host ("$($dir.Name): snapshot $k missing"); continue }
    if ($a[$k] -ne $b[$k]) {
      Write-Host ("$($dir.Name): snapshot $k differs")
      $la = $a[$k] -split "`n"; $lb = $b[$k] -split "`n"
      for ($i = 0; $i -lt [Math]::Min($la.Count, $lb.Count); $i++) {
        if ($la[$i] -ne $lb[$i]) { Write-Host ("  REF  " + $la[$i]); Write-Host ("  OURS " + $lb[$i]); break }
      }
      if ($la.Count -ne $lb.Count) { Write-Host ("  count ref=$($la.Count) ours=$($lb.Count)") }
    }
  }
}
