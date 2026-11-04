param([Parameter(Mandatory=$true)][string]$Dir)
foreach ($f in (Get-ChildItem $Dir -Recurse -Filter '*.FBD')) {
  $x = New-Object System.Xml.XmlDocument
  $x.Load($f.FullName)
  $els = @{}
  # index every element by localId (blocks, variables, wires, comments, dividers)
  foreach ($c in $x.SelectNodes('//Class[@localId]')) {
    $id = $c.GetAttribute('localId')
    if (-not $els.ContainsKey($id)) { $els[$id] = @() }
    $els[$id] += $c
  }
  $bad = New-Object System.Collections.Generic.List[string]
  foreach ($w in $x.SelectNodes('//Class[@ClassType="CDGMWireXml"]')) {
    $wid = $w.GetAttribute('localId')
    $in = $w.SelectSingleNode('./Class[@Embed="input"]')
    $out = $w.SelectSingleNode('./Class[@Embed="output"]')
    if (-not $in -or -not $out) { $bad.Add("wire $wid missing endpoint"); continue }
    $src = $in.GetAttribute('connectId'); $dst = $out.GetAttribute('connectId')
    # target must be an element that references this wire on an input pin / inConnectId
    $foundTarget = $false
    if ($els.ContainsKey($dst)) {
      foreach ($e in $els[$dst]) {
        if ($e.SelectSingleNode(".//Class[@connectId='$wid']") -or $e.GetAttribute('inConnectId') -eq $wid) { $foundTarget = $true }
      }
    }
    if (-not $foundTarget) { $bad.Add("wire $wid target $dst does not reference it") }
    # source: element should reference the wire on an output pin (or be a branch parent: then src is another element and the wire's output points at parent)
    $foundSrc = $false
    if ($els.ContainsKey($src)) {
      foreach ($e in $els[$src]) {
        if ($e.SelectSingleNode(".//Class[@connectId='$wid']") -or $e.GetAttribute('outConnectId') -eq $wid -or $e.GetAttribute('localId') -eq $src) { $foundSrc = $true }
      }
    }
    if (-not $foundSrc) { $bad.Add("wire $wid source $src missing") }
  }
  if ($bad.Count) {
    Write-Host ("=== {0} ({1})" -f $f.Directory.Name, $bad.Count)
    $bad | Select-Object -First 6 | ForEach-Object { Write-Host ("  " + $_) }
  }
}
