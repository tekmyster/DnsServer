# SPDX-License-Identifier: GPL-3.0-only
# Public release guard; contains no private implementation.
param([string]$RepositoryRoot=(Join-Path $PSScriptRoot '..'))
$ErrorActionPreference='Stop'
$root=(Resolve-Path -LiteralPath $RepositoryRoot).Path
if (Test-Path -LiteralPath (Join-Path $root 'PUBLICATION-HOLD.txt')) {
    throw 'Public publication is on hold by owner instruction. Private builds may continue; public packaging is blocked.'
}
$inventory=Join-Path $root 'PRIVATE-FILES.txt'
if (-not (Test-Path -LiteralPath $inventory)) { throw 'Private file inventory is missing.' }
$privateEntries=@(Get-Content -LiteralPath $inventory | Where-Object { $_.Trim() -and -not $_.Trim().StartsWith('#') })
if ($privateEntries.Count) { throw 'Private files are inventoried. Use a separate private release pipeline; public packaging is blocked.' }
$files=@(& git -C $root -c core.quotePath=false ls-files --cached --others --exclude-standard)
if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect repository files for public packaging.' }
$existingGpl=@('Tectanium.WindowsService/Program.cs','Tectanium.WindowsService/Tectanium.WindowsService.csproj')
$blocked=[System.Collections.Generic.List[string]]::new()
foreach ($relative in $files) {
    $privatePath=$relative -cmatch '^(Apps/Tectanium\.[^/]+(?:/|$)|Tectanium\.[^/]+/|DnsServerCore/(?:.*/)?Tectanium/|docs/tectanium/|packaging/tectanium/)'
    if ($privatePath -and $relative -cnotin $existingGpl) { $blocked.Add($relative); continue }
    $path=Join-Path $root $relative
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }
    $stream=[IO.File]::OpenRead($path)
    try {
        $bytes=[byte[]]::new(2048)
        $length=$stream.Read($bytes,0,$bytes.Length)
        $head=[Text.Encoding]::UTF8.GetString($bytes,0,$length)
        if ($head -match '(?im)^\s*(?://|#|/\*|\*|<!--)?\s*Copyright[^\r\n]*Tekmyster[^\r\n]*All Rights Reserved' -or
            $head -match '(?im)^\s*(?://|#|/\*|\*|<!--)?\s*Proprietary and Confidential') {
            $blocked.Add($relative)
        }
    } finally { $stream.Dispose() }
}
if ($blocked.Count) { throw ('Private or unclassified source cannot enter public packages: ' + (($blocked | Sort-Object -Unique) -join ', ')) }
Write-Output 'Public license guard passed.'
