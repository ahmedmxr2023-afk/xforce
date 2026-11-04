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
  $map = @{}
  foreach ($disp in $x.SelectNodes('//Collection[@Name="displayables"]')) {
    $nodes = @($disp.ChildNodes | Where-Object { $_.NodeType -eq 'Element' })
    $elems = @($nodes | Where-Object { $_.GetAttribute('ClassType') -ne 'CDGMWireXml' -and $_.HasAttribute('localId') })
    $max = 0
    foreach ($e in $elems) { $v = [int]$e.GetAttribute('localId'); if ($v -gt $max) { $max = $v } }
    $wires = @($nodes | Where-Object { $_.GetAttribute('ClassType') -eq 'CDGMWireXml' } | Sort-Object { [int]$_.GetAttribute('localId') })
    $next = $max + 1
    foreach ($w in $wires) {
      $old = [int]$w.GetAttribute('localId')
      $seg = [Math]::Max(1, ([regex]::Matches($w.OuterXml, 'posX="').Count - 1))
      $new = $next + $seg - 1
      $next += $seg
      $map[$old] = $new
      $w.SetAttribute('localId', [string]$new)
    }
  }
  if ($map.Count -eq 0) { continue }
  # Update every connectId reference that points at a wire id.
  foreach ($attr in @('connectId','inConnectId','outConnectId','refLocalId')) {
    foreach ($n in $x.SelectNodes("//Class[@$attr]")) {
      $v = $n.GetAttribute($attr)
      if ($v -match '^\d+$') {
        $old = [int]$v
        if ($map.ContainsKey($old)) { $n.SetAttribute($attr, [string]$map[$old]) }
      }
    }
  }
  $s = New-Object System.Xml.XmlWriterSettings
  $s.Encoding = New-Object System.Text.UTF8Encoding($false)
  $wri = [System.Xml.XmlWriter]::Create($f.FullName, $s); $x.Save($wri); $wri.Close()
  Write-Host "renumbered wires in $pou ($($map.Count))"
}
