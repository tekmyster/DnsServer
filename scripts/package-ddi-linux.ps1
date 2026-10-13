param([string]$Runtime = 'linux-x64')
$ErrorActionPreference = 'Stop'
if ($Runtime -notin @('linux-x64','linux-arm64')) { throw 'Use linux-x64 or linux-arm64.' }
$ddiRepository = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$ddiCommit = (& git -C $ddiRepository rev-parse --short=12 HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot identify source revision.' }
$ddiDirty = [bool](& git -C $ddiRepository status --porcelain)
$ddiLabel = $ddiCommit + $(if ($ddiDirty) { '-working' } else { '' }) + '-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ')
$ddiPublish = Join-Path $ddiRepository "artifacts/ddi-$Runtime-$ddiLabel"
if (Test-Path -LiteralPath $ddiPublish) { throw 'Use a new package directory; published builds are immutable.' }
& dotnet publish (Join-Path $ddiRepository 'DnsServerApp/DnsServerApp.csproj') -c Release -r $Runtime --self-contained true --nologo -o $ddiPublish
if ($LASTEXITCODE -ne 0) { throw 'Linux publish failed.' }
Copy-Item -LiteralPath (Join-Path $ddiRepository 'LICENSE'),(Join-Path $ddiRepository 'THIRD-PARTY-NOTICES.md') -Destination $ddiPublish
$ddiManifest = foreach ($ddiFile in (Get-ChildItem -LiteralPath $ddiPublish -Recurse -File | Sort-Object FullName)) {
    $ddiRelative = $ddiFile.FullName.Substring($ddiPublish.Length + 1).Replace('\','/')
    (Get-FileHash -LiteralPath $ddiFile.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $ddiRelative
}
Set-Content -LiteralPath (Join-Path $ddiPublish 'BUILD-SHA256SUMS') -Value $ddiManifest -Encoding ascii
$ddiArchive = $ddiPublish + '.tar.gz'
& tar -czf $ddiArchive -C $ddiPublish .
if ($LASTEXITCODE -ne 0) { throw 'Package archive failed.' }
$ddiHash = (Get-FileHash -LiteralPath $ddiArchive -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath ($ddiArchive + '.sha256') -Value ($ddiHash + '  ' + [IO.Path]::GetFileName($ddiArchive)) -Encoding ascii
Write-Host "Built $ddiArchive"
Write-Host 'Extract into a new guest directory, verify BUILD-SHA256SUMS, and sync the filesystem before switching the service.'
