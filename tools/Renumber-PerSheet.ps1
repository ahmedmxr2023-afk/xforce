param([string]$Project,[string[]]$Names)
$ErrorActionPreference = 'Stop'
$Names = @($Names | ForEach-Object { $_ -split '[,\s]+' } | Where-Object { $_ })
foreach ($pou in $Names) {
  $dir = Join-Path $Project "Current\Elements\$pou"
  $f = Get-ChildItem -LiteralPath $dir -Filter '*.FBD' | Select-Object -First 1
  if (-not $f) { continue }
  $x = New-Object System.Xml.XmlDocument
  $x.PreserveWhitespace = $true
  $x.Load($f.FullName)
  $root = $x.DocumentElement
  $sheets = $x.SelectNodes('//Collection[@Name="sheets"]/Class')
  $map = @{}
  $counter = 1
  foreach ($sheet in $sheets) {
    $disp = $sheet.SelectSingleNode('./Collection[@Name="displayables"]')
    if (-not $disp) { continue }
    $nodes = @($disp.ChildNodes | Where-Object { $_.NodeType -eq 'Element' })
    $dividers = @($nodes | Where-Object { $_.GetAttribute('ClassType') -eq 'CDGMnetworkDividerXml' })
    $blocks = @($nodes | Where-Object { $_.GetAttribute('ClassType') -match 'CFBD(Function|Block)Xml' })
    $comments = @($nodes | Where-Object { $_.GetAttribute('ClassType') -eq 'CDGMCommentXml' })
    $vars = @($nodes | Where-Object { $_.GetAttribute('ClassType') -match 'CFBD(InOut|Out)VarXml' })
    $wires = @($nodes | Where-Object { $_.GetAttribute('ClassType') -eq 'CDGMWireXml' })
    foreach ($group in @($dividers, $blocks, $comments, $vars)) {
      foreach ($e in $group) {
        if (-not $e.HasAttribute('localId')) { continue }
        $old = [int]$e.GetAttribute('localId')
        $map[$old] = $counter
        $e.SetAttribute('localId', [string]$counter)
        $counter++
      }
    }
    foreach ($w in ($wires | Sort-Object { [int]$_.GetAttribute('localId') })) {
      $old = [int]$w.GetAttribute('localId')
      $seg = [Math]::Max(1, ([regex]::Matches($w.OuterXml, 'posX="').Count - 1))
      $new = $counter + $seg - 1
      $counter += $seg
      $map[$old] = $new
      $w.SetAttribute('localId', [string]$new)
    }
  }
  foreach ($attr in @('connectId','inConnectId','outConnectId','refLocalId')) {
    foreach ($n in $x.SelectNodes("//Class[@$attr]")) {
      $v = $n.GetAttribute($attr)
      if ($v -match '^\d+$') {
        $old = [int]$v
        if ($map.ContainsKey($old)) { $n.SetAttribute($attr, [string]$map[$old]) }
      }
    }
  }
  $root.SetAttribute('lastUniqueID', [string]($counter - 1))
  $s = New-Object System.Xml.XmlWriterSettings
  $s.Encoding = New-Object System.Text.UTF8Encoding($false)
  $wri = [System.Xml.XmlWriter]::Create($f.FullName, $s); $x.Save($wri); $wri.Close()
  Write-Host "renumbered $pou (last id $($counter - 1))"
}
