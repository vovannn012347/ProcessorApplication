<#
.SYNOPSIS
    Starts the one-PC test mesh: Tracker1, Tracker2, Client1, Client2, each from its own build folder
    (bin\<Node>\ProcessorApplication), with its own database, Module_Data and ports.

.DESCRIPTION
    Node        Build configuration    Ports (https / http)   P2P role
    Tracker1    NodeTracker1Debug      7107 / 5044            tracker, file_tracker, file_node
    Tracker2    NodeTracker2Debug      7108 / 5045            tracker, file_tracker, file_node (seeds: Tracker1)
    Client1     NodeClient1Debug       7109 / 5046            file_node (seeds: both trackers)
    Client2     NodeClient2Debug       7110 / 5047            file_node (seeds: both trackers)

    Each node reads appsettings.*.<Configuration>.json from its own folder. Settings files are re-applied on start
    according to -Reingest (see SettingsIngest in appsettings.json and /Main/SettingsIngest/Index).

    Visual Studio can only run one build configuration at a time, so the launch profiles alone can't start the
    whole mesh. This script builds each configuration into its own folder and starts all nodes side by side.

.EXAMPLE
    .\Build\run-test-nodes.ps1 -Build -Reingest Overwrite     # first time: build all 4, make the DBs take the new node settings
    .\Build\run-test-nodes.ps1                                # later: start with the existing builds ("Changed" re-ingest)
    .\Build\run-test-nodes.ps1 -Nodes Tracker1,Client1        # only some nodes
    .\Build\run-test-nodes.ps1 -Stop                          # stop the nodes this script started
    .\Build\run-test-nodes.ps1 -Fresh -Build                  # wipe node DBs + Module_Data first (asks to confirm)
