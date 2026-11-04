param(
  [Parameter(Mandatory=$true)][string]$Project,
  [Parameter(Mandatory=$true)][string]$OracleElements
)
$ErrorActionPreference = 'Stop'

function Load-Xml([string]$p) {
  $d = New-Object System.Xml.XmlDocument
  $d.PreserveWhitespace = $true
  $d.Load($p)
  return $d
}
function Save-Xml($doc, [string]$p) {
  $s = New-Object System.Xml.XmlWriterSettings
  $s.Encoding = New-Object System.Text.UTF8Encoding($false)
  $s.Indent = $false
  $w = [System.Xml.XmlWriter]::Create($p, $s)
  $doc.Save($w)
  $w.Close()
}

$cat = Load-Xml (Join-Path $Project 'Current\Project.xml')
$map = @{}
foreach ($c in $cat.SelectNodes('//Collection[@Name="Elements"]/Class')) {
  $n = $c.GetAttribute('ElemName')
  if (-not $n) { continue }
  $ev = $c.SelectSingleNode('.//Class[@Embed="EventConnectionsChanged"]')
  $map[$n] = @{
    dog  = $c.GetAttribute('DogTag')
    id   = if ($ev) { $ev.GetAttribute('eventId') } else { '0' }
    date = if ($ev) { $ev.GetAttribute('date') } else { '1970-01-01 00:00:00' }
    user = if ($ev) { $ev.GetAttribute('user') } else { '0' }
  }
}

$count = 0
foreach ($name in $map.Keys) {
  $oursDir = Join-Path $Project "Current\Elements\$name"
  if (-not (Test-Path $oursDir)) { continue }
  $oursFile = Get-ChildItem -LiteralPath $oursDir -Filter '*.FBD' | Select-Object -First 1
  if (-not $oursFile) { continue }
  $oracleFile = Get-ChildItem -LiteralPath (Join-Path $OracleElements $name) -Filter '*.FBD' -ErrorAction SilentlyContinue | Select-Object -First 1
  if (-not $oracleFile) { continue }

  $ours = Load-Xml $oursFile.FullName
  $oracle = Load-Xml $oracleFile.FullName
  $osnap = $ours.SelectSingleNode('//Collection[@Name="snapshots"]')
  $rsnap = $oracle.SelectSingleNode('//Collection[@Name="snapshots"]')
  if (-not $osnap -or -not $rsnap) { continue }

  $newNode = $ours.ImportNode($rsnap, $true)
  foreach ($cls in $newNode.SelectNodes('./Class')) {
    $cn = $cls.GetAttribute('name')
    if ($map.ContainsKey($cn)) {
      $cls.SetAttribute('dogTag', $map[$cn].dog)
      $ev = $cls.SelectSingleNode('./Class[@Embed="eventConnectionsChanged"]')
      if ($ev) {
        $ev.SetAttribute('eventId', $map[$cn].id)
        $ev.SetAttribute('date', $map[$cn].date)
        $ev.SetAttribute('user', $map[$cn].user)
      }
    }
  }
  [void]$osnap.ParentNode.ReplaceChild($newNode, $osnap)
  Save-Xml $ours $oursFile.FullName
  $count++
}
Write-Host "Replaced snapshots in $count program/FB stream(s)."
