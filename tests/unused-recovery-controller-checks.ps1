# SPDX-License-Identifier: GPL-3.0-only
# Targeted mocked VMware adapter checks. These never connect to a VM or prove physical fencing.
$ErrorActionPreference='Stop'
Import-Module VMware.VimAutomation.Core
$source=Join-Path $PSScriptRoot '../scripts/recover-unused-dhcp-vmware.ps1'
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Resolve-Path $source),[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Controller syntax errors'}
$function=$ast.FindAll({param($node)$node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'NicState'},$true)[0]
. ([scriptblock]::Create($function.Extent.Text))
function TaskComplete($reference) {if($reference -ne 'mock-task'){throw 'Unexpected reconfiguration task'}}
function NewDevice([bool]$connected,[bool]$start,[bool]$guest) {
    $device=[VMware.Vim.VirtualE1000e]::new();$device.Key=4000;$device.MacAddress='02:00:00:00:00:01'
    $device.Connectable=[VMware.Vim.VirtualDeviceConnectInfo]::new()
    $device.Connectable.Connected=$connected;$device.Connectable.StartConnected=$start;$device.Connectable.AllowGuestControl=$guest
    return $device
}
function Get-View($Server,$Id) {
    if($script:readback) {
        $devices=if($script:mode -eq 'missing'){@()}else{@($script:observed)}
    } else {$devices=@((NewDevice $true $true $true))}
    $view=[pscustomobject]@{Config=[pscustomobject]@{Hardware=[pscustomobject]@{Device=$devices}}}
    $view|Add-Member -MemberType ScriptMethod -Name ReconfigVM_Task -Value {
        param($spec)
        $sent=$spec.DeviceChange[0].Device
        $script:observed=NewDevice $sent.Connectable.Connected $sent.Connectable.StartConnected $sent.Connectable.AllowGuestControl
        switch($script:mode){'connected'{$script:observed.Connectable.Connected=$true};'boot'{$script:observed.Connectable.StartConnected=$true};'guest'{$script:observed.Connectable.AllowGuestControl=$true};'identity'{$script:observed.MacAddress='02:00:00:00:00:FF'}}
        $script:readback=$true
        return 'mock-task'
    }
    return $view
}
$saved=@([pscustomobject]@{key=4000;mac='02:00:00:00:00:01';connected=$true;startConnected=$true;allowGuestControl=$true})
$vm=[pscustomobject]@{Id='mock-vm'};$connection='mock-only';$passed=0
foreach($mode in @('valid','connected','boot','guest','missing','identity')) {
    $script:mode=$mode;$script:readback=$false;$rejected=$false
    try{NicState $vm $saved $false}catch{$rejected=$true}
    if($mode -eq 'valid' -and $rejected){throw 'Valid adapter fence rejected'}
    if($mode -ne 'valid' -and !$rejected){throw "Invalid adapter fence accepted: $mode"}
    $passed++
}
$script:mode='valid';$script:readback=$false;NicState $vm $saved $true
if(!$script:observed.Connectable.Connected -or !$script:observed.Connectable.StartConnected -or !$script:observed.Connectable.AllowGuestControl){throw 'Restore does not preserve original adapter flags'}
$passed++
Write-Output "PASS: $passed mocked VMware adapter fencing/readback/restore checks; physical deployment remains unverified."