#>
[CmdletBinding()]
param(
    [ValidateSet('Tracker1', 'Tracker2', 'Client1', 'Client2')]
    [string[]] $Nodes = @('Tracker1', 'Tracker2', 'Client1', 'Client2'),

    # Build each node's configuration first (dotnet build <sln> -c Node...Debug).
    [switch] $Build,

    # How the module settings files are applied on start: Changed (default), Overwrite, Off.
    [ValidateSet('Changed', 'Overwrite', 'Off')]
    [string] $Reingest = 'Changed',

    # Delete each started node's databases and Module_Data before starting (node keys, identity, files, catalogue).
    [switch] $Fresh,

    # Stop the nodes started by this script (PIDs remembered in bin\test-nodes.pids.json) and exit.
    [switch] $Stop
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$sln = Join-Path $root 'ProcessorApplication.sln'
$pidFile = Join-Path $root 'bin\test-nodes.pids.json'

$nodeTable = [ordered]@{
    Tracker1 = @{ Config = 'NodeTracker1Debug'; Https = 7107; Http = 5044 }
    Tracker2 = @{ Config = 'NodeTracker2Debug'; Https = 7108; Http = 5045 }
    Client1  = @{ Config = 'NodeClient1Debug';  Https = 7109; Http = 5046 }
    Client2  = @{ Config = 'NodeClient2Debug';  Https = 7110; Http = 5047 }
}

function Get-NodeFolder([string] $node) { Join-Path $root "bin\$node\ProcessorApplication" }

function Stop-TestNodes {
    if (-not (Test-Path $pidFile)) { Write-Host 'No running test nodes recorded.'; return }
    $entries = Get-Content $pidFile -Raw | ConvertFrom-Json
    foreach ($e in @($entries)) {
        $p = Get-Process -Id $e.Pid -ErrorAction SilentlyContinue
        # Only kill what is still our app (a PID can be reused by an unrelated process after a reboot).
        if ($p -and ($p.ProcessName -like 'ProcessorApplication*' -or $p.ProcessName -eq 'dotnet')) {
            Write-Host "Stopping $($e.Node) (PID $($e.Pid))"
            Stop-Process -Id $e.Pid -Force
        }
    }
    Remove-Item $pidFile -Force
}

if ($Stop) { Stop-TestNodes; return }

# --- Build -------------------------------------------------------------------------------------------------
if ($Build) {
    foreach ($node in $Nodes) {
        $cfg = $nodeTable[$node].Config
        Write-Host "=== Building $node ($cfg) ===" -ForegroundColor Cyan
        & dotnet build $sln -c $cfg --nologo -v minimal
        if ($LASTEXITCODE -ne 0) { throw "Build of $cfg failed." }
    }
}

# --- Checks ------------------------------------------------------------------------------------------------
foreach ($node in $Nodes) {
    $folder = Get-NodeFolder $node
    if (-not (Test-Path (Join-Path $folder 'ProcessorApplication.dll'))) {
        throw "$node is not built ($folder). Run with -Build, or build configuration $($nodeTable[$node].Config)."
    }
    foreach ($port in @($nodeTable[$node].Https, $nodeTable[$node].Http)) {
        $busy = Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue
        if ($busy) { throw "Port $port (needed by $node) is already in use by PID $($busy[0].OwningProcess). Stop it first (-Stop)." }
    }
}

# --- Fresh start ---------------------------------------------------------------------------------------------
if ($Fresh) {
    $answer = Read-Host "Delete the databases and Module_Data of: $($Nodes -join ', ')? Type YES"
    if ($answer -ne 'YES') { Write-Host 'Cancelled.'; return }

    foreach ($node in $Nodes) {
        $folder = Get-NodeFolder $node
        Get-ChildItem $folder -File | Where-Object { $_.Name -match '\.db(-wal|-shm)?$' } | Remove-Item -Force
        $data = Join-Path $folder 'Module_Data'
        if (Test-Path $data) { Remove-Item $data -Recurse -Force }

        # Put back the node's preset files (e.g. Client1's P2P identity) the build normally copies
        # from <project>\configFiles\<Node> (see ForceDeployNodeConfigFiles in Directory.Build.props).
        Get-ChildItem $root -Directory -Recurse -Filter 'configFiles' -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
            ForEach-Object {
                $preset = Join-Path $_.FullName $node
                if (Test-Path $preset) { Copy-Item (Join-Path $preset '*') $folder -Recurse -Force }
            }
        Write-Host "$node wiped." -ForegroundColor Yellow
    }
}

# --- Start ---------------------------------------------------------------------------------------------------
$started = @()
if (Test-Path $pidFile) { $started += @(Get-Content $pidFile -Raw | ConvertFrom-Json) }

$previousEnv = $env:ASPNETCORE_ENVIRONMENT
try {
    # Trackers first, so the clients find their seeds.
    $ordered = $Nodes | Sort-Object { if ($_ -like 'Tracker*') { 0 } else { 1 } }, { $_ }
    foreach ($node in $ordered) {
        $n = $nodeTable[$node]
        $folder = Get-NodeFolder $node
        $urls = "https://localhost:$($n.Https);http://localhost:$($n.Http)"
        $appArgs = @('--urls', $urls, "--SettingsIngest:Mode=$Reingest")

        # The child process inherits the environment at start; the per-node environment name selects
        # appsettings.*.<Configuration>.json.
        $env:ASPNETCORE_ENVIRONMENT = $n.Config

        $exe = Join-Path $folder 'ProcessorApplication.exe'
        if (Test-Path $exe) {
            $proc = Start-Process -FilePath $exe -ArgumentList $appArgs -WorkingDirectory $folder -PassThru
        } else {
            $proc = Start-Process -FilePath 'dotnet' -ArgumentList (@('ProcessorApplication.dll') + $appArgs) -WorkingDirectory $folder -PassThru
        }
        $started += [pscustomobject]@{ Node = $node; Pid = $proc.Id; Url = "http://localhost:$($n.Http)" }
        Write-Host ("{0,-9} PID {1,-6} http://localhost:{2}  https://localhost:{3}  ({4}, re-ingest {5})" -f $node, $proc.Id, $n.Http, $n.Https, $n.Config, $Reingest) -ForegroundColor Green

        if ($node -like 'Tracker*') { Start-Sleep -Seconds 3 }
    }
}
finally {
    $env:ASPNETCORE_ENVIRONMENT = $previousEnv
}

New-Item -ItemType Directory -Force -Path (Split-Path $pidFile) | Out-Null
$started | ConvertTo-Json | Set-Content $pidFile -Encoding UTF8

Write-Host ''
Write-Host 'Admin pages (local only): /Main/Settings/Index, /Main/SettingsIngest/Index, /P2P/..., /FileTransfer/FileArchive/Index'
Write-Host 'Stop all: .\Build\run-test-nodes.ps1 -Stop'
