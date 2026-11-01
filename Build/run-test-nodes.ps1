<#
.SYNOPSIS
    Starts the one-PC test mesh: Tracker1, Tracker2, Client1, Client2. One build per group, one node home per node.

.DESCRIPTION
    Layout (platform = -Rid, default win-x64):
        bin\<platform>\Tracker\ProcessorApplication\   program files of the Tracker group (shared)
        bin\<platform>\Tracker\nodes\Tracker1\         Tracker1's databases, Module_Data, own settings files
        bin\<platform>\Tracker\nodes\Tracker2\
        bin\<platform>\Client\ProcessorApplication\    program files of the Client group
        bin\<platform>\Client\nodes\Client1\ , Client2\

    Node        Group     Environment            Ports (https / http)   P2P role
    Tracker1    Tracker   NodeTracker1Debug      7107 / 5044            tracker, file_tracker, file_node
    Tracker2    Tracker   NodeTracker2Debug      7108 / 5045            tracker, file_tracker, file_node
    Client1     Client    NodeClient1Debug       7109 / 5046            file_node
    Client2     Client    NodeClient2Debug       7110 / 5047            file_node

    Each node is started as:
        ProcessorApplication.exe --node-home <its home> --environment <Environment> --urls ... --SettingsIngest:Mode=<Reingest>
    It reads the shared settings files from the program folder and its own appsettings.*.<Environment>.json from its
    home (the build copies them there from the projects). Settings files are re-applied on start according to -Reingest.

.EXAMPLE
    .\Build\run-test-nodes.ps1 -Build -Reingest Overwrite     # first time: build both groups, apply the node settings
    .\Build\run-test-nodes.ps1                                # later: start with the existing builds ("Changed" re-ingest)
    .\Build\run-test-nodes.ps1 -Nodes Tracker1,Client1        # only some nodes
    .\Build\run-test-nodes.ps1 -Stop                          # stop the nodes this script started
    .\Build\run-test-nodes.ps1 -Fresh                         # wipe node databases + Module_Data first (asks to confirm)
    .\Build\run-test-nodes.ps1 -MigrateOld                    # once: copy the data of the old bin\<Node>\ProcessorApplication folders into the homes
#>
[CmdletBinding()]
param(
    [ValidateSet('Tracker1', 'Tracker2', 'Client1', 'Client2')]
    [string[]] $Nodes = @('Tracker1', 'Tracker2', 'Client1', 'Client2'),

    # Platform folder (must match the RuntimeIdentifier of the build).
    [string] $Rid = 'win-x64',

    # Build the needed groups first (dotnet build <sln> -c NodeTracker1Debug / NodeClient1Debug).
    [switch] $Build,

    # How the module settings files are applied on start: Changed (default), Overwrite, Off.
    [ValidateSet('Changed', 'Overwrite', 'Off')]
    [string] $Reingest = 'Changed',

    # Delete each started node's databases and Module_Data before starting (node keys, identity, files, catalogue).
    [switch] $Fresh,

    # Copy databases + Module_Data from the old layout (bin\<Node>\ProcessorApplication) into the node homes.
    [switch] $MigrateOld,

    # Stop the nodes started by this script and exit.
    [switch] $Stop
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$sln = Join-Path $root 'ProcessorApplication.sln'
$pidFile = Join-Path $root "bin\$Rid\test-nodes.pids.json"

$nodeTable = [ordered]@{
    Tracker1 = @{ Group = 'Tracker'; Config = 'NodeTracker1Debug'; Https = 7107; Http = 5044 }
    Tracker2 = @{ Group = 'Tracker'; Config = 'NodeTracker2Debug'; Https = 7108; Http = 5045 }
    Client1  = @{ Group = 'Client';  Config = 'NodeClient1Debug';  Https = 7109; Http = 5046 }
    Client2  = @{ Group = 'Client';  Config = 'NodeClient2Debug';  Https = 7110; Http = 5047 }
}

function Get-AppFolder([string] $node) { Join-Path $root "bin\$Rid\$($nodeTable[$node].Group)\ProcessorApplication" }
function Get-NodeHome([string] $node)  { Join-Path $root "bin\$Rid\$($nodeTable[$node].Group)\nodes\$node" }

# Data that belongs to a node (everything else in a home is its settings files).
function Get-NodeDataItems([string] $folder) {
    $items = @()
    $items += Get-ChildItem $folder -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '\.db(-wal|-shm)?$' }
    foreach ($d in @('Module_Data', 'data')) {
        $p = Join-Path $folder $d
        if (Test-Path $p) { $items += Get-Item $p }
    }
    $mods = Join-Path $folder 'Modules'
    if (Test-Path $mods) {
        $items += Get-ChildItem $mods -File -Recurse -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '\.db(-wal|-shm)?$' }
    }
    return $items
}

# Preset files <project>\configFiles\<Node>\** (the build seeds them too; used again after -Fresh).
function Copy-NodePresets([string] $node, [string] $nodeHome) {
    Get-ChildItem $root -Directory -Recurse -Filter 'configFiles' -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch '\\(bin|obj|node_modules)\\' } |
        ForEach-Object {
            $preset = Join-Path $_.FullName $node
            if (Test-Path $preset) { Copy-Item (Join-Path $preset '*') $nodeHome -Recurse -Force }
        }
}

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

