# SPDX-License-Identifier: Apache-2.0
# SPDX-FileCopyrightText: 2026 Ventana Tools LLC
<#
.SYNOPSIS
Builds the SDK, packs every package, and builds the samples against the packages it just packed.

.DESCRIPTION
Runs unchanged on Windows PowerShell 5.1 and PowerShell 7.

1. Builds the solution with one package version, by default 0.1.0-dev.<UTC yyyyMMddHHmmss>
   (-p:VentanaExtensionsVersion=<version>), so a package from an earlier build can never stand in
   for the one just built.
2. Packs, into artifacts/packages and in this order: the author and Testing libraries, the
   templates, the Node SDK tarball (npm pack), then the tool, which carries the other three.
3. Restores and builds every .NET sample against those packages, into a fresh per-run folder,
   artifacts/consumer-packages/<run id>.
4. Writes artifacts/build/state.json, which tools/verify.ps1 reads.

No name of the product is written here: the package family comes from the solution file's name.

.PARAMETER Configuration
Debug or Release (the default).

.PARAMETER PackageVersion
The version to stamp on every package instead of the dev stamp.

.PARAMETER RepositoryVersion
Stamp the version in Directory.Build.props (VentanaExtensionsVersion) instead of a dev stamp. The
release workflow uses this.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$PackageVersion = '',
    [switch]$RepositoryVersion
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$onWindows = [System.Environment]::OSVersion.Platform -eq [System.PlatformID]::Win32NT

# Runs a program, shows its output, and throws when it exits with another code than 0. Native
# programs write warnings to standard error, which must never stop the script by itself.
function Invoke-Native {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [string[]]$ArgumentList = @(),
        [string]$WorkingDirectory = ''
    )
    $saved = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    if ($WorkingDirectory) { Push-Location -LiteralPath $WorkingDirectory }
    try {
        & $FilePath @ArgumentList | Out-Host
        $code = $LASTEXITCODE
    } finally {
        if ($WorkingDirectory) { Pop-Location }
        $ErrorActionPreference = $saved
    }
    if ($code -ne 0) {
        throw ('"{0} {1}" exited with code {2}.' -f $FilePath, ($ArgumentList -join ' '), $code)
    }
}

function Write-Step([string]$Text) {
    Write-Host ''
    Write-Host ('== ' + $Text)
}

function Write-Utf8File([string]$Path, [string]$Text) {
    [System.IO.File]::WriteAllText($Path, $Text, (New-Object System.Text.UTF8Encoding($false)))
}

function Remove-Folder([string]$Path) {
    if (Test-Path -LiteralPath $Path) {
        try {
            Remove-Item -LiteralPath $Path -Recurse -Force
        } catch {
            Write-Warning ('Could not delete ' + $Path + ' (a build server may still hold a file); it is left in place.')
        }
    }
}

$npm = 'npm'
if ($onWindows) { $npm = 'npm.cmd' }

# The package family is the solution's name: VentanaTools.<Product>.Extensions.
$solutions = @(Get-ChildItem -LiteralPath $root -Filter '*.slnx' -File)
if ($solutions.Count -ne 1) { throw 'Expected exactly one .slnx file at the repository root.' }
$solution = $solutions[0].FullName
$family = [System.IO.Path]::GetFileNameWithoutExtension($solution)

# The version.
$props = [System.IO.File]::ReadAllText([System.IO.Path]::Combine($root, 'Directory.Build.props'))
$match = [regex]::Match($props, '<VentanaExtensionsVersion>([^<]+)</VentanaExtensionsVersion>')
if (-not $match.Success) { throw 'Directory.Build.props has no VentanaExtensionsVersion.' }
$propsVersion = $match.Groups[1].Value.Trim()
$stamp = [System.DateTime]::UtcNow.ToString('yyyyMMddHHmmss', [System.Globalization.CultureInfo]::InvariantCulture)
if ($PackageVersion) {
    $version = $PackageVersion
} elseif ($RepositoryVersion) {
    $version = $propsVersion
} else {
    $version = $propsVersion.Split('-')[0] + '-dev.' + $stamp
}
if ($version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') { throw ('Not a package version: ' + $version) }
$runId = $stamp
$versionProperty = '-p:VentanaExtensionsVersion=' + $version

$artifacts = [System.IO.Path]::Combine($root, 'artifacts')
$packages = [System.IO.Path]::Combine($artifacts, 'packages')
$consumerRoot = [System.IO.Path]::Combine($artifacts, 'consumer-packages')
$consumer = [System.IO.Path]::Combine($consumerRoot, $runId)
$stateFolder = [System.IO.Path]::Combine($artifacts, 'build')
$nodeStageRoot = [System.IO.Path]::Combine($artifacts, 'node-pack')

Write-Host ('Package family: ' + $family)
Write-Host ('Version:        ' + $version)
Write-Host ('Configuration:  ' + $Configuration)

# Earlier dev builds and their consumer folders are not needed once a new build starts.
New-Item -ItemType Directory -Force -Path $packages, $stateFolder | Out-Null
Get-ChildItem -LiteralPath $packages -File |
    Where-Object { $_.Name -match '-dev\.\d{14}\.(nupkg|snupkg|tgz)$' } |
    ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }
