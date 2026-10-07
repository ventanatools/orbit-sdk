# SPDX-License-Identifier: Apache-2.0
# SPDX-FileCopyrightText: 2026 Ventana Tools LLC
<#
.SYNOPSIS
Renames the product throughout the SDK repository: the scripted, mechanical rename of contract
section 12.4.

.DESCRIPTION
eng/rename-product.ps1 -From <Current> -To <New> [-HostId <id>] [-PackageExtension <ext>]

Runs on Windows PowerShell 5.1 and PowerShell 7, at the root of a clean checkout (it refuses to run
on a tree with uncommitted or untracked changes). Every token derives from -From and -To; this
script names no product.

1. Renames, with git mv, every folder and file whose name contains the package family
   VentanaTools.<Current>.Extensions or the Node package folder <current>-extensions.
2. Replaces, in tracked text files: VentanaTools.<Current>.Extensions (and its lowercase form), then
   @ventanatools/<current>-extensions and <current>-extensions, then the whole token <current>-ext
   (the tool command and template short names), then the whole token <current>-sdk (the
   repository, which is renamed with the product).
3. Runs tools/Set-HostCodename.ps1 for the registry entry in fixtures/hosts.json and every file
   derived from it.
4. Replaces, in the documentation (every Markdown file, NOTICE and its Node copy, and package descriptions), the
   display name, the old host id where it stands as a whole token or a URL or path segment, and the
   old package file extension.
5. Replaces what is left of the old name in every other tracked text file (test literals and
   comments, build comments), keeping its case.
Then it runs ProductNameConfinementTests, unless -SkipTests is given.

It never edits fixtures/reserved-publishers.json, the reservedIds of fixtures/hosts.json or of the
Node SDK's copy, CHANGELOG.md or docs/design/: the design documents name both products and are
edited by hand in the same change, and the old name stays reserved. It prints every renamed path and
the number of replacements per token.

.PARAMETER From
The current product name, as in the package family (one capitalized word).

.PARAMETER To
The new product name (one capitalized word). It becomes the display name.

.PARAMETER HostId
The new host id. Default: -To in lowercase.

.PARAMETER PackageExtension
The new package file extension. Default: a dot, the new host id and "extension".

.PARAMETER SkipTests
Do not run ProductNameConfinementTests at the end.

.EXAMPLE
eng/rename-product.ps1 -From <Current> -To <New>
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$From,
    [Parameter(Mandatory = $true)][string]$To,
    [string]$HostId = '',
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

function Get-NativeOutput {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [string[]]$ArgumentList = @()
    )
    $saved = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $lines = @(& $FilePath @ArgumentList)
        $code = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $saved
    }
    if ($code -ne 0) { throw ('"{0} {1}" exited with code {2}.' -f $FilePath, ($ArgumentList -join ' '), $code) }
    return , $lines
}

function Get-Tracked {
    return Get-NativeOutput git @('-C', $root, '-c', 'core.quotePath=false', 'ls-files')
}

function Get-FullPath([string]$Relative) {
    return [System.IO.Path]::Combine($root, $Relative.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
}

# Files no text replacement touches: the rename scripts, the history, the design documents, and the
# fixtures (the codename script edits the registry entry; the reserved lists keep the old name).
function Test-Protected([string]$Relative) {
    return $Relative -eq 'eng/rename-product.ps1' -or $Relative -eq 'tools/Set-HostCodename.ps1' -or
        $Relative -eq 'CHANGELOG.md' -or $Relative.StartsWith('docs/design/') -or $Relative.StartsWith('fixtures/') -or
        $Relative -match '^node/[^/]+/lib/hosts\.json$'
}

# A token replacement: a regular expression, its replacement, and a count of what it replaced.
function New-Token([string]$Name, [string]$Pattern, [string]$Replacement) {
    return [pscustomobject]@{ Name = $Name; Pattern = $Pattern; Replacement = $Replacement; Count = 0 }
}

# Applies tokens, in order, to every tracked text file the filter selects; returns nothing.
function Update-Files([object[]]$Tokens, [scriptblock]$Select) {
    foreach ($file in (Get-Tracked)) {
        if (Test-Protected $file) { continue }
        if (-not (& $Select $file)) { continue }
        $path = Get-FullPath $file
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }
        $bytes = [System.IO.File]::ReadAllBytes($path)
        if ([System.Array]::IndexOf($bytes, [byte]0) -ge 0) { continue }
        $bom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
        $start = 0
        if ($bom) { $start = 3 }
        $original = $utf8.GetString($bytes, $start, $bytes.Length - $start)
        $text = $original
        foreach ($token in $Tokens) {
            $found = [regex]::Matches($text, $token.Pattern).Count
            if ($found -gt 0) {
                $token.Count += $found
                $text = [regex]::Replace($text, $token.Pattern, $token.Replacement)
            }
        }
        if ($text -cne $original) {
            $out = $utf8.GetBytes($text)
            if ($bom) { $out = [byte[]](@(0xEF, 0xBB, 0xBF) + $out) }
            [System.IO.File]::WriteAllBytes($path, $out)
        }
    }
}

