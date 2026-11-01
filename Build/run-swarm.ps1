<#
.SYNOPSIS
    Starts N swarm nodes from ONE TrackerSwarm build, each with its own node home and port.

.DESCRIPTION
    Layout (platform = -Rid, default win-x64):
        bin\<platform>\TrackerSwarm\ProcessorApplication\     program files + the shared appsettings.*.NodeTrackerSwarm.json
        bin\<platform>\TrackerSwarm\nodes\swarm-001\          node 1: databases, Module_Data, its own settings override
        bin\<platform>\TrackerSwarm\nodes\swarm-002\ ...

    Node i listens on http://localhost:(FirstPort + i - 1) (http only, no https port per node).
    On first creation every home gets:
      - the preset files <project>\configFiles\TrackerSwarm\** (if any), like the build does for the test mesh;
      - Modules\P2PModule\appsettings.P2P.NodeTrackerSwarm.json with this node's own signalling endpoint and the
        seed endpoints, layered over the shared file of the same name in the program folder.
    Re-running with different -Seeds rewrites that override in every home (nothing else is touched).

    Each node runs:
        ProcessorApplication.exe --node-home <home> --environment NodeTrackerSwarm --urls http://localhost:<port> --SettingsIngest:Mode=<Reingest>

.EXAMPLE
    .\Build\run-swarm.ps1 -Build -Count 50                     # build once, start 50 nodes on 6101..6150
    .\Build\run-swarm.ps1 -Count 10 -FirstPort 6201            # 10 nodes on other ports
    .\Build\run-swarm.ps1 -Count 50 -Seeds http://localhost:5044,http://localhost:5045   # seed from the test-mesh trackers
    .\Build\run-swarm.ps1 -Stop                                # stop every swarm node started by this script
    .\Build\run-swarm.ps1 -Count 50 -Fresh                     # wipe the homes' data first (asks to confirm)
#>
[CmdletBinding()]
param(
    [int] $Count = 10,
    [int] $FirstPort = 6101,

    # Signalling endpoints the nodes contact first. Default: the first swarm node.
    [string[]] $Seeds = @(),

    [string] $Rid = 'win-x64',
    [switch] $Build,

    [ValidateSet('Changed', 'Overwrite', 'Off')]
    [string] $Reingest = 'Changed',

    # Seconds between node starts (the first node gets 5 s head start as the default seed).
    [double] $StartDelaySeconds = 1,

    [switch] $Fresh,
    [switch] $Stop
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$sln = Join-Path $root 'ProcessorApplication.sln'
$groupRoot = Join-Path $root "bin\$Rid\TrackerSwarm"
$app = Join-Path $groupRoot 'ProcessorApplication'
$homesRoot = Join-Path $groupRoot 'nodes'
$pidFile = Join-Path $groupRoot 'swarm.pids.json'

function Stop-Swarm {
    if (-not (Test-Path $pidFile)) { Write-Host 'No running swarm nodes recorded.'; return }
    $entries = Get-Content $pidFile -Raw | ConvertFrom-Json
    foreach ($e in @($entries)) {
        $p = Get-Process -Id $e.Pid -ErrorAction SilentlyContinue
        if ($p -and ($p.ProcessName -like 'ProcessorApplication*' -or $p.ProcessName -eq 'dotnet')) {
            Stop-Process -Id $e.Pid -Force
        }
    }
    Remove-Item $pidFile -Force
    Write-Host "Stopped $(@($entries).Count) swarm node(s)."
}

if ($Stop) { Stop-Swarm; return }

if ($Build) {
    Write-Host "=== Building NodeTrackerSwarm ($Rid) ===" -ForegroundColor Cyan
    & dotnet build $sln -c NodeTrackerSwarm -p:RuntimeIdentifier=$Rid --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'Build of NodeTrackerSwarm failed.' }
}

if (-not (Test-Path (Join-Path $app 'ProcessorApplication.dll'))) {
    throw "The swarm is not built ($app). Run with -Build, or build configuration NodeTrackerSwarm."
}

if ($Seeds.Count -eq 0) { $Seeds = @("http://localhost:$FirstPort") }

