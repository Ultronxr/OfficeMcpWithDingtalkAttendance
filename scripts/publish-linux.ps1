<#
.SYNOPSIS
发布可直接运行的 Linux x64 OfficeMcp 后端。
.DESCRIPTION
保持 SDK 默认构建目录，串行发布到 artifacts/publish-linux-x64；不包含本地凭据、不自动部署。
.PARAMETER RuntimeVersion
随发布包携带的 .NET 8 运行时补丁版本。
#>
[CmdletBinding()]
param([ValidatePattern('^8\.0\.\d+$')][string]$RuntimeVersion = '8.0.31')

$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskProject = Join-Path $taskRoot 'src/OfficeMcp.Api/OfficeMcp.Api.csproj'
$taskOutput = Join-Path $taskRoot 'artifacts/publish-linux-x64'

# 自包含包免除服务器安装 SDK／共享运行时；凭据仍由服务目录单独配置。
& dotnet publish $taskProject -c Release -r linux-x64 --self-contained true -m:1 -o $taskOutput "-p:RuntimeFrameworkVersion=$RuntimeVersion" --nologo
if ($LASTEXITCODE -ne 0) { throw 'Linux 发布失败，请检查构建输出。' }
Write-Output "Linux 发布目录：$taskOutput"
