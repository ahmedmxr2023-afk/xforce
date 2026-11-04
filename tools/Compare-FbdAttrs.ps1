param([string]$Ref,[string]$Ours,[string]$Pou)
function Elements($p) {
  $x = New-Object System.Xml.XmlDocument
  $x.Load($p)
  $map = @{}
  foreach ($c in $x.SelectNodes('//Class[@ClassType]')) { if ($c.GetAttribute('ClassType') -match 'Comment|Divider') { continue }
    $ct = $c.GetAttribute('ClassType')
    $key = $ct
    foreach ($a in @('varName','typeName','instanceName')) {
      if ($c.HasAttribute($a)) { $key += '|' + $a + '=' + $c.GetAttribute($a) }
    }
    foreach ($a in @('posX','posY')) { if ($c.HasAttribute($a)) { $key += '|' + $a + '=' + $c.GetAttribute($a) } }
    # for snapshots / declarations avoid duplicate keys by appending index
    $i = 0; while ($map.ContainsKey($key)) { $i++; $key = $key -replace '\|#\d+$',''; $key += '|#' + $i }
    $map[$key] = $c
  }
  return $map
}
$a = Elements (Get-ChildItem -LiteralPath (Join-Path $Ref $Pou) -Filter '*.FBD' | Select-Object -First 1).FullName
$b = Elements (Get-ChildItem -LiteralPath (Join-Path $Ours $Pou) -Filter '*.FBD' | Select-Object -First 1).FullName
$skip = @('localId','refLocalId','connectId','outConnectId','inConnectId','dateTime','lastUniqueID')
$count = 0
foreach ($k in $a.Keys) {
  if (-not $b.ContainsKey($k)) { Write-Host "MISSING $k"; $count++; continue }
  $ca = $a[$k]; $cb = $b[$k]
  foreach ($attr in $ca.Attributes) {
    if ($skip -contains $attr.Name) { continue }
    $v1 = $attr.Value
    $v2 = if ($cb.HasAttribute($attr.Name)) { $cb.GetAttribute($attr.Name) } else { '<missing>' }
    if ($v1 -ne $v2) { Write-Host ("DIFF {0} @{1}: ref=[{2}] ours=[{3}]" -f $k, $attr.Name, $v1, $v2); $count++ }
  }
  foreach ($attr in $cb.Attributes) {
    if ($skip -contains $attr.Name) { continue }
    if (-not $ca.HasAttribute($attr.Name)) { Write-Host ("EXTRA {0} @{1}=[{2}]" -f $k, $attr.Name, $attr.Value); $count++ }
  }
  if ($count -gt 60) { break }
}
Write-Host "total reported: $count"
