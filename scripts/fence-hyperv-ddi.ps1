param(
    [Parameter(Mandatory)][string]$VMName,
    [Parameter(Mandatory)][Guid]$VMId,
    [Parameter(Mandatory)][System.Net.IPAddress]$ManagementSource
)
$ErrorActionPreference = 'Stop'
Import-Module Hyper-V
$ddiVm = Hyper-V\Get-VM -Name $VMName
if ($ddiVm.Id -ne $VMId) { throw 'VM identity differs from the enrolled fencing target.' }
$ddiRules = @(
    @{Weight=65000;Action='Allow';Direction='Inbound';Protocol='TCP';LocalPort='22';RemoteIPAddress="$ManagementSource/32"},
    @{Weight=65001;Action='Allow';Direction='Inbound';Protocol='TCP';LocalPort='5380';RemoteIPAddress="$ManagementSource/32"},
    @{Weight=65002;Action='Allow';Direction='Inbound';Protocol='TCP';LocalPort='53443';RemoteIPAddress="$ManagementSource/32"},
    @{Weight=65010;Action='Allow';Direction='Outbound';Protocol='TCP';LocalPort='22';RemoteIPAddress="$ManagementSource/32"},
    @{Weight=65011;Action='Allow';Direction='Outbound';Protocol='TCP';LocalPort='5380';RemoteIPAddress="$ManagementSource/32"},
    @{Weight=65012;Action='Allow';Direction='Outbound';Protocol='TCP';LocalPort='53443';RemoteIPAddress="$ManagementSource/32"},
    @{Weight=64000;Action='Deny';Direction='Outbound'},
    @{Weight=64001;Action='Deny';Direction='Inbound'}
)
function Assert-DdiFenceRule($actual, $expected) {
    if (-not $actual -or @($actual).Count -ne 1) { throw 'Fencing rule is missing or ambiguous.' }
    if (-not $expected.ContainsKey('Protocol') -and [string]$actual.Protocol -notin @('ANY','Any','0','256','')) { throw 'Wildcard service fence did not match all protocols.' }
    foreach ($ddiKey in $expected.Keys) {
        $ddiActualValue = [string]$actual.$ddiKey
        $ddiExpectedValue = [string]$expected[$ddiKey]
        if ($ddiKey -eq 'Protocol' -and $ddiActualValue -in @('6','17')) { $ddiActualValue = if ($ddiActualValue -eq '6') { 'TCP' } else { 'UDP' } }
        if ($ddiActualValue -ne $ddiExpectedValue) { throw "Conflicting fencing rule: $ddiKey differs at weight $($expected.Weight)." }
    }
}
$ddiAdapters = @(Hyper-V\Get-VMNetworkAdapter -VM $ddiVm)
if ($ddiAdapters.Count -eq 0) { throw 'Fencing target has no network adapters.' }
foreach ($ddiAdapter in $ddiAdapters) {
    foreach ($ddiRule in $ddiRules) {
        $ddiExisting = @(Hyper-V\Get-VMNetworkAdapterExtendedAcl -VMNetworkAdapter $ddiAdapter | Where-Object Weight -eq $ddiRule.Weight)
        if ($ddiExisting.Count -eq 0) { Hyper-V\Add-VMNetworkAdapterExtendedAcl -VMNetworkAdapter $ddiAdapter @ddiRule -Stateful $false }
        $ddiActual = @(Hyper-V\Get-VMNetworkAdapterExtendedAcl -VMNetworkAdapter $ddiAdapter | Where-Object Weight -eq $ddiRule.Weight)
        Assert-DdiFenceRule $ddiActual $ddiRule
    }
}
$ddiReceipt = [ordered]@{vmName=$ddiVm.Name;vmId=[string]$ddiVm.Id;managementSource=[string]$ManagementSource;adapters=@($ddiAdapters | ForEach-Object Name);ruleCount=$ddiRules.Count;verifiedAt=[DateTime]::UtcNow.ToString('o')}
$ddiReceipt | ConvertTo-Json -Depth 4 -Compress
