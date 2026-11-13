param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'dist'),
    [string]$Dotnet
)

$ErrorActionPreference = 'Stop'
if (!$Dotnet) {
    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($command) {
        $Dotnet = $command.Source
    } else {
        $Dotnet = Join-Path $env:ProgramFiles 'dotnet/dotnet.exe'
    }
    if (!(Test-Path -LiteralPath $Dotnet -PathType Leaf)) {
        throw 'Install the .NET 10 SDK, or pass its dotnet.exe using -Dotnet.'
    }
}

$OutputDirectory = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
$project = Join-Path $PSScriptRoot 'src/Autocaster.App/Autocaster.App.csproj'
$artifacts = Join-Path $PSScriptRoot 'artifacts'
$target = Join-Path $OutputDirectory 'WarframeAutocaster.exe'
$pending = "$target.$([guid]::NewGuid().ToString('N')).tmp"
$previous = Join-Path $OutputDirectory 'WarframeAutocaster.previous.exe'
$publish = Join-Path $artifacts ('publish-' + [guid]::NewGuid().ToString('N'))

$running = Get-Process WarframeAutocaster -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -eq $target }
if ($running) { throw 'Close this portable copy of Autocaster before rebuilding.' }

try {
    & $Dotnet publish $project -c Release -r win-x64 --self-contained true --artifacts-path $artifacts `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o $publish
    if ($LASTEXITCODE -ne 0) { throw 'Portable build failed.' }

    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $publish 'WarframeAutocaster.exe') -Destination $pending
    $profileDestination = Join-Path $OutputDirectory 'profiles'
    if (!(Test-Path -LiteralPath $profileDestination)) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'profiles') -Destination $profileDestination -Recurse
    }
    if ($OutputDirectory -ne $PSScriptRoot) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination $OutputDirectory -Force
    }

    if (Test-Path -LiteralPath $target -PathType Leaf) {
        [IO.File]::Replace($pending, $target, $previous)
    } else {
        [IO.File]::Move($pending, $target)
    }
    Write-Output $target
}
finally {
    if (Test-Path -LiteralPath $pending) { Remove-Item -LiteralPath $pending }
    if (Test-Path -LiteralPath (Join-Path $publish 'WarframeAutocaster.exe')) {
        Remove-Item -LiteralPath (Join-Path $publish 'WarframeAutocaster.exe')
    }
    if ((Test-Path -LiteralPath $publish) -and !(Get-ChildItem -LiteralPath $publish -Force)) {
        Remove-Item -LiteralPath $publish
    }
}
