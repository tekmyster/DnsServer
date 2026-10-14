param([Parameter(Mandatory=$true)][string]$AppDir,
      [string]$DataDir = (Join-Path $env:ProgramData 'teQtanium DDI Server'),
      [ValidateRange(1,65535)][int]$DnsPort=53,
      [ValidateRange(1024,65535)][int]$ConsolePort=5380,
      [switch]$Check, [switch]$Uninstall)
$ErrorActionPreference = 'Stop'
# A setup launched from PowerShell 7 can inherit its module search path. Load the matching built-in security module explicitly.
Import-Module (Join-Path $PSHOME 'Modules\Microsoft.PowerShell.Security\Microsoft.PowerShell.Security.psd1') -ErrorAction Stop
$serviceName = 'TectaniumDDI'
$appPath = [IO.Path]::GetFullPath($AppDir)
$dataPath = [IO.Path]::GetFullPath($DataDir)
$executable = Join-Path $appPath 'TectaniumService.exe'
$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($Uninstall) {
    if ($existing) {
        $installed = Get-CimInstance Win32_Service -Filter "Name='$serviceName'"
        if (-not $installed.PathName.StartsWith('"' + $executable + '"', [StringComparison]::OrdinalIgnoreCase)) { throw 'The service belongs to another installation.' }
        Stop-Service -Name $serviceName
        & sc.exe delete $serviceName | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'The service could not be removed.' }
    }
    Write-Output 'Service removed. Configuration and backups retained.'
    exit 0
}
if ($existing) { throw 'teQtanium is already installed. Export a backup and use the documented manual upgrade procedure.' }
if (Test-Path -LiteralPath (Join-Path $dataPath 'auth.config')) { throw 'Existing configuration detected. Use a new directory and import a backup after installation.' }
if ($appPath.Equals($dataPath, [StringComparison]::OrdinalIgnoreCase)) { throw 'Use separate application and configuration directories.' }
$occupied = @(Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue | Where-Object { $_.LocalPort -in $DnsPort,$ConsolePort }) + @(Get-NetUDPEndpoint -ErrorAction SilentlyContinue | Where-Object { $_.LocalPort -eq $DnsPort })
if ($occupied.Count) { throw 'DNS or console ports are occupied. Use an isolated host; this installer will not replace that service.' }
if ($DnsPort -ne 53 -and -not (Test-Path -LiteralPath (Join-Path $dataPath 'dns.config'))) { throw 'Prepare a DNS configuration before using a nonstandard DNS port.' }
if ($Check) { exit 0 }
New-Item -ItemType Directory -Path $dataPath -Force | Out-Null
$created = $false
try {
    New-Service -Name $serviceName -DisplayName 'teQtanium DDI Server' -BinaryPathName ('"' + $executable + '" "' + $dataPath + '"') -StartupType Manual | Out-Null
    $created = $true
    # Use LocalService with this service's own SID for configuration access. Never start as LocalSystem.
    $configured = Invoke-CimMethod -InputObject (Get-CimInstance Win32_Service -Filter "Name='$serviceName'") -MethodName Change -Arguments @{StartName='NT AUTHORITY\LocalService';StartPassword=''}
    if ($configured.ReturnValue -ne 0 -or (Get-CimInstance Win32_Service -Filter "Name='$serviceName'").StartName -ne 'NT AUTHORITY\LocalService') { throw ('The service account could not be configured. Service error: ' + $configured.ReturnValue) }
    & sc.exe sidtype $serviceName unrestricted | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'The service identity could not be configured.' }
    $serviceSidText = (& sc.exe showsid $serviceName | Out-String)
    if ($LASTEXITCODE -ne 0 -or $serviceSidText -notmatch 'S-1-5-80-\d+-\d+-\d+-\d+-\d+') { throw 'The service identity could not be read.' }
    $serviceSid = New-Object Security.Principal.SecurityIdentifier($Matches[0])
    $acl = New-Object Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($identity in @('S-1-5-18','S-1-5-32-544')) {
        $sid = New-Object Security.Principal.SecurityIdentifier($identity)
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid,'FullControl','ContainerInherit,ObjectInherit','None','Allow'))
    }
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($serviceSid,'Modify','ContainerInherit,ObjectInherit','None','Allow'))
    Set-Acl -LiteralPath $dataPath -AclObject $acl
    $passwordPath = Join-Path $dataPath 'initial-admin-password.txt'
    $bytes = New-Object byte[] 32
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    [IO.File]::WriteAllText($passwordPath, [Convert]::ToBase64String($bytes))
    $environment = @('DNS_SERVER_WEB_SERVICE_LOCAL_ADDRESSES=127.0.0.1', ('DNS_SERVER_WEB_SERVICE_HTTP_PORT=' + $ConsolePort), ('DNS_SERVER_LOG_FOLDER_PATH=' + (Join-Path $dataPath 'logs')), ('DNS_SERVER_ADMIN_PASSWORD_FILE=' + $passwordPath))
    New-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName" -Name Environment -PropertyType MultiString -Value $environment -Force | Out-Null
    Start-Service -Name $serviceName
    $ready = $false
    for ($attempt=0; $attempt -lt 30; $attempt++) {
        try {
            $login = Invoke-RestMethod -Uri "http://127.0.0.1:$ConsolePort/api/user/login" -Method Post -Body @{user='admin';pass=[IO.File]::ReadAllText($passwordPath);includeInfo='true'}
            if ($login.status -eq 'ok') {
                $headers = @{Authorization='Bearer ' + $login.token}
                try {
                    $settings = Invoke-RestMethod -Uri "http://127.0.0.1:$ConsolePort/api/settings/get" -Headers $headers
                    if ($settings.status -eq 'ok' -and $login.info.permissions.Settings.canModify) { $ready = $true }
                } finally { Invoke-RestMethod -Uri "http://127.0.0.1:$ConsolePort/api/user/logout" -Method Post -Headers $headers | Out-Null }
                if ($ready) { break }
            }
        } catch { Start-Sleep -Seconds 1 }
    }
    if (-not $ready) { throw 'The service did not pass a fresh administrative login and settings check.' }
    Set-Service -Name $serviceName -StartupType Automatic
    Write-Output "Installed. Console: http://127.0.0.1:$ConsolePort/. Initial admin credential: $passwordPath"
} catch {
    if ($created) { Stop-Service -Name $serviceName -ErrorAction SilentlyContinue; & sc.exe delete $serviceName | Out-Null }
    throw
}
