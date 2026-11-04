#requires -version 5
<#
  Build a TriStation .pt2x from a PLCopen/TriStation XML using TriStation's own
  XML Import/Export utility.

  This is the path TriStation itself uses when it imports XML: it creates the
  elements, compiles them (which is what writes the .IST listings and the
  compiler-generated _TEMP_* variables into every instance snapshot) and then
  lets the editor resolve the block instances. A container assembled outside
  TriStation cannot contain those compiler artifacts, which is why an
  externally built .pt2x reports WF0018 "out-of-date element" as soon as the
  project is compiled.

  Usage:
    .\Import-TriStationXml.ps1 -Xml <file.xml> -Out <file.pt2x> [-Version 5.8.0]

  The output file is created next to <file.pt2x>; an existing file is never
  overwritten unless -Force is given.
#>
param(
  [Parameter(Mandatory = $true)][string]$Xml,
  [Parameter(Mandatory = $true)][string]$Out,
  [string]$Version = "5.8.0",
  [switch]$Force
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $Xml)) { throw "XML not found: $Xml" }
if ((Test-Path -LiteralPath $Out) -and -not $Force) { throw "Output exists: $Out (use -Force to overwrite)" }

$root = Join-Path ${env:ProgramFiles(x86)} "Triconex\TriStation 1131 $Version"
$programs = Join-Path $root "Programs"
$tool = Join-Path $programs "XMLImportExport.exe"
if (-not (Test-Path -LiteralPath $tool)) { throw "TriStation XML Import/Export not found: $tool" }

# The wrapper that ships with TriStation (XmlImpExp.bat) relies on this variable.
$env:TS58PROGRAMSDIR = $programs

$xmlPath = (Resolve-Path -LiteralPath $Xml).Path
$outPath = [IO.Path]::GetFullPath($Out)
$outDir = Split-Path -Parent $outPath
if ($outDir -and -not (Test-Path -LiteralPath $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }

Write-Host "Importing $xmlPath"
Write-Host "        -> $outPath"

& $tool -import $xmlPath $outPath
$code = $LASTEXITCODE

if (-not (Test-Path -LiteralPath $outPath)) {
  throw "TriStation did not produce $outPath (exit code $code)"
}

$info = Get-Item -LiteralPath $outPath
Write-Host ("Done: {0:N0} bytes, {1}" -f $info.Length, $info.LastWriteTime)
