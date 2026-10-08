# SPDX-License-Identifier: Apache-2.0
# SPDX-FileCopyrightText: 2026 Ventana Tools LLC
<#
.SYNOPSIS
Changes a host's id in the host-id registry, fixtures/hosts.json, and regenerates every file derived
from it (contract section 12.4, step 2).

.DESCRIPTION
Runs on Windows PowerShell 5.1 and PowerShell 7, from any folder of the SDK repository.

The script edits only the registry entry whose id is -From: its id, and its display name and package
file extension when -DisplayName and -PackageExtension are given. It never edits the registry's
reservedIds or fixtures/reserved-publishers.json, so the old id stays reserved. It then regenerates
every file derived from the registry:

- the Node SDK's byte copy, node/<package>/lib/hosts.json;
- the hosts and $schema members of every sample and template manifest (extension.json);
- the hostId default of every template (.template.config/template.json);
- the host-keyed schema folder, schemas/extensions/<id>/ (moved with git mv, each $id updated);
- the pin of hosts.json in the test project's FixturePins.txt;
- the package file pattern in .gitignore, when the package file extension changes.

Fixtures and test vectors use the test host id example-host, so no other fixture, pin or proof
changes. Finally it runs ProductNameConfinementTests, unless -SkipTests is given.

.PARAMETER From
The id of the registry entry to change.

.PARAMETER To
The new host id: lowercase letters and digits, words joined by single hyphens, at most 32 characters.

.PARAMETER DisplayName
The entry's new display name. Default: unchanged.

.PARAMETER PackageExtension
The entry's new package file extension, for example .<id>extension. Default: unchanged.

.PARAMETER SkipTests
Do not run ProductNameConfinementTests (eng/rename-product.ps1 runs them itself at the end).

.EXAMPLE
tools/Set-HostCodename.ps1 -From <current-id> -To <new-id> -DisplayName <New> -PackageExtension .<new-id>extension
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$From,
    [Parameter(Mandatory = $true)][string]$To,
    [string]$DisplayName = '',
    [string]$PackageExtension = '',
    [switch]$SkipTests
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$utf8 = New-Object System.Text.UTF8Encoding($false)

function Invoke-Native {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [string[]]$ArgumentList = @()
    )
    $saved = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $FilePath @ArgumentList | Out-Host
        $code = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $saved
    }
    if ($code -ne 0) { throw ('"{0} {1}" exited with code {2}.' -f $FilePath, ($ArgumentList -join ' '), $code) }
}

function Get-Tracked {
    $saved = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $lines = @(& git -C $root -c core.quotePath=false ls-files)
        $code = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $saved
    }
    if ($code -ne 0) { throw 'git ls-files failed; run the script inside the SDK repository.' }
    return , $lines
}

