<#
.SYNOPSIS
    Starts the Triconex-to-TIA engine on this machine with the local toolchain
    (circuit-reader, BlLadderCli, pt2conv) and full TIA Portal Openness support.

.DESCRIPTION
    Release build, server GC, port 5080 by default - the port the web app reads
    from MSW_ENGINE_URL in .env.local. Logs go to
    %TEMP%\opencode\blrun\engine-local.*.log.

    Pass -WithWeb to also start `npm run dev` for the site on port 3000.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\start-engine-local.ps1 -WithWeb
#>
param(
    [int]$Port = 5080,
    [switch]$WithWeb,
    [switch]$Restart
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$dll  = Join-Path $root "engine\src\TriconexToTia.Server\bin\Release\net7.0\TriconexToTia.Server.dll"
$pt2  = Join-Path $root "pt2-conv\src\Pt2Conv\bin\Release\net7.0\pt2conv.exe"
$cli  = Join-Path $root "tools\BlLadderCli\bin\Release\net48\BlLadderCli.exe"
$cr   = "C:\Users\Moatasem\circuit-reader\csharp\src\CircuitReader.Cli\bin\Release\net7.0\circuit-reader.exe"
if (-not (Test-Path $cr)) { $cr = Join-Path $root "circuit-reader\src\CircuitReader.Cli\bin\Release\net7.0\circuit-reader.exe" }

$logDir = Join-Path $env:TEMP "opencode\blrun"
New-Item -ItemType Directory -Force -Path $logDir | Out-Null

function Test-Health([int]$p) {
    try { Invoke-RestMethod "http://localhost:$p/health" -TimeoutSec 3 | Out-Null; return $true } catch { return $false }
}

foreach ($path in @($dll, $pt2, $cli, $cr)) {
    if (-not (Test-Path $path)) { throw "Missing build output: $path" }
}

if ($Restart) {
    Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -like "*TriconexToTia*" } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Seconds 2
}

if (Test-Health $Port) {
    Write-Host "Engine already listening on http://localhost:$Port"
} else {
    $env:PORT = "$Port"
    $env:MSW_CIRCUITREADER_PATH = $cr
    $env:MSW_LADDERCLI_PATH = $cli
    $env:MSW_PT2CONV_PATH = $pt2
    $env:MSW_OUTPUT_DIR = Join-Path $root "data\engine-output"
    $env:MSW_STATUS_DIR = Join-Path $root "status"
    $env:MSW_MAX_UPLOAD_BYTES = "1073741824"

    # Match the token the web app sends (MSW_ENGINE_TOKEN in .env.local) so the
    # engine can be exposed through a tunnel without becoming an open relay.
    $envFile = Join-Path $root ".env.local"
    if (Test-Path $envFile) {
        $line = Get-Content $envFile | Where-Object { $_ -match '^\s*MSW_ENGINE_TOKEN\s*=' } | Select-Object -First 1
        if ($line) {
            $token = ($line -split '=', 2)[1].Trim()
            if ($token) { $env:MSW_ENGINE_TOKEN = $token }
        }
    }

    # Full local performance: server GC, PGO and loop quick-jitting.
    $env:DOTNET_gcServer = "1"
    $env:DOTNET_TieredPGO = "1"
    $env:DOTNET_TC_QuickJitForLoops = "1"

    New-Item -ItemType Directory -Force -Path $env:MSW_OUTPUT_DIR | Out-Null

    $proc = Start-Process -FilePath "dotnet" -ArgumentList @($dll) `
        -RedirectStandardOutput (Join-Path $logDir "engine-local.out.log") `
        -RedirectStandardError (Join-Path $logDir "engine-local.err.log") `
        -WindowStyle Hidden -PassThru

    $deadline = (Get-Date).AddSeconds(45)
    do {
        Start-Sleep -Seconds 2
        if (Test-Health $Port) { break }
    } while ((Get-Date) -lt $deadline)

    if (Test-Health $Port) {
        $health = Invoke-RestMethod "http://localhost:$Port/health" -TimeoutSec 5
        Write-Host "Engine pid=$($proc.Id) on http://localhost:$Port (TIA import: $($health.tiaImport.available))"
    } else {
        throw "Engine did not become healthy. See $logDir\engine-local.err.log"
    }
}

if ($WithWeb) {
    $web = $null
    try { $web = Invoke-WebRequest -UseBasicParsing "http://localhost:3000" -TimeoutSec 3 } catch {}
    if ($web) {
        Write-Host "Web dev server already on http://localhost:3000"
    } else {
        # PORT was exported for the engine; next dev would inherit it and fight
        # for the same port.
        Remove-Item Env:PORT -ErrorAction SilentlyContinue
        Start-Process -FilePath "cmd.exe" -WorkingDirectory $root `
            -ArgumentList @("/c", "npm run dev -- -p 3000 > `"$logDir\web-dev.log`" 2>&1") `
            -WindowStyle Hidden
        $deadline = (Get-Date).AddSeconds(90)
        do {
            Start-Sleep -Seconds 3
            try { $web = Invoke-WebRequest -UseBasicParsing "http://localhost:3000/api/engine/health" -TimeoutSec 10 } catch {}
        } while (-not $web -and (Get-Date) -lt $deadline)
        if ($web) { Write-Host "Web on http://localhost:3000 (engine reachable)" }
        else { Write-Host "Web did not answer yet - see $logDir\web-dev.log" }
    }
}
