param(
  [Parameter(Mandatory=$true)][string]$Project,
  [Parameter(Mandatory=$true)][string]$OracleElements,
  [Parameter(Mandatory=$true)][string[]]$Names
)
$ErrorActionPreference = 'Stop'
$Names = @($Names | ForEach-Object { $_ -split '[,\s]+' } | Where-Object { $_.Length -gt 0 })
function Load-Xml([string]$p) { $d = New-Object System.Xml.XmlDocument; $d.PreserveWhitespace = $true; $d.Load($p); $d }
function Save-Xml($doc, [string]$p) {
  $s = New-Object System.Xml.XmlWriterSettings
  $s.Encoding = New-Object System.Text.UTF8Encoding($false)
  $w = [System.Xml.XmlWriter]::Create($p, $s); $doc.Save($w); $w.Close()
}
$cat = Load-Xml (Join-Path $Project 'Current\Project.xml')
$map = @{}
foreach ($c in $cat.SelectNodes('//Collection[@Name="Elements"]/Class')) {
  $n = $c.GetAttribute('ElemName'); if (-not $n) { continue }
  $ev = $c.SelectSingleNode('.//Class[@Embed="EventConnectionsChanged"]')
  $map[$n] = @{
    dog = $c.GetAttribute('DogTag')
    id = if ($ev) { $ev.GetAttribute('eventId') } else { '0' }
    date = if ($ev) { $ev.GetAttribute('date') } else { '1970-01-01 00:00:00' }
    user = if ($ev) { $ev.GetAttribute('user') } else { '0' }
  }
}
foreach ($name in $Names) {
  $oursDir = Join-Path $Project "Current\Elements\$name"
  $oracleDir = Join-Path $OracleElements $name
  if (-not (Test-Path $oursDir) -or -not (Test-Path $oracleDir)) { Write-Host "skip $name"; continue }
  $oursF = Get-ChildItem -LiteralPath $oursDir -Filter '*.FBD' | Select-Object -First 1
  $oracleF = Get-ChildItem -LiteralPath $oracleDir -Filter '*.FBD' | Select-Object -First 1
  if (-not $oursF -or -not $oracleF) { continue }
  Copy-Item $oracleF.FullName $oursF.FullName -Force
  $doc = Load-Xml $oursF.FullName
  $snap = $doc.SelectSingleNode('//Collection[@Name="snapshots"]')
  if ($snap) {
    foreach ($cls in $snap.SelectNodes('./Class')) {
      $cn = $cls.GetAttribute('name')
      if ($map.ContainsKey($cn)) {
        $cls.SetAttribute('dogTag', $map[$cn].dog)
        $ev = $cls.SelectSingleNode('./Class[@Embed="eventConnectionsChanged"]')
        if ($ev) { $ev.SetAttribute('eventId', $map[$cn].id); $ev.SetAttribute('date', $map[$cn].date); $ev.SetAttribute('user', $map[$cn].user) }
      }
    }
  }
  foreach ($b in $doc.SelectNodes('//Class[@ClassType="CFBDFBlockXml"]')) {
    $tn = $b.GetAttribute('typeName')
    if ($map.ContainsKey($tn)) { $b.SetAttribute('snapEventId', $map[$tn].id) }
  }
  Save-Xml $doc $oursF.FullName
  Write-Host "swapped $name"
}