# Preset folders <project>\configFiles\TrackerSwarm (found once).
$presetDirs = @(Get-ChildItem $root -Directory -Recurse -Filter 'configFiles' -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -notmatch '\\(bin|obj|node_modules)\\' } |
    ForEach-Object { Join-Path $_.FullName 'TrackerSwarm' } |
    Where-Object { Test-Path $_ })

function Get-NodeName([int] $i) { 'swarm-{0:D3}' -f $i }

function Write-NodeOverride([string] $nodeHome, [int] $port) {
    $dir = Join-Path $nodeHome 'Modules\P2PModule'
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $override = [ordered]@{
        P2PSettings = [ordered]@{
            Endpoints     = @("http://localhost:$port")
            SeedEndpoints = @($Seeds | Where-Object { $_ -ne "http://localhost:$port" })
        }
    }
    $json = $override | ConvertTo-Json -Depth 5
    Set-Content -Path (Join-Path $dir 'appsettings.P2P.NodeTrackerSwarm.json') -Value $json -Encoding UTF8
}

# --- Prepare homes ---------------------------------------------------------------------------------------------
if ($Fresh) {
    $answer = Read-Host "Delete databases and Module_Data of swarm-001..$(Get-NodeName $Count)? Type YES"
    if ($answer -ne 'YES') { Write-Host 'Cancelled.'; return }
}

for ($i = 1; $i -le $Count; $i++) {
    $nodeHome = Join-Path $homesRoot (Get-NodeName $i)
    $isNew = -not (Test-Path $nodeHome)
    New-Item -ItemType Directory -Force -Path $nodeHome | Out-Null

    if ($Fresh -and -not $isNew) {
        Get-ChildItem $nodeHome -File -Recurse | Where-Object { $_.Name -match '\.db(-wal|-shm)?$' } | Remove-Item -Force
        foreach ($d in @('Module_Data', 'data')) {
            $p = Join-Path $nodeHome $d
            if (Test-Path $p) { Remove-Item $p -Recurse -Force }
        }
    }
    if ($isNew -or $Fresh) {
        foreach ($preset in $presetDirs) { Copy-Item (Join-Path $preset '*') $nodeHome -Recurse -Force }
    }
    Write-NodeOverride $nodeHome ($FirstPort + $i - 1)
}

# --- Ports -------------------------------------------------------------------------------------------------------
for ($i = 1; $i -le $Count; $i++) {
    $port = $FirstPort + $i - 1
    $busy = Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue
    if ($busy) { throw "Port $port ($(Get-NodeName $i)) is already in use by PID $($busy[0].OwningProcess). Stop it first (-Stop) or use -FirstPort." }
}

# --- Start ---------------------------------------------------------------------------------------------------------
$exe = Join-Path $app 'ProcessorApplication.exe'
$dll = Join-Path $app 'ProcessorApplication.dll'
$started = @()
if (Test-Path $pidFile) { $started += @(Get-Content $pidFile -Raw | ConvertFrom-Json) }

for ($i = 1; $i -le $Count; $i++) {
    $name = Get-NodeName $i
    $port = $FirstPort + $i - 1
    $nodeHome = Join-Path $homesRoot $name
    $appArgs = @('--node-home', "`"$nodeHome`"", '--environment', 'NodeTrackerSwarm', '--urls', "http://localhost:$port", "--SettingsIngest:Mode=$Reingest")

    if (Test-Path $exe) {
        $proc = Start-Process -FilePath $exe -ArgumentList $appArgs -WorkingDirectory $nodeHome -PassThru
    } else {
        $proc = Start-Process -FilePath 'dotnet' -ArgumentList (@("`"$dll`"") + $appArgs) -WorkingDirectory $nodeHome -PassThru
    }
    $started += [pscustomobject]@{ Node = $name; Pid = $proc.Id; Url = "http://localhost:$port" }
    Write-Host ("{0}  PID {1,-6} http://localhost:{2}" -f $name, $proc.Id, $port) -ForegroundColor Green

    if ($i -eq 1) { Start-Sleep -Seconds 5 } elseif ($StartDelaySeconds -gt 0) { Start-Sleep -Milliseconds ([int]($StartDelaySeconds * 1000)) }
}

$started | ConvertTo-Json | Set-Content $pidFile -Encoding UTF8
Write-Host ''
Write-Host "Seeds: $($Seeds -join ', ')"
Write-Host 'Stop all: .\Build\run-swarm.ps1 -Stop'
