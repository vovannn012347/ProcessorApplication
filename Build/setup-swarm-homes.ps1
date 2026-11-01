<#
.SYNOPSIS
    Creates the swarm node homes from the swarm template node: each a copy of the template's database (with the
    admin user you created there), its own reserved name, port, signalling endpoint and random seed nodes.

.DESCRIPTION
    1. Build NodeTrackerSwarm once (Visual Studio or .\Build\run-swarm.ps1 -Build). The build seeds
       bin\<platform>\TrackerSwarm\nodes\_template\ with the presets from <project>\configFiles\TrackerSwarm.
    2. Start the template node once (launch profile "µProcessorSwarmTemplate", or .\Build\run-swarm.ps1 -Template),
       create the admin user, adjust whatever should be the same on every node, then STOP it.
    3. Run this script. For every node it makes nodes\<name>\ with:
         - a copy of the template home WITHOUT anything that must be unique per node:
             Module_Data\P2P (identity, consensus, peers), Module_Data\FileSharing, Module_Data\Portal (tunnel keys,
             cloudflared), Module_Data\ScriptProvisioning, data\p2p, the settings re-ingest state, settings files;
           kept: the databases (admin user, settings) and Module_Data\Main (the server key the database needs);
         - Modules\P2PModule\appsettings.P2P.NodeTrackerSwarm.json with
             EnforcedNodeIdentifier = the node's reserved name (used when its identity is created on first start),
             Endpoints              = http://localhost:<its port>,
             SeedEndpoints          = the previous node in a ring (so the graph is always connected) + random others;
         - optionally Modules\ProviderlessModule\appsettings.Providerless.NodeTrackerSwarm.json with the same name as
           Clinic ID (-ReserveClinicIds);
         - swarm-node.json (name, port, url, seeds) that run-swarm.ps1 starts it from.
       These settings files are layered over the shared appsettings.*.NodeTrackerSwarm.json of the program folder
       and applied to the node's database by settings re-ingest on start.
    Existing homes keep their data and only get their settings files rewritten, unless -Force (wipe and copy again).

    Note: every copy shares the template's server root key (Module_Data\Main\keys) - needed to open the copied
    database. Fine for tests; real nodes each create their own.

.EXAMPLE
    .\Build\setup-swarm-homes.ps1 -Count 50                                   # swarm-001..swarm-050 on ports 6101..6150, 2 seeds each
    .\Build\setup-swarm-homes.ps1 -Count 50 -SeedsPerNode 3 -RandomSeed 42    # 3 seeds each, reproducible choice
    .\Build\setup-swarm-homes.ps1 -NamesFile .\Build\swarm-names.txt          # reserved names, one per line (count = number of names)
    .\Build\setup-swarm-homes.ps1 -Count 50 -ExtraSeeds http://localhost:5044 # every node also seeds the test-mesh Tracker1
    .\Build\setup-swarm-homes.ps1 -Count 50 -Force                            # recreate all 50 from the template