function Write-Counts([object[]]$Tokens) {
    foreach ($token in $Tokens) { Write-Host ('  {0,6}  {1}' -f $token.Count, $token.Name) }
}

# ---------------------------------------------------------------------------------------------
$nameGrammar = '^[A-Z][A-Za-z0-9]*$'
if ($From -cnotmatch $nameGrammar -or $To -cnotmatch $nameGrammar) {
    throw '-From and -To must each be one capitalized word of letters and digits, as in the package family.'
}
if ($From -ceq $To) { throw '-From and -To are the same.' }
$oldLower = $From.ToLowerInvariant()
$newLower = $To.ToLowerInvariant()
$oldFamily = 'VentanaTools.' + $From + '.Extensions'
$newFamily = 'VentanaTools.' + $To + '.Extensions'
if (-not (Test-Path -LiteralPath (Get-FullPath ($oldFamily + '.slnx')))) {
    throw ('This is not the SDK repository of ' + $From + ': ' + $oldFamily + '.slnx is missing.')
}
$status = Get-NativeOutput git @('-C', $root, 'status', '--porcelain')
if ($status.Count -gt 0) { throw 'The working tree has changes. Commit them, or set them aside, before renaming.' }

# The registry entry of the current product.
$registry = [System.IO.File]::ReadAllText((Get-FullPath 'fixtures/hosts.json')) | ConvertFrom-Json
$entries = @($registry.hosts | Where-Object { $_.displayName -ceq $From -or $_.id -ceq $oldLower })
if ($entries.Count -ne 1) { throw ('fixtures/hosts.json has no single entry for ' + $From + '.') }
$oldHost = [string]$entries[0].id
$oldExt = ''
if ($entries[0].PSObject.Properties.Name -contains 'packageExtension') { $oldExt = [string]$entries[0].packageExtension }
$newHost = $HostId
if (-not $newHost) { $newHost = $newLower }
$newExt = $PackageExtension
if (-not $newExt) { $newExt = '.' + $newHost + 'extension' }

Write-Host ('Package family:  ' + $oldFamily + ' -> ' + $newFamily)
Write-Host ('Host id:         ' + $oldHost + ' -> ' + $newHost)
Write-Host ('Package files:   ' + $oldExt + ' -> ' + $newExt)

# ---------------------------------------------------------------------------------------------
Write-Host ''
Write-Host '1. Folders and files'
function Get-RenamedSegment([string]$Segment) {
    return $Segment.Replace($oldFamily, $newFamily).Replace($oldLower + '-extensions', $newLower + '-extensions')
}
while ($true) {
    $next = $null
    foreach ($file in (Get-Tracked)) {
        $segments = $file.Split('/')
        for ($i = 0; $i -lt $segments.Length; $i++) {
            if ((Get-RenamedSegment $segments[$i]) -cne $segments[$i]) {
                $next = ($segments[0..$i] -join '/')
                break
            }
        }
        if ($next) { break }
    }
    if (-not $next) { break }
    $parts = $next.Split('/')
    $parts[$parts.Length - 1] = Get-RenamedSegment $parts[$parts.Length - 1]
    $target = $parts -join '/'
    Invoke-Native git @('-C', $root, 'mv', $next, $target)
    Write-Host ('  ' + $next + ' -> ' + $target)
}

# ---------------------------------------------------------------------------------------------
Write-Host ''
Write-Host '2. Package family, Node package, tool command and repository'
$escapedOldLower = [regex]::Escape($oldLower)
$familyTokens = @(
    (New-Token $oldFamily ([regex]::Escape($oldFamily)) $newFamily),
    (New-Token $oldFamily.ToLowerInvariant() ([regex]::Escape($oldFamily.ToLowerInvariant())) $newFamily.ToLowerInvariant()),
    (New-Token ('@ventanatools/' + $oldLower + '-extensions') ([regex]::Escape('@ventanatools/' + $oldLower + '-extensions')) ('@ventanatools/' + $newLower + '-extensions')),
    (New-Token ($oldLower + '-extensions') ($escapedOldLower + '-extensions') ($newLower + '-extensions')),
    (New-Token ($oldLower + '-ext') ('(?<![A-Za-z0-9])' + $escapedOldLower + '-ext(?![A-Za-z0-9])') ($newLower + '-ext')),
    (New-Token ($oldLower + '-sdk') ('(?<![A-Za-z0-9])' + $escapedOldLower + '-sdk(?![A-Za-z0-9])') ($newLower + '-sdk'))
)
Update-Files $familyTokens { param($file) $true }
Write-Counts $familyTokens

