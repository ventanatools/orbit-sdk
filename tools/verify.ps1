# SPDX-License-Identifier: Apache-2.0
# SPDX-FileCopyrightText: 2026 Ventana Tools LLC
<#
.SYNOPSIS
Verifies the bits tools/build.ps1 just built.

.DESCRIPTION
Runs unchanged on Windows PowerShell 5.1 and PowerShell 7. Run tools/build.ps1 first; this script
reads the version and run id it wrote to artifacts/build/state.json.

1. Repository lints: no private or personal residue in tracked files; no host id or product name
   in samples or templates outside the places allowed; no raw private-use glyph in the docs, the
   libraries, the tool, the tests, samples, templates or the Node SDK (XML documentation shows it as an
   empty box); the SPDX header of every source file matches its folder's licence.
2. The library, Testing, tool and schema tests.
3. The Node SDK's and the Photoshop sample's npm tests.
4. Every .NET sample against the freshly packed packages: its resolved author library has the same
   SHA-256 as the one in the new .nupkg, and its tests pass.
5. The freshly packed tool, installed with --tool-path: validate, pack and verify every sample.
6. Every template, instantiated through the tool's new command (custom template hive, --feed) in a
   temporary folder outside the repository: its tool manifest pins the new tool, and it builds and
   passes the installed tool's test command.
7. The Generic Host add-on: its package depends on exactly this version of the author package and on
   the hosting abstractions alone, the feed new prepares carries it, and its readme's example builds
   against it from that feed.
8. Every package's release notes link to its version's section of CHANGELOG.md, or to the changelog
   when the version has no section (a -dev build).

No name of the product is written here: the product names come from fixtures/hosts.json, the package
family from the solution file's name and the tool's command from its project file.

.PARAMETER Configuration
Debug or Release (the default); it must match the build.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
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

# Runs a program and returns its standard output as lines; throws when it fails.
function Get-NativeOutput {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [string[]]$ArgumentList = @(),
        [string]$WorkingDirectory = ''
    )
    $saved = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    if ($WorkingDirectory) { Push-Location -LiteralPath $WorkingDirectory }
    try {
        $lines = @(& $FilePath @ArgumentList)
        $code = $LASTEXITCODE
    } finally {
        if ($WorkingDirectory) { Pop-Location }
        $ErrorActionPreference = $saved
    }
    if ($code -ne 0) {
        $lines | Out-Host
        throw ('"{0} {1}" exited with code {2}.' -f $FilePath, ($ArgumentList -join ' '), $code)
    }
    return , $lines
}

function Write-Step([string]$Text) {
    Write-Host ''
    Write-Host ('== ' + $Text)
}

