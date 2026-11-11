# Run as administrator, after starting the receiver. Scope is only awdl0 and IPv6 link-local peers.
param([Parameter(Mandatory=$true)][string]$PythonExe, [string]$RulePrefix = 'Пульсар')
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$OutputEncoding = [Console]::OutputEncoding
$PythonExe = (Resolve-Path -LiteralPath $PythonExe).Path
foreach ($awdlRule in @(@("$RulePrefix TCP on awdl0",'TCP','8771'), @("$RulePrefix mDNS on awdl0",'UDP','5353'))) {
    $awdlExisting = Get-NetFirewallRule -DisplayName $awdlRule[0] -ErrorAction SilentlyContinue
    if ($awdlExisting) {
        $awdlExisting | Get-NetFirewallApplicationFilter | Set-NetFirewallApplicationFilter -Program $PythonExe | Out-Null
    } else {
        New-NetFirewallRule -DisplayName $awdlRule[0] -Direction Inbound -Action Allow -Protocol $awdlRule[1] -LocalPort $awdlRule[2] -InterfaceAlias awdl0 -Program $PythonExe -RemoteAddress 'fe80::/10' -Profile Any | Out-Null
    }
    Write-Output "Разрешены $($awdlRule[1]) $($awdlRule[2]) на awdl0 для $PythonExe"
}
