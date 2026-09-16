#requires -RunAsAdministrator
<#
.SYNOPSIS
通过 NSSM 注册 OfficeMcp Windows 服务，供用户完成本机安装。
.PARAMETER NssmPath
NSSM x64 程序路径。
.PARAMETER PublishDirectory
包含 OfficeMcp.Api.exe 和运行配置的发布目录。
.PARAMETER ServiceName
要新建的系统服务名；不覆盖同名现有服务。
.PARAMETER StartNow
指定后立即启动；默认只注册并设置开机自启。
#>
[CmdletBinding()]
param(
    [string]$NssmPath = (Join-Path $PSScriptRoot '../../nssm-2.24/win64/nssm.exe'),
    [string]$PublishDirectory = (Join-Path $PSScriptRoot '../artifacts/publish'),
    [ValidatePattern('^[A-Za-z][A-Za-z0-9_-]{0,79}$')]
    [string]$ServiceName = 'OfficeMcp',
    [switch]$StartNow
)

$ErrorActionPreference = 'Stop'
$taskNssm = (Resolve-Path -LiteralPath $NssmPath).Path
$taskPublish = (Resolve-Path -LiteralPath $PublishDirectory).Path
$taskExecutable = Join-Path $taskPublish 'OfficeMcp.Api.exe'
if (-not (Test-Path -LiteralPath $taskExecutable -PathType Leaf)) { throw '请先执行 publish.ps1。' }
if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) { throw "服务 $ServiceName 已存在，请使用 nssm edit 管理现有服务。" }

# 显式检查配置，避免注册一个启动后因缺少密钥反复退出的服务。
$taskLocalConfig = Join-Path $taskPublish 'appsettings.Local.json'
if (-not (Test-Path -LiteralPath $taskLocalConfig)) { throw '请先在发布目录填写 appsettings.Local.json；示例位于 deploy 目录。' }
$taskConfig = Get-Content -LiteralPath $taskLocalConfig -Raw | ConvertFrom-Json
if ([string]::IsNullOrWhiteSpace($taskConfig.Authentication.ApiKey) -or $taskConfig.Authentication.ApiKey.Length -lt 32 -or
    [string]::IsNullOrWhiteSpace($taskConfig.DingTalk.ClientId) -or [string]::IsNullOrWhiteSpace($taskConfig.DingTalk.ClientSecret)) {
    throw '本机配置缺少有效的 API Key 或钉钉凭据。'
}

function Invoke-OfficeNssm {
    <# .SYNOPSIS 执行 NSSM 命令并检查退出码，失败时停止配置。 #>
    param([Parameter(Mandatory)][string[]]$Arguments)
    & $taskNssm @Arguments
    if ($LASTEXITCODE -ne 0) { throw "NSSM 命令失败：$($Arguments[0])。请检查已注册服务的配置。" }
}

$taskLogs = Join-Path $taskPublish 'logs'
New-Item -ItemType Directory -Path $taskLogs -Force | Out-Null
Invoke-OfficeNssm -Arguments @('install', $ServiceName, $taskExecutable)
Invoke-OfficeNssm -Arguments @('set', $ServiceName, 'AppDirectory', $taskPublish)
Invoke-OfficeNssm -Arguments @('set', $ServiceName, 'DisplayName', 'Office HTTP API')
Invoke-OfficeNssm -Arguments @('set', $ServiceName, 'Description', '办公通用 HTTP API，供 MCP 网关通过 ZeroTier 调用。')
Invoke-OfficeNssm -Arguments @('set', $ServiceName, 'Start', 'SERVICE_AUTO_START')
Invoke-OfficeNssm -Arguments @('set', $ServiceName, 'AppEnvironmentExtra', 'ASPNETCORE_ENVIRONMENT=Production')
Invoke-OfficeNssm -Arguments @('set', $ServiceName, 'AppStdout', (Join-Path $taskLogs 'stdout.log'))
Invoke-OfficeNssm -Arguments @('set', $ServiceName, 'AppStderr', (Join-Path $taskLogs 'stderr.log'))
Invoke-OfficeNssm -Arguments @('set', $ServiceName, 'AppRotateFiles', '1')
Invoke-OfficeNssm -Arguments @('set', $ServiceName, 'AppRotateOnline', '1')
Invoke-OfficeNssm -Arguments @('set', $ServiceName, 'AppRotateBytes', '10485760')
# 允许 ASP.NET Core 在控制台停止信号后完成正在处理的请求，再由 NSSM 兜底结束进程。
Invoke-OfficeNssm -Arguments @('set', $ServiceName, 'AppStopMethodConsole', '30000')
Invoke-OfficeNssm -Arguments @('set', $ServiceName, 'AppExit', 'Default', 'Restart')
Invoke-OfficeNssm -Arguments @('set', $ServiceName, 'AppRestartDelay', '5000')
if ($StartNow) { Invoke-OfficeNssm -Arguments @('start', $ServiceName) }
Write-Output "已注册服务：$ServiceName"
