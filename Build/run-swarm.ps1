<#
.SYNOPSIS
    Starts the swarm nodes made by setup-swarm-homes.ps1 (or the swarm template node) from ONE TrackerSwarm build.

.DESCRIPTION
    Layout (platform = -Rid, default win-x64):
        bin\<platform>\TrackerSwarm\ProcessorApplication\     program files + the shared appsettings.*.NodeTrackerSwarm.json
        bin\<platform>\TrackerSwarm\nodes\_template\          the template node (admin user etc.), see setup-swarm-homes.ps1
        bin\<platform>\TrackerSwarm\nodes\<name>\             one node: databases, Module_Data, swarm-node.json, own settings

    Each node runs:
        ProcessorApplication.exe --node-home <home> --environment NodeTrackerSwarm --urls http://localhost:<port> --SettingsIngest:Mode=<Reingest>
    Its port, name and seeds come from <home>\swarm-node.json (written by setup-swarm-homes.ps1).

.EXAMPLE
    .\Build\run-swarm.ps1 -Build -Template              # build, start only the template node (http://localhost:5048) to set it up
    .\Build\setup-swarm-homes.ps1 -Count 50             # (template stopped) make 50 homes from it
    .\Build\run-swarm.ps1 -Reingest Overwrite           # first start of the copies: apply every settings file
    .\Build\run-swarm.ps1                               # later starts
    .\Build\run-swarm.ps1 -Count 10                     # only the first 10 homes
    .\Build\run-swarm.ps1 -Stop                         # stop everything this script started
#>
[CmdletBinding()]
param(
    # Start only the template node (to set it up), not the swarm.
    [switch] $Template,

    # Start only the first N homes (in swarm-homes.json order). 0 = all.
    [int] $Count = 0,

    [string] $Rid = 'win-x64',
    [switch] $Build,

    [ValidateSet('Changed', 'Overwrite', 'Off')]
    [string] $Reingest = 'Changed',

    # Seconds between node starts (the first node gets 5 s head start).
    [double] $StartDelaySeconds = 1,

    # Console window of each node: Minimized (default, one taskbar entry each), Normal, or Hidden (log nothing visible).
    [ValidateSet('Minimized', 'Normal', 'Hidden')]
    [string] $Window = 'Minimized',

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
    $entries = @(Get-Content $pidFile -Raw | ConvertFrom-Json)
    foreach ($e in $entries) {
        $p = Get-Process -Id $e.Pid -ErrorAction SilentlyContinue
        if ($p -and ($p.ProcessName -like 'ProcessorApplication*' -or $p.ProcessName -eq 'dotnet')) {
            Stop-Process -Id $e.Pid -Force
        }
    }
    Remove-Item $pidFile -Force
    Write-Host "Stopped $($entries.Count) swarm node(s)."
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

# --- Which nodes -----------------------------------------------------------------------------------------------
if ($Template) {
    $nodes = @([pscustomobject]@{ Name = '_template'; Port = 5048; Home = (Join-Path $homesRoot '_template') })
} else {
    $nodes = @()
    foreach ($dir in Get-ChildItem $homesRoot -Directory -ErrorAction SilentlyContinue | Sort-Object Name) {
        $info = Join-Path $dir.FullName 'swarm-node.json'
        if (-not (Test-Path $info)) { continue }
        $j = Get-Content $info -Raw | ConvertFrom-Json
        $nodes += [pscustomobject]@{ Name = $j.Name; Port = [int]$j.Port; Home = $dir.FullName }
    }
    if ($nodes.Count -eq 0) { throw "No swarm homes in $homesRoot. Make them with .\Build\setup-swarm-homes.ps1." }
    $nodes = @($nodes | Sort-Object Port)
    if ($Count -gt 0) { $nodes = @($nodes | Select-Object -First $Count) }
}

foreach ($n in $nodes) {
    $busy = Get-NetTCPConnection -State Listen -LocalPort $n.Port -ErrorAction SilentlyContinue
    if ($busy) { throw "Port $($n.Port) ($($n.Name)) is already in use by PID $($busy[0].OwningProcess). Stop it first (-Stop)." }
}

# --- Start -------------------------------------------------------------------------------------------------------
$exe = Join-Path $app 'ProcessorApplication.exe'
$dll = Join-Path $app 'ProcessorApplication.dll'
$started = @()
if (Test-Path $pidFile) { $started += @(Get-Content $pidFile -Raw | ConvertFrom-Json) }

$first = $true
foreach ($n in $nodes) {
    New-Item -ItemType Directory -Force -Path $n.Home | Out-Null
    $appArgs = @('--node-home', "`"$($n.Home)`"", '--environment', 'NodeTrackerSwarm', '--urls', "http://localhost:$($n.Port)", "--SettingsIngest:Mode=$Reingest")

    if (Test-Path $exe) {
        $proc = Start-Process -FilePath $exe -ArgumentList $appArgs -WorkingDirectory $n.Home -WindowStyle $(if ($Template) { 'Normal' } else { $Window }) -PassThru
    } else {
        $proc = Start-Process -FilePath 'dotnet' -ArgumentList (@("`"$dll`"") + $appArgs) -WorkingDirectory $n.Home -WindowStyle $(if ($Template) { 'Normal' } else { $Window }) -PassThru
    }
    $started += [pscustomobject]@{ Node = $n.Name; Pid = $proc.Id; Url = "http://localhost:$($n.Port)" }
    Write-Host ("{0,-12} PID {1,-6} http://localhost:{2}" -f $n.Name, $proc.Id, $n.Port) -ForegroundColor Green

    if ($first) { Start-Sleep -Seconds 5; $first = $false }
    elseif ($StartDelaySeconds -gt 0) { Start-Sleep -Milliseconds ([int]($StartDelaySeconds * 1000)) }
}

$started | ConvertTo-Json | Set-Content $pidFile -Encoding UTF8
Write-Host ''
if ($Template) { Write-Host 'Set up the template (admin user, settings), stop it (-Stop), then run .\Build\setup-swarm-homes.ps1.' }
Write-Host 'Stop all: .\Build\run-swarm.ps1 -Stop'
