<#
.SYNOPSIS
以 Windows x64、依赖 .NET 8 运行时的方式发布办公 API。
.DESCRIPTION
构建目录维持 SDK 默认值，串行发布；产物写入项目 artifacts/publish。
如遇文件被占用，请停止相关服务后重新运行，不切换构建目录。
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskProject = Join-Path $taskRoot 'src/OfficeMcp.Api/OfficeMcp.Api.csproj'
$taskOutput = Join-Path $taskRoot 'artifacts/publish'

# 仅创建发布产物，不安装、启动系统服务，也不复制本机凭据。
& dotnet publish $taskProject -c Release -r win-x64 --self-contained false -m:1 -o $taskOutput --nologo
if ($LASTEXITCODE -ne 0) { throw '发布失败，请检查上方构建输出。' }
Write-Output "发布目录：$taskOutput"
Write-Output '运行前请在发布目录配置 appsettings.Local.json，或设置环境变量。'
