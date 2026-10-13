<#
.SYNOPSIS
Allows JetKVM MQTT status traffic to ChangeMonitorMode.
.DESCRIPTION
Run in elevated PowerShell. IP addresses must match settings.json.
Use -WhatIf to preview. Block rules are changed only with -DisableConflictingBlocks.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)]
    [System.Net.IPAddress]$JetIp,
    [Parameter(Mandatory = $true)]
    [System.Net.IPAddress]$ReceiverIp,
    [ValidateRange(1, 65535)]
    [int]$Port = 1883,
    [string]$ExecutablePath = (Join-Path $PSScriptRoot 'publish\jetkvm-monitor-switcher.exe'),
    [switch]$DisableConflictingBlocks
)

$ErrorActionPreference = 'Stop'
$resolvedExecutable = (Resolve-Path -LiteralPath $ExecutablePath).ProviderPath
if (!(Test-Path -LiteralPath $resolvedExecutable -PathType Leaf)) {
    throw 'ExecutablePath must identify an executable file.'
}
if ($JetIp.AddressFamily -ne [System.Net.Sockets.AddressFamily]::InterNetwork -or
    $ReceiverIp.AddressFamily -ne [System.Net.Sockets.AddressFamily]::InterNetwork) {
    throw 'Use IPv4 addresses for JetIp and ReceiverIp.'
}

$ruleName = 'ChangeMonitorMode-JetKVM-State'
if ($PSCmdlet.ShouldProcess($resolvedExecutable, "Allow TCP port $Port from $JetIp to $ReceiverIp")) {
    $existingRule = Get-NetFirewallRule -Name $ruleName -ErrorAction SilentlyContinue
    if ($existingRule) { $existingRule | Remove-NetFirewallRule }
    New-NetFirewallRule -Name $ruleName `
        -DisplayName 'ChangeMonitorMode: JetKVM session status only' `
        -Direction Inbound -Action Allow -Protocol TCP -LocalPort $Port `
        -LocalAddress $ReceiverIp.ToString() -RemoteAddress $JetIp.ToString() `
        -Program $resolvedExecutable -Profile Any | Out-Null
}

if ($DisableConflictingBlocks) {
    $blockRules = Get-NetFirewallApplicationFilter |
        Where-Object { $_.Program -eq $resolvedExecutable } |
        Get-NetFirewallRule |
        Where-Object { $_.Action -eq 'Block' -and $_.Direction -eq 'Inbound' }
    foreach ($blockRule in $blockRules) {
        $portFilter = $blockRule | Get-NetFirewallPortFilter
        if (($portFilter.Protocol -eq 'TCP' -or $portFilter.Protocol -eq '6') -and
            $PSCmdlet.ShouldProcess($blockRule.Name, 'Disable conflicting inbound TCP block rule')) {
            $blockRule | Disable-NetFirewallRule
        }
    }
}
