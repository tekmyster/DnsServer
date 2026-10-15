param(
    [string]$LibraryPath = (Join-Path $PSScriptRoot '..\..\TechnitiumLibrary')
)

$ErrorActionPreference = 'Stop'
$Configuration = 'Release'
$repositoryPath = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '.NET 10 SDK is required to build this project.'
}

if (-not (Test-Path -LiteralPath $LibraryPath -PathType Container)) {
    throw 'Clone TechnitiumSoftware/TechnitiumLibrary next to this checkout before building.'
}

$resolvedLibraryPath = (Resolve-Path -LiteralPath $LibraryPath).Path
$expectedLibraryPath = [System.IO.Path]::GetFullPath((Join-Path $repositoryPath '..\TechnitiumLibrary'))
if (-not $resolvedLibraryPath.Equals($expectedLibraryPath, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Existing project references require TechnitiumLibrary at $expectedLibraryPath."
}

$projects = @(
    (Join-Path $resolvedLibraryPath 'TechnitiumLibrary.ByteTree\TechnitiumLibrary.ByteTree.csproj'),
    (Join-Path $resolvedLibraryPath 'TechnitiumLibrary.Net\TechnitiumLibrary.Net.csproj'),
    (Join-Path $resolvedLibraryPath 'TechnitiumLibrary.Security.OTP\TechnitiumLibrary.Security.OTP.csproj'),
    (Join-Path $repositoryPath 'DnsServerApp\DnsServerApp.csproj'),
    (Join-Path $repositoryPath 'Apps\AdvancedBlockingApp\AdvancedBlockingApp.csproj')
)

foreach ($project in $projects) {
    Write-Host "Building $project"
    & dotnet build $project -c $Configuration --nologo -v minimal
    if ($LASTEXITCODE -ne 0) {
        throw "Build failed for $project (exit code $LASTEXITCODE)."
    }
}