# ---------------------------------------------------------------------------------------------
Write-Host ''
Write-Host '3. The registry entry and its derived files'
& ([System.IO.Path]::Combine($root, 'tools', 'Set-HostCodename.ps1')) -From $oldHost -To $newHost -DisplayName $To -PackageExtension $newExt -SkipTests

# ---------------------------------------------------------------------------------------------
# The old name, its host id and its package file extension, in the order that keeps each pattern
# from matching inside an earlier one's result.
function Get-NameTokens([bool]$Everywhere) {
    $tokens = @()
    if ($oldExt) {
        $tokens += New-Token $oldExt ([regex]::Escape($oldExt)) $newExt
        if ($Everywhere) { $tokens += New-Token $oldExt.TrimStart('.') ([regex]::Escape($oldExt.TrimStart('.'))) $newExt.TrimStart('.') }
    }
    $tokens += New-Token ($oldHost + ' (host id)') ('(?<![A-Za-z0-9])' + [regex]::Escape($oldHost) + '(?![A-Za-z0-9])') $newHost
    if ($Everywhere) {
        $tokens += New-Token $From ([regex]::Escape($From)) $To
        $tokens += New-Token $From.ToUpperInvariant() ([regex]::Escape($From.ToUpperInvariant())) $To.ToUpperInvariant()
        $tokens += New-Token ($oldLower + ' (anywhere)') $escapedOldLower $newLower
    } else {
        $tokens += New-Token $From ('(?<![A-Za-z0-9])' + [regex]::Escape($From) + '(?![a-z0-9])') $To
    }
    return , $tokens
}

Write-Host ''
Write-Host '4. Documentation: display name, host id and package file extension'
$docsTokens = Get-NameTokens $false
# The Node package carries a copy of NOTICE (RepositoryRulesTests keeps the two equal).
Update-Files $docsTokens { param($file) $file.EndsWith('.md') -or $file -eq 'NOTICE' -or $file -match '^node/[^/]+/NOTICE$' }
# Package descriptions are display text too.
$descriptionTokens = Get-NameTokens $false
foreach ($file in @((Get-Tracked) | Where-Object { $_.EndsWith('.csproj') -and -not (Test-Protected $_) })) {
    $path = Get-FullPath $file
    $text = $utf8.GetString([System.IO.File]::ReadAllBytes($path))
    $updated = [regex]::Replace($text, '(<Description>)([^<]*)(</Description>)', {
            param($found)
            $inner = $found.Groups[2].Value
            foreach ($token in $descriptionTokens) {
                $token.Count += [regex]::Matches($inner, $token.Pattern).Count
                $inner = [regex]::Replace($inner, $token.Pattern, $token.Replacement)
            }
            $found.Groups[1].Value + $inner + $found.Groups[3].Value
        })
    if ($updated -cne $text) { [System.IO.File]::WriteAllBytes($path, $utf8.GetBytes($updated)) }
}
Write-Counts $docsTokens
Write-Host '  in package descriptions:'
Write-Counts $descriptionTokens

Write-Host ''
Write-Host '5. What is left of the old name elsewhere (test literals, comments)'
$restTokens = Get-NameTokens $true
Update-Files $restTokens { param($file) $true }
Write-Counts $restTokens

# ---------------------------------------------------------------------------------------------
if (-not $SkipTests) {
    Write-Host ''
    Write-Host 'ProductNameConfinementTests'
    $tests = [System.IO.Path]::Combine($root, 'tests', $newFamily + '.Tests', $newFamily + '.Tests.csproj')
    Invoke-Native dotnet @('test', $tests, '-c', 'Release', '-nologo', '--filter', 'FullyQualifiedName~ProductNameConfinementTests|FullyQualifiedName~FixturePinTests')
}

Write-Host ''
Write-Host ('Renamed ' + $From + ' to ' + $To + '. Now edit docs/design/ by hand (contract section 12.4), add a CHANGELOG entry, and review the diff.')