function Get-FullPath([string]$Relative) {
    return [System.IO.Path]::Combine($root, $Relative.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
}

function Read-Text([string]$Relative) {
    return $utf8.GetString([System.IO.File]::ReadAllBytes((Get-FullPath $Relative)))
}

# Writes a file only when its text changed, and says so.
function Write-Text([string]$Relative, [string]$Text) {
    $path = Get-FullPath $Relative
    $old = $utf8.GetString([System.IO.File]::ReadAllBytes($path))
    if ($old -ne $Text) {
        [System.IO.File]::WriteAllBytes($path, $utf8.GetBytes($Text))
        Write-Host ('  updated ' + $Relative)
    }
}

function Get-JsonString([string]$Text, [string]$Member) {
    $found = [regex]::Match($Text, '"' + [regex]::Escape($Member) + '"\s*:\s*"([^"]*)"')
    if ($found.Success) { return $found.Groups[1].Value }
    return $null
}

$hostIdGrammar = '^[a-z][a-z0-9]*(-[a-z0-9]+)*$'
if ($To -cnotmatch $hostIdGrammar -or $To.Length -gt 32) { throw ('-To is not a host id (contract section 2.4): ' + $To) }
if ($PackageExtension -and $PackageExtension -cnotmatch '^\.[a-z][a-z0-9]*$') {
    throw ('-PackageExtension must be a dot and lowercase letters and digits: ' + $PackageExtension)
}
if ($DisplayName -and ($DisplayName -match '["\\]' -or $DisplayName.Trim() -ne $DisplayName -or $DisplayName.Length -gt 64)) {
    throw ('-DisplayName must be plain text without quotes or backslashes: ' + $DisplayName)
}

# ---------------------------------------------------------------------------------------------
# The registry entry.
$registryPath = 'fixtures/hosts.json'
$registry = Read-Text $registryPath
$entryPattern = '\{[^{}]*"id"\s*:\s*"' + [regex]::Escape($From) + '"[^{}]*\}'
$entries = [regex]::Matches($registry, $entryPattern)
if ($entries.Count -ne 1) { throw ('fixtures/hosts.json has no single entry with the id ' + $From + '.') }
$entry = $entries[0].Value
foreach ($other in [regex]::Matches($registry, '"id"\s*:\s*"([^"]*)"')) {
    if ($other.Groups[1].Value -ceq $To -and $To -cne $From) { throw ('fixtures/hosts.json already has an entry with the id ' + $To + '.') }
}
$oldExtension = Get-JsonString $entry 'packageExtension'
$newEntry = [regex]::Replace($entry, '("id"\s*:\s*")[^"]*(")', ('${1}' + $To + '${2}'))
if ($DisplayName) {
    $newEntry = [regex]::Replace($newEntry, '("displayName"\s*:\s*")[^"]*(")', ('${1}' + $DisplayName + '${2}'))
}
if ($PackageExtension) {
    if ($null -eq $oldExtension) { throw ('The entry ' + $From + ' has no packageExtension to change.') }
    $newEntry = [regex]::Replace($newEntry, '("packageExtension"\s*:\s*")[^"]*(")', ('${1}' + $PackageExtension + '${2}'))
}
$registry = $registry.Substring(0, $entries[0].Index) + $newEntry + $registry.Substring($entries[0].Index + $entries[0].Length)

Write-Host ('Host id ' + $From + ' -> ' + $To)
Write-Text $registryPath $registry

# The old id stays reserved because the reserved lists are never edited here. The new id belongs on
# them as well (contract sections 2.3 and 3.5); they are edited by hand, in the same change.
$reservedIds = [regex]::Match($registry, '"reservedIds"\s*:\s*\[([^\]]*)\]').Groups[1].Value
$publishers = Read-Text 'fixtures/reserved-publishers.json'
foreach ($id in @($From, $To)) {
    if ($reservedIds -notmatch ('"' + [regex]::Escape($id) + '"')) {
        Write-Warning ($id + ' is not in reservedIds in fixtures/hosts.json; add it by hand (contract section 2.3).')
    }
    if ($publishers -notmatch ('"' + [regex]::Escape($id) + '"')) {
        Write-Warning ($id + ' is not in fixtures/reserved-publishers.json; add it by hand (contract section 3.5).')
    }
}

$tracked = Get-Tracked
$fromPattern = [regex]::Escape($From)

# ---------------------------------------------------------------------------------------------
# The Node SDK's byte copy.
foreach ($copy in @($tracked | Where-Object { $_ -match '^node/[^/]+/lib/hosts\.json$' })) {
    Write-Text $copy $registry
}

# Sample and template manifests: the hosts list and the host segment of $schema.
foreach ($manifest in @($tracked | Where-Object { $_ -match '^(samples|templates)/(.+/)?extension\.json$' })) {
    $text = Read-Text $manifest
    $text = [regex]::Replace($text, '("hosts"\s*:\s*\[)([^\]]*)(\])', {
            param($found)
            $found.Groups[1].Value + [regex]::Replace($found.Groups[2].Value, ('"' + $fromPattern + '"'), ('"' + $To + '"')) + $found.Groups[3].Value
        })
    $text = [regex]::Replace($text, '("\$schema"\s*:\s*")([^"]*)(")', {
            param($found)
            $found.Groups[1].Value + [regex]::Replace($found.Groups[2].Value, ('(?<=/)' + $fromPattern + '(?=/)'), $To) + $found.Groups[3].Value
        })
    Write-Text $manifest $text
}

# The templates' hostId default.
foreach ($template in @($tracked | Where-Object { $_ -match '^templates/.+/\.template\.config/template\.json$' })) {
    $text = Read-Text $template
    $text = [regex]::Replace($text, ('("hostId"\s*:\s*\{[^{}]*?"defaultValue"\s*:\s*")' + $fromPattern + '(")'), ('${1}' + $To + '${2}'))
    Write-Text $template $text
}

# The host-keyed schema folder.
$oldFolder = 'schemas/extensions/' + $From
$newFolder = 'schemas/extensions/' + $To
if ($From -cne $To -and @($tracked | Where-Object { $_.StartsWith($oldFolder + '/') }).Count -gt 0) {
    if (Test-Path -LiteralPath (Get-FullPath $newFolder)) { throw ($newFolder + ' already exists.') }
    Invoke-Native git @('-C', $root, 'mv', $oldFolder, $newFolder)
    Write-Host ('  renamed ' + $oldFolder + ' -> ' + $newFolder)
    foreach ($schema in @(Get-ChildItem -LiteralPath (Get-FullPath $newFolder) -File -Filter '*.json')) {
        $relative = $newFolder + '/' + $schema.Name
        $text = Read-Text $relative
        Write-Text $relative ([regex]::Replace($text, ('(/schemas/extensions/)' + $fromPattern + '(/)'), ('${1}' + $To + '${2}')))
    }
}

# The pin of hosts.json: SHA-256 over its LF bytes.
$algorithm = [System.Security.Cryptography.SHA256]::Create()
try {
    $pin = ([System.BitConverter]::ToString($algorithm.ComputeHash($utf8.GetBytes($registry.Replace("`r`n", "`n"))))).Replace('-', '').ToLowerInvariant()
} finally {
    $algorithm.Dispose()
}
foreach ($pins in @($tracked | Where-Object { $_ -match '(^|/)FixturePins\.txt$' })) {
    $text = Read-Text $pins
    Write-Text $pins ([regex]::Replace($text, '(?m)^[0-9a-f]{64}(?= text hosts\.json\r?$)', $pin))
}

# The ignored package files.
if ($PackageExtension -and $oldExtension -and $PackageExtension -cne $oldExtension) {
    foreach ($ignore in @($tracked | Where-Object { $_ -match '(^|/)\.gitignore$' })) {
        $text = Read-Text $ignore
        Write-Text $ignore ([regex]::Replace($text, ('(?m)^\*' + [regex]::Escape($oldExtension) + '(?=\r?$)'), ('*' + $PackageExtension)))
    }
}

# ---------------------------------------------------------------------------------------------
if (-not $SkipTests) {
    $solutions = @(Get-ChildItem -LiteralPath $root -Filter '*.slnx' -File)
    if ($solutions.Count -ne 1) { throw 'Expected exactly one .slnx file at the repository root.' }
    $family = [System.IO.Path]::GetFileNameWithoutExtension($solutions[0].Name)
    $tests = [System.IO.Path]::Combine($root, 'tests', $family + '.Tests', $family + '.Tests.csproj')
    Invoke-Native dotnet @('test', $tests, '-c', 'Release', '-nologo', '--filter', 'FullyQualifiedName~ProductNameConfinementTests|FullyQualifiedName~FixturePinTests')
}
Write-Host 'Done.'
