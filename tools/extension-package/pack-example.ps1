# SPDX-License-Identifier: Apache-2.0
#Requires -Version 7.0
param(
    [Parameter(Mandatory)][ValidateSet('countdown', 'photoshop')][string]$Sample,
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$taskRepo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$taskSource = Join-Path $taskRepo "samples/$Sample-extension"
$taskOutput = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) }
    else { Join-Path $taskRepo 'artifacts/extension-distribution' }
$taskStage = Join-Path $taskOutput ("stage-" + $Sample + '-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $taskStage -Force | Out-Null

function Copy-StagedFile([string]$From, [string]$Name) {
    if ((Get-Item -LiteralPath $From).Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw 'Example source cannot be a link.'
    }
    $taskTarget = Join-Path $taskStage $Name
    New-Item -ItemType Directory -Path (Split-Path $taskTarget -Parent) -Force | Out-Null
    Copy-Item -LiteralPath $From -Destination $taskTarget
}

Copy-StagedFile (Join-Path $taskSource 'extension.json') 'extension.json'
Copy-StagedFile (Join-Path $taskSource 'orbit-package.json') 'package.json'
Copy-StagedFile (Join-Path $taskSource 'PACKAGE-README.md') 'README.md'
Copy-StagedFile (Join-Path $taskRepo 'LICENSE') 'payload/LICENSE'
Copy-StagedFile (Join-Path $taskRepo 'NOTICE') 'payload/NOTICE'

if ($Sample -eq 'countdown') {
    $taskFiles = @('README.md', 'Directory.Build.props', 'CountdownExtensionSample.csproj',
        'Program.cs', 'Countdown.cs', 'CountdownHandler.cs', 'extension.json',
        'tests/CountdownExtensionSample.Tests.csproj', 'tests/Program.cs')
    $taskBuilt = Join-Path $taskSource 'bin/Release/net10.0'
    if (-not (Test-Path -LiteralPath (Join-Path $taskBuilt 'CountdownExtensionSample.dll'))) {
        throw 'Build the Countdown Release sample before packaging it.'
    }
    foreach ($taskRuntimeFile in @('CountdownExtensionSample.dll',
            'CountdownExtensionSample.deps.json', 'CountdownExtensionSample.runtimeconfig.json',
            'Orbit.Extensions.Protocol.dll', 'Orbit.Extensions.Sdk.dll')) {
        Copy-StagedFile (Join-Path $taskBuilt $taskRuntimeFile) ("payload/companion/" + $taskRuntimeFile)
    }
} else {
    # Explicit source allowlist: never sweep credentials, local fixtures or
    # generated bundles into an archive when packaging a developer's checkout.
    $taskFiles = @('README.md', 'extension.json', 'package.json', 'package-lock.json',
        'THIRD-PARTY-NOTICES.md', 'licenses/ws-MIT.txt', 'licenses/noble-hashes-MIT.txt',
        'licenses/esbuild-MIT.txt', 'companion/main.cjs', 'companion/bridge-v2.cjs',
        'companion/protocol-v2.cjs', 'companion/protocol.cjs', 'uxp/client-v2.js',
        'uxp/index.html', 'uxp/index.js', 'uxp/manifest.json', 'uxp/platform.js',
        'uxp/wire-v2.js', 'uxp/wire.js', 'tools/build-uxp.cjs', 'tools/check-syntax.cjs',
        'tools/crypto-entry.mjs', 'test/bridge-origin.test.cjs', 'test/bridge-v2.test.cjs',
        'test/fixtures/protocol-v2.json', 'test/helpers.cjs', 'test/history.test.cjs',
        'test/interop-v2-peer.cjs', 'test/panel-connection.test.cjs',
        'test/panel-lifetime.test.cjs', 'test/platform-refusal.test.cjs',
        'test/platform-v2.test.cjs', 'test/protocol.test.cjs')
}
foreach ($taskFile in $taskFiles) {
    Copy-StagedFile (Join-Path $taskSource $taskFile) ("payload/source/" + $taskFile)
}
$taskVersion = (Get-Content -LiteralPath (Join-Path $taskSource 'extension.json') -Raw | ConvertFrom-Json).version
$taskDestination = Join-Path $taskOutput ("example." + $Sample + '-' + $taskVersion + '.orbitextension')
& dotnet run --project (Join-Path $PSScriptRoot 'ExtensionPackageTool.csproj') -c Release -- pack $taskStage $taskDestination
if ($LASTEXITCODE -ne 0) { throw 'Example packaging failed. Existing output is never replaced.' }
& dotnet run --project (Join-Path $PSScriptRoot 'ExtensionPackageTool.csproj') -c Release -- verify $taskDestination
if ($LASTEXITCODE -ne 0) { throw 'Example validation failed.' }
Write-Output $taskDestination
