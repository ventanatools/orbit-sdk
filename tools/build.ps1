# SPDX-License-Identifier: Apache-2.0
# SPDX-FileCopyrightText: 2026 Ventana Tools LLC
#Requires -Version 7.0
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$taskRepository = Split-Path $PSScriptRoot -Parent
$taskFeed = Join-Path $taskRepository 'artifacts/packages'
New-Item -ItemType Directory -Path $taskFeed -Force | Out-Null

function Invoke-Dotnet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw 'The .NET build failed.' }
}

Push-Location $taskRepository
try {
    Invoke-Dotnet -Arguments @('restore', 'VentanaTools.Orbit.Extensions.slnx')
    Invoke-Dotnet -Arguments @('build', 'VentanaTools.Orbit.Extensions.slnx', '-c', $Configuration, '--no-restore')
    foreach ($taskProject in @('src/VentanaTools.Orbit.Extensions/VentanaTools.Orbit.Extensions.csproj')) {
        Invoke-Dotnet -Arguments @('pack', $taskProject, '-c', $Configuration, '--no-build', '-o', $taskFeed)
    }
    foreach ($taskProject in @('samples/dotnet-extension/DotnetExtensionSample.csproj',
            'samples/countdown-extension/CountdownExtensionSample.csproj',
            'samples/countdown-extension/tests/CountdownExtensionSample.Tests.csproj')) {
        Invoke-Dotnet -Arguments @('restore', $taskProject)
        Invoke-Dotnet -Arguments @('build', $taskProject, '-c', $Configuration, '--no-restore')
    }
} finally { Pop-Location }
