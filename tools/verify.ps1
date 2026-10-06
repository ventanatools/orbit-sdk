# SPDX-License-Identifier: Apache-2.0
# SPDX-FileCopyrightText: 2026 Ventana Tools LLC
#Requires -Version 7.0
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$taskRepository = Split-Path $PSScriptRoot -Parent

function Invoke-Checked([string]$Program, [string[]]$Arguments) {
    & $Program @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Program verification failed." }
}

Push-Location $taskRepository
try {
    Invoke-Checked -Program dotnet -Arguments @('test', 'VentanaTools.Orbit.Extensions.slnx', '-c', $Configuration, '--no-build', '--no-restore')
    Invoke-Checked -Program dotnet -Arguments @('run', '--project', 'samples/countdown-extension/tests/CountdownExtensionSample.Tests.csproj',
        '-c', $Configuration, '--no-build', '--no-restore')
    Push-Location (Join-Path $taskRepository 'samples/photoshop-extension')
    try {
        Invoke-Checked -Program npm -Arguments @('ci', '--ignore-scripts')
        Invoke-Checked -Program npm -Arguments @('run', 'check')
        Invoke-Checked -Program npm -Arguments @('test')
    } finally { Pop-Location }
    if ($Configuration -eq 'Release') {
        $taskPackageOutput = Join-Path $taskRepository ('artifacts/verification/' + [guid]::NewGuid().ToString('N'))
        foreach ($taskSample in @('countdown', 'photoshop')) {
            & (Join-Path $PSScriptRoot 'extension-package/pack-example.ps1') -Sample $taskSample -OutputDirectory $taskPackageOutput
            if ($LASTEXITCODE -ne 0) { throw 'Example packaging failed.' }
        }
    }
} finally { Pop-Location }