if (Test-Path -LiteralPath $consumerRoot) {
    Get-ChildItem -LiteralPath $consumerRoot -Directory | ForEach-Object { Remove-Folder $_.FullName }
}
Remove-Folder $nodeStageRoot
New-Item -ItemType Directory -Force -Path $consumer | Out-Null

Push-Location -LiteralPath $root
try {
    Write-Step 'Restore and build the solution'
    Invoke-Native dotnet @('restore', $solution, $versionProperty)
    Invoke-Native dotnet @('build', $solution, '-c', $Configuration, '--no-restore', '-nologo', $versionProperty)

    Write-Step 'Pack the libraries'
    foreach ($project in @(
            [System.IO.Path]::Combine($root, 'src', $family, $family + '.csproj'),
            [System.IO.Path]::Combine($root, 'src', $family + '.Testing', $family + '.Testing.csproj'))) {
        Invoke-Native dotnet @('pack', $project, '-c', $Configuration, '--no-build', '-nologo', '-o', $packages, $versionProperty)
    }

    Write-Step 'Pack the templates'
    Invoke-Native dotnet @('pack', [System.IO.Path]::Combine($root, 'templates', $family + '.Templates.csproj'),
        '-c', $Configuration, '--no-build', '-nologo', '-o', $packages, $versionProperty)

    Write-Step 'Pack the Node SDK'
    # npm pack takes the version from package.json, so a copy of the package gets the build's version.
    $nodeManifests = @(Get-ChildItem -LiteralPath ([System.IO.Path]::Combine($root, 'node')) -Directory |
            Where-Object { Test-Path -LiteralPath ([System.IO.Path]::Combine($_.FullName, 'package.json')) })
    if ($nodeManifests.Count -ne 1) { throw 'Expected exactly one Node package under node/.' }
    $nodeSource = $nodeManifests[0].FullName
    $nodeStage = [System.IO.Path]::Combine($nodeStageRoot, $runId)
    New-Item -ItemType Directory -Force -Path $nodeStage | Out-Null
    Get-ChildItem -LiteralPath $nodeSource -Force |
        Where-Object { $_.Name -ne 'node_modules' } |
        ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $nodeStage -Recurse -Force }
    $nodePackageJson = [System.IO.Path]::Combine($nodeStage, 'package.json')
    $nodeText = [System.IO.File]::ReadAllText($nodePackageJson)
    $versionPattern = New-Object System.Text.RegularExpressions.Regex('"version"\s*:\s*"[^"]*"')
    $nodeText = $versionPattern.Replace($nodeText, ('"version": "' + $version + '"'), 1)
    Write-Utf8File $nodePackageJson $nodeText
    Invoke-Native $npm @('pack', '--ignore-scripts', '--pack-destination', $packages) $nodeStage
    $tarball = $family.ToLowerInvariant().Replace('.', '-') + '-' + $version + '.tgz'
    if (-not (Test-Path -LiteralPath ([System.IO.Path]::Combine($packages, $tarball)))) {
        throw ('npm pack did not write ' + $tarball + '.')
    }
    Remove-Folder $nodeStageRoot

    Write-Step 'Pack the tool'
    Invoke-Native dotnet @('pack', [System.IO.Path]::Combine($root, 'src', $family + '.Tool', $family + '.Tool.csproj'),
        '-c', $Configuration, '--no-build', '-nologo', '-o', $packages, $versionProperty)

    foreach ($expected in @(
            ($family + '.' + $version + '.nupkg'), ($family + '.' + $version + '.snupkg'),
            ($family + '.Testing.' + $version + '.nupkg'), ($family + '.Testing.' + $version + '.snupkg'),
            ($family + '.Templates.' + $version + '.nupkg'), ($family + '.Tool.' + $version + '.nupkg'), $tarball)) {
        if (-not (Test-Path -LiteralPath ([System.IO.Path]::Combine($packages, $expected)))) {
            throw ('Missing package: ' + $expected)
        }
    }

    Write-Step 'Build the samples against the new packages'
    $samples = @(Get-ChildItem -LiteralPath ([System.IO.Path]::Combine($root, 'samples')) -Directory)
    foreach ($sample in $samples) {
        $projects = @(Get-ChildItem -LiteralPath $sample.FullName -Filter '*.csproj' -File)
        $testsFolder = [System.IO.Path]::Combine($sample.FullName, 'tests')
        if (Test-Path -LiteralPath $testsFolder) {
            $projects += @(Get-ChildItem -LiteralPath $testsFolder -Filter '*.csproj' -File)
        }
        foreach ($project in $projects) {
            Invoke-Native dotnet @('restore', $project.FullName, '--packages', $consumer, $versionProperty)
            Invoke-Native dotnet @('build', $project.FullName, '-c', $Configuration, '--no-restore', '-nologo', $versionProperty)
        }
    }

    $state = [ordered]@{
        family           = $family
        version          = $version
        runId            = $runId
        configuration    = $Configuration
        packages         = $packages
        consumerPackages = $consumer
    }
    Write-Utf8File ([System.IO.Path]::Combine($stateFolder, 'state.json')) (($state | ConvertTo-Json) + "`n")
} finally {
    Pop-Location
}

Write-Step 'Done'
Write-Host ('Packages in ' + $packages + ', version ' + $version + '.')
Write-Host 'Next: tools/verify.ps1'