# --- Build (one build per group; either configuration of a group sets up both of its node homes) ----------
if ($Build) {
    $groups = $Nodes | ForEach-Object { $nodeTable[$_].Group } | Select-Object -Unique
    foreach ($g in $groups) {
        $cfg = if ($g -eq 'Tracker') { 'NodeTracker1Debug' } else { 'NodeClient1Debug' }
        Write-Host "=== Building group $g ($cfg, $Rid) ===" -ForegroundColor Cyan
        & dotnet build $sln -c $cfg -p:RuntimeIdentifier=$Rid --nologo -v minimal
        if ($LASTEXITCODE -ne 0) { throw "Build of $cfg failed." }
    }
}

# --- Checks ------------------------------------------------------------------------------------------------
foreach ($node in $Nodes) {
    $app = Get-AppFolder $node
    if (-not (Test-Path (Join-Path $app 'ProcessorApplication.dll'))) {
        throw "$node's group is not built ($app). Run with -Build, or build configuration $($nodeTable[$node].Config)."
    }
    foreach ($port in @($nodeTable[$node].Https, $nodeTable[$node].Http)) {
        $busy = Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue
        if ($busy) { throw "Port $port (needed by $node) is already in use by PID $($busy[0].OwningProcess). Stop it first (-Stop)." }
    }
}

# --- Migrate the old layout ----------------------------------------------------------------------------------
if ($MigrateOld) {
    foreach ($node in $Nodes) {
        $old = Join-Path $root "bin\$node\ProcessorApplication"
        $nodeHome = Get-NodeHome $node
        if (-not (Test-Path $old)) { Write-Host "$node : no old folder ($old), skipped."; continue }
        New-Item -ItemType Directory -Force -Path $nodeHome | Out-Null
        foreach ($item in (Get-NodeDataItems $old)) {
            $rel = $item.FullName.Substring($old.Length).TrimStart('\')
            $dest = Join-Path $nodeHome $rel
            New-Item -ItemType Directory -Force -Path (Split-Path $dest) | Out-Null
            Copy-Item $item.FullName $dest -Recurse -Force
        }
        Write-Host "$node : data copied from $old" -ForegroundColor Yellow
    }
}

# --- Fresh start ---------------------------------------------------------------------------------------------
if ($Fresh) {
    $answer = Read-Host "Delete the databases and Module_Data of: $($Nodes -join ', ')? Type YES"
    if ($answer -ne 'YES') { Write-Host 'Cancelled.'; return }

    foreach ($node in $Nodes) {
        $nodeHome = Get-NodeHome $node
        Get-NodeDataItems $nodeHome | ForEach-Object { Remove-Item $_.FullName -Recurse -Force }
        Copy-NodePresets $node $nodeHome
        Write-Host "$node wiped." -ForegroundColor Yellow
    }
}

# --- Start ---------------------------------------------------------------------------------------------------
$started = @()
if (Test-Path $pidFile) { $started += @(Get-Content $pidFile -Raw | ConvertFrom-Json) }

# Trackers first, so the clients find their seeds.
$ordered = $Nodes | Sort-Object { if ($_ -like 'Tracker*') { 0 } else { 1 } }, { $_ }
foreach ($node in $ordered) {
    $n = $nodeTable[$node]
    $app = Get-AppFolder $node
    $nodeHome = Get-NodeHome $node
    New-Item -ItemType Directory -Force -Path $nodeHome | Out-Null

    $urls = "https://localhost:$($n.Https);http://localhost:$($n.Http)"
    $appArgs = @('--node-home', "`"$nodeHome`"", '--environment', $n.Config, '--urls', $urls, "--SettingsIngest:Mode=$Reingest")

    $exe = Join-Path $app 'ProcessorApplication.exe'
    if (Test-Path $exe) {
        $proc = Start-Process -FilePath $exe -ArgumentList $appArgs -WorkingDirectory $nodeHome -PassThru
    } else {
        $dll = Join-Path $app 'ProcessorApplication.dll'
        $proc = Start-Process -FilePath 'dotnet' -ArgumentList (@("`"$dll`"") + $appArgs) -WorkingDirectory $nodeHome -PassThru
    }
    $started += [pscustomobject]@{ Node = $node; Pid = $proc.Id; Url = "http://localhost:$($n.Http)" }
    Write-Host ("{0,-9} PID {1,-6} http://localhost:{2}  https://localhost:{3}  ({4}, re-ingest {5})" -f $node, $proc.Id, $n.Http, $n.Https, $n.Config, $Reingest) -ForegroundColor Green

    if ($node -like 'Tracker*') { Start-Sleep -Seconds 3 }
}

New-Item -ItemType Directory -Force -Path (Split-Path $pidFile) | Out-Null
$started | ConvertTo-Json | Set-Content $pidFile -Encoding UTF8

Write-Host ''
Write-Host 'Admin pages (local only): /Main/Settings/Index, /Main/SettingsIngest/Index, /P2P/..., /FileTransfer/FileArchive/Index'
Write-Host 'Stop all: .\Build\run-test-nodes.ps1 -Stop'
