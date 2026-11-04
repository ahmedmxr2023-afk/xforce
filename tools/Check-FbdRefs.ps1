param([Parameter(Mandatory=$true)][string]$Dir)
$bad = 0
foreach ($f in (Get-ChildItem $Dir -Recurse -Filter '*.FBD')) {
  $t = [IO.File]::ReadAllText($f.FullName)
  $ids = New-Object System.Collections.Generic.HashSet[string]
  foreach ($m in [regex]::Matches($t, 'localId="(\d+)"')) { [void]$ids.Add($m.Groups[1].Value) }
  $issues = New-Object System.Collections.Generic.List[string]
  foreach ($m in [regex]::Matches($t, '(?<![\w])(inConnectId|outConnectId|connectId)="(\d+)"')) {
    $kind = $m.Groups[1].Value; $id = $m.Groups[2].Value
    if (-not $ids.Contains($id)) { $issues.Add("$kind=$id missing") }
  }
  # wires: input/output connectId, and ensure localId unique
  $wireIds = @{}
  foreach ($m in [regex]::Matches($t, '<Class ClassType="CDGMWireXml" localId="(\d+)"')) {
    $id=$m.Groups[1].Value
    if ($wireIds.ContainsKey($id)) { $issues.Add("duplicate wire id $id") } else { $wireIds[$id]=1 }
  }
  if ($issues.Count -gt 0) {
    Write-Host ("=== " + $f.Directory.Name + " (" + $issues.Count + ")")
    $issues | Select-Object -First 10 | ForEach-Object { Write-Host ("  " + $_) }
    $bad++
  }
}
Write-Host ("POUs with dangling references: " + $bad)
