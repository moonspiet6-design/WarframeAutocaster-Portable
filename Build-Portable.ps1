param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'dist'),
    [string]$Dotnet = 'dotnet'
)

$ErrorActionPreference = 'Stop'
$OutputDirectory = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
$project = Join-Path $PSScriptRoot 'src/Autocaster.App/Autocaster.App.csproj'
$artifacts = Join-Path $PSScriptRoot 'artifacts'

& $Dotnet publish $project -c Release -r win-x64 --self-contained true --artifacts-path $artifacts `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o $OutputDirectory
if ($LASTEXITCODE -ne 0) { throw 'Portable build failed.' }

$profileDestination = Join-Path $OutputDirectory 'profiles'
if (!(Test-Path -LiteralPath $profileDestination)) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'profiles') -Destination $profileDestination -Recurse
}

Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination $OutputDirectory -Force
Write-Output (Join-Path $OutputDirectory 'WarframeAutocaster.exe')