function Join-Repository([string]$Relative) {
    return [System.IO.Path]::Combine($root, $Relative.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
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

# The text of a tracked file, or $null for a binary file (one with a NUL byte).
function Get-TrackedText([string]$Relative) {
    $bytes = [System.IO.File]::ReadAllBytes((Join-Repository $Relative))
    if ([System.Array]::IndexOf($bytes, [byte]0) -ge 0) { return $null }
    return (New-Object System.Text.UTF8Encoding($false)).GetString($bytes)
}

function Get-Sha256([System.IO.Stream]$Stream) {
    $algorithm = [System.Security.Cryptography.SHA256]::Create()
    try {
        return ([System.BitConverter]::ToString($algorithm.ComputeHash($Stream))).Replace('-', '').ToLowerInvariant()
    } finally {
        $algorithm.Dispose()
    }
}

# A package's nuspec, as XML.
function Get-PackageNuspec([string]$Path) {
    $archive = [System.IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $entries = @($archive.Entries | Where-Object { $_.FullName -notmatch '/' -and $_.FullName.EndsWith('.nuspec') })
        if ($entries.Count -ne 1) { throw ('Expected one .nuspec in ' + $Path + '.') }
        $reader = New-Object System.IO.StreamReader($entries[0].Open())
        try { return [xml]$reader.ReadToEnd() } finally { $reader.Dispose() }
    } finally {
        $archive.Dispose()
    }
}

function Get-FileSha256([string]$Path) {
    $stream = [System.IO.File]::OpenRead($Path)
    try { return Get-Sha256 $stream } finally { $stream.Dispose() }
}

$npm = 'npm'
if ($onWindows) { $npm = 'npm.cmd' }

# The build this script verifies.
$statePath = Join-Repository 'artifacts/build/state.json'
if (-not (Test-Path -LiteralPath $statePath)) { throw 'Run tools/build.ps1 first: artifacts/build/state.json is missing.' }
$state = [System.IO.File]::ReadAllText($statePath) | ConvertFrom-Json
if ($state.configuration -ne $Configuration) {
    throw ('tools/build.ps1 built ' + $state.configuration + '; verify the same configuration.')
}
$family = [string]$state.family
$version = [string]$state.version
$runId = [string]$state.runId
$packages = [string]$state.packages
$consumer = [string]$state.consumerPackages
$versionProperty = '-p:VentanaExtensionsVersion=' + $version
$solution = Join-Repository ($family + '.slnx')
$segment = $family.Split('.')[1]
$toolProject = Join-Repository ('src/' + $family + '.Tool/' + $family + '.Tool.csproj')
$commandMatch = [regex]::Match([System.IO.File]::ReadAllText($toolProject), '<ToolCommandName>([^<]+)</ToolCommandName>')
if (-not $commandMatch.Success) { throw 'The tool project has no ToolCommandName.' }
$command = $commandMatch.Groups[1].Value.Trim()

Write-Host ('Package family: ' + $family)
Write-Host ('Version:        ' + $version)

$tracked = Get-NativeOutput git @('-C', $root, '-c', 'core.quotePath=false', 'ls-files')
$failures = New-Object System.Collections.Generic.List[string]

# ---------------------------------------------------------------------------------------------
Write-Step 'Lint: private residue in tracked files'
# The product names, from the registry: every id, display name, reserved id and package file
# extension (contract section 2.8).
$registry = [System.IO.File]::ReadAllText((Join-Repository 'fixtures/hosts.json')) | ConvertFrom-Json
$productNames = New-Object System.Collections.Generic.List[string]
foreach ($entry in @($registry.hosts)) {
    $productNames.Add([string]$entry.id)
    $productNames.Add([string]$entry.displayName)
    if ($entry.PSObject.Properties.Name -contains 'packageExtension' -and $entry.packageExtension) {
        $productNames.Add(([string]$entry.packageExtension).TrimStart('.'))
    }
}
foreach ($reserved in @($registry.reservedIds)) { $productNames.Add([string]$reserved) }
$productNames = @($productNames | ForEach-Object { $_.ToLowerInvariant() } | Sort-Object -Unique)
$capitalized = @($productNames | ForEach-Object { $_.Substring(0, 1).ToUpperInvariant() + $_.Substring(1) })
$appCore = '\b(?:' + (($capitalized | ForEach-Object { [regex]::Escape($_) }) -join '|') + ')\.Core\b'
$leaks = @(
    @{ What = 'a Bitbucket address'; Pattern = '(?i)bitbucket\.org' },
    @{ What = 'a private app project name'; Pattern = $appCore },
    @{ What = 'a private decision record number'; Pattern = 'ADR-\d' },
    @{ What = 'a user profile path'; Pattern = '(?i)[a-z]:\\users\\' },
    @{ What = 'a personal name'; Pattern = 'Seth\s+Cottle' }
)
foreach ($file in $tracked) {
    # The changelog's history notes may name what earlier previews were.
    if ($file -eq 'CHANGELOG.md') { continue }
    $text = Get-TrackedText $file
    if ($null -eq $text) { continue }
    foreach ($leak in $leaks) {
        if ([regex]::IsMatch($text, $leak.Pattern)) {
            $lineNumber = 0
            foreach ($line in $text.Split("`n")) {
                $lineNumber++
                if ([regex]::IsMatch($line, $leak.Pattern)) { $failures.Add(('{0}:{1}: {2}' -f $file, $lineNumber, $leak.What)) }
            }
        }
    }
}

# ---------------------------------------------------------------------------------------------
Write-Step 'Lint: host ids and product names in samples and templates'
# Samples and templates say "the host app". A product name may appear only in a manifest's hosts and
# $schema lines, in the templates' hostId default, in package-family references and in the tool's
# command and template short names.
$escapedSegment = [regex]::Escape($segment.ToLowerInvariant())
$allowed = @(
    ('(?i)ventanatools\.' + $escapedSegment + '\.extensions[a-z0-9.]*'),
    ('(?i)@ventanatools/' + $escapedSegment + '-extensions(?:/[a-z0-9._/-]*)?'),
    ('(?i)ventanatools-' + $escapedSegment + '-extensions-[a-z0-9.-]*?\.tgz'),
    ('(?i)node/' + $escapedSegment + '-extensions'),
    ('(?i)(?<![a-z0-9])' + [regex]::Escape($command) + '(?:-[a-z]+)?(?![a-z0-9])')
)
foreach ($file in $tracked) {
    if (-not ($file.StartsWith('samples/') -or $file.StartsWith('templates/'))) { continue }
    $text = Get-TrackedText $file
    if ($null -eq $text) { continue }
    $leaf = $file.Substring($file.LastIndexOf('/') + 1)
    $inHostId = $false
    $lineNumber = 0
    foreach ($line in $text.Split("`n")) {
        $lineNumber++
        if ($leaf -eq 'extension.json' -and $line -match '^\s*"(hosts|\$schema)"\s*:') { continue }
        if ($leaf -eq 'template.json') {
            if ($line -match '^\s*"hostId"\s*:\s*\{') { $inHostId = $true }
            elseif ($inHostId -and $line -match '^\s*\}') { $inHostId = $false }
            elseif ($inHostId -and $line -match '^\s*"defaultValue"\s*:') { continue }
        }
        $rest = $line
        foreach ($pattern in $allowed) { $rest = [regex]::Replace($rest, $pattern, '') }
        foreach ($name in $productNames) {
            if ($rest.IndexOf($name, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                $failures.Add(('{0}:{1}: names a product ({2}); say "the host app"' -f $file, $lineNumber, $name))
            }
        }
    }
}

# ---------------------------------------------------------------------------------------------
Write-Step 'Lint: raw private-use glyphs'
# Glyphs are written as JSON or language escapes, never as the raw character (contract section 3.7).
$privateUse = '[' + [char]0xE000 + '-' + [char]0xF8FF + ']'
foreach ($file in $tracked) {
    $extension = [System.IO.Path]::GetExtension($file).ToLowerInvariant()
    $inDocs = $file -eq 'README.md' -or ($file.StartsWith('docs/') -and @('.md', '.mdx', '.json') -contains $extension)
    $inCode = ($file.StartsWith('samples/') -or $file.StartsWith('node/') -or $file.StartsWith('templates/') -or
        $file.StartsWith('src/') -or $file.StartsWith('tests/')) -and
        @('.json', '.cs', '.js', '.cjs', '.mjs', '.ts', '.md', '.html') -contains $extension
    # The Node SDK's byte copies follow their fixtures.
    if ($file -match '^node/[^/]+/lib/(codes/[^/]+\.json|hosts\.json)$') { $inCode = $false }
    if (-not ($inDocs -or $inCode)) { continue }
    $text = Get-TrackedText $file
    if ($null -eq $text) { continue }
    $lineNumber = 0
    foreach ($line in $text.Split("`n")) {
        $lineNumber++
        if ([regex]::IsMatch($line, $privateUse)) { $failures.Add(('{0}:{1}: raw private-use character; write it as an escape' -f $file, $lineNumber)) }
    }
}

# ---------------------------------------------------------------------------------------------
Write-Step 'Lint: SPDX headers'
# Libraries, tools, tests and the Node SDK: Apache-2.0. Samples: MIT-0. Template output: no header,
# so authors own what the templates generate (contract section 2.6).
$copyright = 'SPDX-FileCopyrightText: 2026 Ventana Tools LLC'
foreach ($file in $tracked) {
    $extension = [System.IO.Path]::GetExtension($file).ToLowerInvariant()
    if (@('.cs', '.ps1', '.js', '.cjs', '.mjs', '.ts') -notcontains $extension) { continue }
    $licence = $null
    if ($file.StartsWith('templates/content/')) { $licence = '' }
    elseif ($file.StartsWith('samples/')) { $licence = 'MIT-0' }
    elseif ($file -match '^(src|tests|tools|eng|node)/') { $licence = 'Apache-2.0' }
    else { continue }
    $head = (@((Get-TrackedText $file).Split("`n")) | Select-Object -First 5) -join "`n"
    if ($licence -eq '') {
        if ($head -match 'SPDX-License-Identifier') { $failures.Add(('{0}: template output carries no SPDX header' -f $file)) }
    } elseif (-not ($head.Contains('SPDX-License-Identifier: ' + $licence) -and $head.Contains($copyright))) {
        $failures.Add(('{0}: needs SPDX-License-Identifier: {1} and {2} in its first lines' -f $file, $licence, $copyright))
    }
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Host $_ }
    throw ('{0} lint failure(s).' -f $failures.Count)
}
Write-Host 'Lints passed.'

# ---------------------------------------------------------------------------------------------
Write-Step 'Library, Testing, tool and schema tests'
Invoke-Native dotnet @('test', $solution, '-c', $Configuration, '--no-build', '-nologo', $versionProperty)

# ---------------------------------------------------------------------------------------------
Write-Step 'Node SDK and Photoshop sample'
$nodeFolders = @(Get-ChildItem -LiteralPath (Join-Repository 'node') -Directory |
        Where-Object { Test-Path -LiteralPath ([System.IO.Path]::Combine($_.FullName, 'package.json')) })
foreach ($folder in $nodeFolders) {
    Invoke-Native $npm @('ci', '--ignore-scripts', '--no-audit', '--no-fund') $folder.FullName
    Invoke-Native $npm @('test') $folder.FullName
}
$photoshop = Join-Repository 'samples/photoshop-extension'
Invoke-Native $npm @('ci', '--ignore-scripts', '--no-audit', '--no-fund') $photoshop
Invoke-Native $npm @('run', 'check') $photoshop
Invoke-Native $npm @('test') $photoshop

$savedNuGetPackages = $env:NUGET_PACKAGES
$savedVersion = $env:VentanaExtensionsVersion
$work = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), 'ventana-verify-' + $runId)
try {
    # Every restore from here on uses the build's own package folder, so no dev package reaches the
    # person's global NuGet cache, and any build a command starts sees the build's version.
    $env:NUGET_PACKAGES = $consumer
    $env:VentanaExtensionsVersion = $version

    # -----------------------------------------------------------------------------------------
    Write-Step 'Samples against the new packages'
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $nupkg = [System.IO.Path]::Combine($packages, $family + '.' + $version + '.nupkg')
    $archive = [System.IO.Compression.ZipFile]::OpenRead($nupkg)
    try {
        $entries = @($archive.Entries | Where-Object { $_.FullName -match ('^lib/[^/]+/' + [regex]::Escape($family) + '\.dll$') })
        if ($entries.Count -ne 1) { throw ('Expected one ' + $family + '.dll in ' + $nupkg + '.') }
        $stream = $entries[0].Open()
        try { $packedHash = Get-Sha256 $stream } finally { $stream.Dispose() }
    } finally {
        $archive.Dispose()
    }
    Write-Host ('Packed ' + $family + '.dll: ' + $packedHash)

    $consumerFull = [System.IO.Path]::GetFullPath($consumer).TrimEnd('\', '/')
    $sampleProjects = New-Object System.Collections.Generic.List[System.IO.FileInfo]
    foreach ($sample in @(Get-ChildItem -LiteralPath (Join-Repository 'samples') -Directory)) {
        foreach ($project in @(Get-ChildItem -LiteralPath $sample.FullName -Filter '*.csproj' -File)) { $sampleProjects.Add($project) }
        $tests = [System.IO.Path]::Combine($sample.FullName, 'tests')
        if (Test-Path -LiteralPath $tests) {
            foreach ($project in @(Get-ChildItem -LiteralPath $tests -Filter '*.csproj' -File)) { $sampleProjects.Add($project) }
        }
    }
    foreach ($project in $sampleProjects) {
        $folder = $project.DirectoryName
        $assetsPath = [System.IO.Path]::Combine($folder, 'obj', 'project.assets.json')
        if (-not (Test-Path -LiteralPath $assetsPath)) { throw ($project.FullName + ' was not restored; run tools/build.ps1.') }
        # Read as text: the assets file can be larger than Windows PowerShell's JSON reader allows.
        $assets = [System.IO.File]::ReadAllText($assetsPath)
        if (-not $assets.Contains('"' + $family + '/' + $version + '"')) {
            throw ($project.Name + ' did not resolve ' + $family + ' ' + $version + '.')
        }
        $packageFolders = [regex]::Match($assets, '"packageFolders"\s*:\s*\{([^}]*)\}')
        $folders = @([regex]::Matches($packageFolders.Groups[1].Value, '"((?:[^"\\]|\\.)*)"\s*:') |
                ForEach-Object { [System.IO.Path]::GetFullPath($_.Groups[1].Value.Replace('\\', '\').Replace('\/', '/')).TrimEnd('\', '/') })
        if ($folders -notcontains $consumerFull) {
            throw ($project.Name + ' restored outside this build''s package folder ' + $consumer + '.')
        }
        # The output folder of this build, not an older one for another target framework.
        $outDir = ((Get-NativeOutput dotnet @('msbuild', $project.FullName, '-nologo', '-getProperty:TargetDir',
                        ('-p:Configuration=' + $Configuration), $versionProperty)) -join '').Trim()
        $output = [System.IO.Path]::Combine($outDir, $family + '.dll')
        if (-not (Test-Path -LiteralPath $output)) { throw ($project.Name + ' has no ' + $output + '; run tools/build.ps1.') }
        $hash = Get-FileSha256 $output
        if ($hash -ne $packedHash) {
            throw ($output + ' (' + $hash + ') is not the freshly packed ' + $family + '.dll (' + $packedHash + ').')
        }
        Write-Host ($project.Name + ': ' + $family + ' ' + $version + ', same SHA-256 as the new package.')
    }
    foreach ($project in $sampleProjects) {
        if ($project.Directory.Name -eq 'tests') {
            Invoke-Native dotnet @('test', $project.FullName, '-c', $Configuration, '--no-build', '-nologo', $versionProperty)
        }
    }

    # -----------------------------------------------------------------------------------------
    Write-Step 'The Generic Host add-on package'
    # An add-on may depend on Microsoft.Extensions abstractions and on exactly this version of the
    # author package, whose internal output seam it uses (contract section 9.5); nothing else.
    $packageVersions = [System.IO.File]::ReadAllText((Join-Repository 'Directory.Packages.props'))
    $abstractions = [regex]::Match($packageVersions, '<PackageVersion Include="Microsoft\.Extensions\.Hosting\.Abstractions" Version="([^"]+)"')
    if (-not $abstractions.Success) { throw 'Directory.Packages.props has no Microsoft.Extensions.Hosting.Abstractions version.' }
    $hostingId = $family + '.Hosting'
    $hostingNuspec = Get-PackageNuspec ([System.IO.Path]::Combine($packages, $hostingId + '.' + $version + '.nupkg'))
    $groups = @($hostingNuspec.package.metadata.dependencies.group)
    if ($groups.Count -ne 1 -or [string]$groups[0].targetFramework -ne 'net10.0') {
        throw ($hostingId + ' must have one dependency group, for net10.0.')
    }
    $found = New-Object System.Collections.Generic.List[string]
    foreach ($dependency in @($groups[0].dependency)) { $found.Add([string]$dependency.id + ' ' + [string]$dependency.version) }
    $wanted = @(($family + ' [' + $version + ']'), ('Microsoft.Extensions.Hosting.Abstractions ' + $abstractions.Groups[1].Value))
    $foundText = @($found | Sort-Object) -join '; '
    $wantedText = @($wanted | Sort-Object) -join '; '
    if ($foundText -ne $wantedText) {
        throw ($hostingId + ' depends on ' + $foundText + '; expected ' + $wantedText + '.')
    }
    Write-Host ($hostingId + ': depends on ' + ($found -join ' and ') + '.')

    # -----------------------------------------------------------------------------------------
    Write-Step 'Release notes'
    # The link eng/Ventana.Package.props computes: the version's heading in CHANGELOG.md as GitHub
    # anchors it, or the changelog itself.
    $packageProps = [System.IO.File]::ReadAllText((Join-Repository 'eng/Ventana.Package.props'))
    $repositoryUrl = [regex]::Match($packageProps, '<VentanaRepositoryUrl>([^<]+)</VentanaRepositoryUrl>')
    if (-not $repositoryUrl.Success) { throw 'eng/Ventana.Package.props has no VentanaRepositoryUrl.' }
    $changelogUrl = $repositoryUrl.Groups[1].Value.Trim() + '/blob/main/CHANGELOG.md'
    $expectedNotes = $changelogUrl
    foreach ($line in [System.IO.File]::ReadAllLines((Join-Repository 'CHANGELOG.md'))) {
        if (-not $line.StartsWith('## [' + $version + ']', [System.StringComparison]::Ordinal)) { continue }
        $anchor = New-Object System.Text.StringBuilder
        foreach ($c in $line.Substring(3).Trim().ToLowerInvariant().ToCharArray()) {
            if ($c -eq ' ') { [void]$anchor.Append('-') }
            elseif ([char]::IsLetterOrDigit($c) -or $c -eq '-' -or $c -eq '_') { [void]$anchor.Append($c) }
        }
        $expectedNotes = $changelogUrl + '#' + $anchor.ToString()
        break
    }
    $familyPackages = @(Get-ChildItem -LiteralPath $packages -Filter ($family + '*.' + $version + '.nupkg') -File)
    if ($familyPackages.Count -lt 5) { throw ('Expected the five packages of ' + $version + ' in ' + $packages + '.') }
    foreach ($file in $familyPackages) {
        $notes = [string](Get-PackageNuspec $file.FullName).package.metadata.releaseNotes
        if ($notes -ne $expectedNotes) { throw ($file.Name + ' has the release notes "' + $notes + '"; expected "' + $expectedNotes + '".') }
    }
    Write-Host ('Every package links its release notes to ' + $expectedNotes)

    # -----------------------------------------------------------------------------------------
    Write-Step 'Install the new tool'
    # Earlier verification runs are not needed once a new one starts.
    Remove-Folder (Join-Repository 'artifacts/verify')
    $verifyRun = Join-Repository ('artifacts/verify/' + $runId)
    $toolPath = [System.IO.Path]::Combine($verifyRun, 'tools')
    $hive = [System.IO.Path]::Combine($verifyRun, 'template-hive')
    $feed = [System.IO.Path]::Combine($verifyRun, 'feed')
    # The repository's NuGet.config maps the package family to artifacts/packages, so the install
    # needs no --add-source (which the .NET SDK refuses where package source mapping is in use).
    Invoke-Native dotnet @('tool', 'install', ($family + '.Tool'), '--tool-path', $toolPath, '--version', $version) $root
    $tool = [System.IO.Path]::Combine($toolPath, $command)
    if ($onWindows) { $tool += '.exe' }
    Invoke-Native $tool @('--version')

    # -----------------------------------------------------------------------------------------
    Write-Step 'Validate, pack and verify every sample'
    $distribution = Join-Repository 'artifacts/extension-distribution'
    New-Item -ItemType Directory -Force -Path $distribution | Out-Null
    foreach ($sample in @(Get-ChildItem -LiteralPath (Join-Repository 'samples') -Directory)) {
        if (-not (Test-Path -LiteralPath ([System.IO.Path]::Combine($sample.FullName, 'extension.json')))) { continue }
        $relative = 'samples/' + $sample.Name
        Invoke-Native $tool @('validate', $relative) $root
        $packed = (Get-NativeOutput $tool @('pack', $relative, '-o', $distribution, '--force', '--json') $root) -join "`n"
        $result = $packed | ConvertFrom-Json
        if (-not $result.ok) { throw ('Packing ' + $relative + ' failed.') }
        $packagePath = [string]$result.package.path
        if (-not [System.IO.Path]::IsPathRooted($packagePath)) { $packagePath = Join-Repository $packagePath }
        Invoke-Native $tool @('verify', $packagePath) $root
    }

    # -----------------------------------------------------------------------------------------
    Write-Step 'Templates, through the new tool'
    # Projects are created outside the repository, whose build settings and central package versions
    # would otherwise apply to them.
    Remove-Folder $work
    New-Item -ItemType Directory -Force -Path $work | Out-Null
    $templates = [System.IO.Path]::Combine($packages, $family + '.Templates.' + $version + '.nupkg')
    Invoke-Native dotnet @('new', 'install', $templates, '--debug:custom-hive', $hive) $work
    $toolPackage = ($family + '.Tool.' + $version + '.nupkg').ToLowerInvariant()
    foreach ($kind in @('action', 'widget', 'node')) {
        $name = 'Verify' + $kind.Substring(0, 1).ToUpperInvariant() + $kind.Substring(1)
        $project = [System.IO.Path]::Combine($work, $name)
        Invoke-Native $tool @('new', $kind, '-n', $name, '-o', $project, '--extension-id', ('example.verify-' + $kind),
            '--feed', $feed, '--debug:custom-hive', $hive) $work
        # The project's local tool manifest pins this build's tool, whose package new copied into the
        # feed. The tool tests' template tests run `dotnet tool restore` and `dotnet tool run` on it.
        $toolManifest = [System.IO.File]::ReadAllText([System.IO.Path]::Combine($project, '.config', 'dotnet-tools.json')) | ConvertFrom-Json
        $pinned = $toolManifest.tools.PSObject.Properties[($family + '.Tool').ToLowerInvariant()]
        if ($null -eq $pinned -or [string]$pinned.Value.version -ne $version -or @($pinned.Value.commands) -notcontains $command) {
            throw ($name + '/.config/dotnet-tools.json does not pin ' + $command + ' ' + $version + '.')
        }
        if (@(Get-ChildItem -LiteralPath $feed -Filter '*.nupkg' -File | Where-Object { $_.Name.ToLowerInvariant() -eq $toolPackage }).Count -ne 1) {
            throw ('new did not copy the tool''s own package into ' + $feed + '.')
        }
        if ($kind -eq 'node') {
            Invoke-Native $npm @('install', '--no-audit', '--no-fund') $project
        } else {
            Invoke-Native dotnet @('build', '-nologo', '-warnaserror') $project
        }
        # The tool installed above, by path. `dotnet tool run` would resolve the manifest through the
        # per-user tool resolver cache, which keeps the first package it saw for a version: with a
        # fixed version (-PackageVersion, -RepositoryVersion) a second run would fail, or test an
        # older tool than the one just built.
        Invoke-Native $tool @('test') $project
    }

    # -----------------------------------------------------------------------------------------
    Write-Step 'The Generic Host add-on, from the feed'
    $hostingPackage = ($hostingId + '.' + $version + '.nupkg').ToLowerInvariant()
    if (@(Get-ChildItem -LiteralPath $feed -Filter '*.nupkg' -File | Where-Object { $_.Name.ToLowerInvariant() -eq $hostingPackage }).Count -ne 1) {
        throw ('new did not copy ' + $hostingId + ' into ' + $feed + '.')
    }
    # A Generic Host companion made the way the add-on's readme says: its example is Program.cs, and the
    # widget project's nuget.config maps the package family to the feed.
    $readme = [System.IO.File]::ReadAllText((Join-Repository ('src/' + $hostingId + '/README.md'))).Replace("`r`n", "`n")
    $example = [regex]::Match($readme, '(?s)```csharp\n(.*?)\n```')
    if (-not $example.Success) { throw ('The ' + $hostingId + ' readme has no C# example.') }
    $hosted = [System.IO.Path]::Combine($work, 'VerifyHosted')
    New-Item -ItemType Directory -Force -Path $hosted | Out-Null
    Copy-Item -LiteralPath ([System.IO.Path]::Combine($work, 'VerifyWidget', 'nuget.config')) -Destination $hosted
    $hostedProject = '<Project Sdk="Microsoft.NET.Sdk">' + "`n" +
        '  <PropertyGroup>' + "`n" +
        '    <OutputType>Exe</OutputType>' + "`n" +
        '    <TargetFramework>net10.0-windows</TargetFramework>' + "`n" +
        '    <Nullable>enable</Nullable>' + "`n" +
        '    <ImplicitUsings>enable</ImplicitUsings>' + "`n" +
        '  </PropertyGroup>' + "`n" +
        '  <ItemGroup>' + "`n" +
        '    <PackageReference Include="' + $hostingId + '" Version="' + $version + '" />' + "`n" +
        '    <PackageReference Include="Microsoft.Extensions.Hosting" Version="' + $abstractions.Groups[1].Value + '" />' + "`n" +
        '  </ItemGroup>' + "`n" +
        '</Project>' + "`n"
    [System.IO.File]::WriteAllText([System.IO.Path]::Combine($hosted, 'VerifyHosted.csproj'), $hostedProject, (New-Object System.Text.UTF8Encoding($false)))
    [System.IO.File]::WriteAllText([System.IO.Path]::Combine($hosted, 'Program.cs'), $example.Groups[1].Value + "`n", (New-Object System.Text.UTF8Encoding($false)))
    Invoke-Native dotnet @('build', '-nologo', '-warnaserror') $hosted
} finally {
    $env:NUGET_PACKAGES = $savedNuGetPackages
    $env:VentanaExtensionsVersion = $savedVersion
    Remove-Folder $work
}

Write-Step 'Verified'
Write-Host ($family + ' ' + $version + ': lints, tests, samples, packages and templates all pass.')