#>
[CmdletBinding()]
param(
    [int] $Count = 10,
    [int] $FirstPort = 6101,

    # Seeds per node: 1 = ring only; more = ring + random others (a denser mesh).
    [ValidateRange(1, 20)]
    [int] $SeedsPerNode = 2,

    # Signalling endpoints every node also seeds (e.g. the test-mesh trackers).
    [string[]] $ExtraSeeds = @(),

    # Reserved node names. Either -Names, -NamesFile (one per line, # comments allowed) or generated <NamePrefix>-001...
    [string[]] $Names = @(),
    [string] $NamesFile = '',
    [string] $NamePrefix = 'swarm',

    # Also use each node's name as its Clinic ID (ProviderlessModule; becomes the tunnel subdomain if a tunnel is used).
    [switch] $ReserveClinicIds,

    # 0 = different random seeds each run; anything else = the same choice every time.
    [int] $RandomSeed = 0,

    [string] $Rid = 'win-x64',

    # Wipe existing homes and copy them from the template again.
    [switch] $Force
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$groupRoot = Join-Path $root "bin\$Rid\TrackerSwarm"
$homesRoot = Join-Path $groupRoot 'nodes'
$template = Join-Path $homesRoot '_template'
$listFile = Join-Path $groupRoot 'swarm-homes.json'

# --- Template ------------------------------------------------------------------------------------------------
$templateDb = Join-Path $template 'medicalsystem.db'
if (-not (Test-Path $templateDb)) {
    throw "No template database ($templateDb). Build NodeTrackerSwarm, start the template node once (profile µProcessorSwarmTemplate or run-swarm.ps1 -Template), create the admin user, stop it, then run this again."
}
try {
    $fs = [System.IO.File]::Open($templateDb, 'Open', 'Read', 'None')
    $fs.Close()
} catch {
    throw "The template database is in use - stop the template node first ($templateDb)."
}

# Relative paths (from the home root) that are never copied: everything that must be unique per node.
$excludedDirs = @('Module_Data\P2P', 'Module_Data\FileSharing', 'Module_Data\Portal', 'Module_Data\ScriptProvisioning', 'data\p2p', 'logs')
$excludedFiles = @('Module_Data\Main\settings_ingest_state.json', 'swarm-node.json')

function Test-Excluded([string] $rel, [bool] $isDir) {
    foreach ($d in $excludedDirs) {
        if ($rel -ieq $d -or $rel.StartsWith($d + '\', [System.StringComparison]::OrdinalIgnoreCase)) { return $true }
    }
    if (-not $isDir) {
        if ($excludedFiles -icontains $rel) { return $true }
        if ((Split-Path $rel -Leaf) -like 'appsettings*.json') { return $true }
    }
    return $false
}

function Copy-Template([string] $dest) {
    $base = (Resolve-Path $template).Path.TrimEnd('\')
    foreach ($item in Get-ChildItem $template -Recurse -Force) {
        $rel = $item.FullName.Substring($base.Length).TrimStart('\')
        if (Test-Excluded $rel $item.PSIsContainer) { continue }
        $target = Join-Path $dest $rel
        if ($item.PSIsContainer) {
            New-Item -ItemType Directory -Force -Path $target | Out-Null
        } else {
            New-Item -ItemType Directory -Force -Path (Split-Path $target) | Out-Null
            Copy-Item $item.FullName $target -Force
        }
    }
}

# --- Names ---------------------------------------------------------------------------------------------------
if ($NamesFile) {
    $Names = @(Get-Content $NamesFile | ForEach-Object { ($_ -replace '#.*$', '').Trim() } | Where-Object { $_ })
}
if ($Names.Count -gt 0) {
    $Count = $Names.Count
} else {
    $Names = @(1..$Count | ForEach-Object { '{0}-{1:D3}' -f $NamePrefix, $_ })
}
$Names = @($Names | ForEach-Object { $_.ToLowerInvariant() })
foreach ($n in $Names) {
    if ($n -notmatch '^[a-z0-9][a-z0-9-]{0,62}$') { throw "Bad node name '$n': use a-z, 0-9 and '-', start with a letter or digit, at most 63 characters." }
    if ($n -eq '_template') { throw "'_template' is reserved for the template node." }
}
$dupes = $Names | Group-Object | Where-Object { $_.Count -gt 1 } | ForEach-Object { $_.Name }
if ($dupes) { throw "Duplicate node names: $($dupes -join ', ')" }
if ($Count -lt 2) { throw 'A swarm needs at least 2 nodes.' }
$seedCount = [Math]::Min($SeedsPerNode, $Count - 1)

# --- Seeds: ring predecessor (connected graph) + random others ---------------------------------------------------
$rng = if ($RandomSeed -ne 0) { New-Object System.Random $RandomSeed } else { New-Object System.Random }
$nodes = @()
for ($i = 0; $i -lt $Count; $i++) {
    $port = $FirstPort + $i
    $nodes += [pscustomobject]@{ Name = $Names[$i]; Port = $port; Url = "http://localhost:$port"; Seeds = @() }
}
for ($i = 0; $i -lt $Count; $i++) {
    $picked = New-Object System.Collections.Generic.List[int]
    $picked.Add(($i - 1 + $Count) % $Count)
    while ($picked.Count -lt $seedCount) {
        $j = $rng.Next($Count)
        if ($j -ne $i -and -not $picked.Contains($j)) { $picked.Add($j) }
    }
    $nodes[$i].Seeds = @($picked | ForEach-Object { $nodes[$_].Url }) + @($ExtraSeeds)
}

# --- Homes -----------------------------------------------------------------------------------------------------
function Write-Json([string] $path, $object) {
    New-Item -ItemType Directory -Force -Path (Split-Path $path) | Out-Null
    $json = $object | ConvertTo-Json -Depth 6
    [System.IO.File]::WriteAllText($path, $json, (New-Object System.Text.UTF8Encoding $false))
}

$created = 0; $updated = 0
foreach ($n in $nodes) {
    $nodeHome = Join-Path $homesRoot $n.Name
    if ($Force -and (Test-Path $nodeHome)) { Remove-Item $nodeHome -Recurse -Force }

    if (-not (Test-Path $nodeHome)) {
        New-Item -ItemType Directory -Force -Path $nodeHome | Out-Null
        Copy-Template $nodeHome
        $created++
    } else {
        $updated++
        if (Test-Path (Join-Path $nodeHome 'Module_Data\P2P\identity\node_identity.json')) {
            Write-Verbose "$($n.Name): already has a P2P identity; a changed reserved name only applies to a new identity."
        }
    }

    Write-Json (Join-Path $nodeHome 'Modules\P2PModule\appsettings.P2P.NodeTrackerSwarm.json') ([ordered]@{
        P2PSettings = [ordered]@{
            EnforcedNodeIdentifier = $n.Name
            Endpoints              = @($n.Url)
            SeedEndpoints          = @($n.Seeds)
        }
    })
    if ($ReserveClinicIds) {
        Write-Json (Join-Path $nodeHome 'Modules\ProviderlessModule\appsettings.Providerless.NodeTrackerSwarm.json') ([ordered]@{
            PortalAccessSettings = [ordered]@{ EnforcedPCIdentifier = $n.Name }
        })
    }
    Write-Json (Join-Path $nodeHome 'swarm-node.json') ([ordered]@{ Name = $n.Name; Port = $n.Port; Url = $n.Url; Seeds = @($n.Seeds) })
}

Write-Json $listFile @($nodes | ForEach-Object { [ordered]@{ Name = $_.Name; Port = $_.Port; Url = $_.Url } })

Write-Host "Swarm homes in $homesRoot : $created created from the template, $updated updated (settings only)." -ForegroundColor Green
Write-Host "Nodes: $($Names[0]) .. $($Names[-1]), ports $FirstPort..$($FirstPort + $Count - 1), $seedCount seed(s) each$(if ($ExtraSeeds.Count) { ' + ' + ($ExtraSeeds -join ', ') })."
Write-Host 'Start them: .\Build\run-swarm.ps1      (first start: -Reingest Overwrite applies every settings file to the copied databases)'
