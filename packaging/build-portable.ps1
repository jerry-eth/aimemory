[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+\.\d+$')]
    [string]$Version = '1.0.0.0',
    [string]$Configuration = 'Release',
    [ValidateSet('win-x64','win-arm64')]
    [string]$Runtime = 'win-x64',
    [switch]$KeepStage
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifacts = Join-Path $repo 'artifacts\portable'
$stage = Join-Path $repo 'artifacts\portable-stage'
$zipPath = Join-Path $artifacts ("AiMemoryManager_{0}_portable_{1}.zip" -f $Version, $Runtime)

function Assert-WorkspacePath([string]$path) {
    $full = [IO.Path]::GetFullPath($path)
    $root = $repo.TrimEnd('\') + '\'
    if (-not $full.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {
        throw "拒绝操作：路径不在仓库工作区内：$full"
    }
    return $full
}

$null = Assert-WorkspacePath $artifacts
$null = Assert-WorkspacePath $stage
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

Write-Host "发布便携版 ($Configuration / $Runtime / self-contained=true)..."
& dotnet publish (Join-Path $repo 'src\AiMemoryManager\AiMemoryManager.csproj') `
    --configuration $Configuration `
    --runtime $Runtime `
    --self-contained true `
    --output $stage
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败：$LASTEXITCODE" }

$readme = @"
AI 内存管家便携版
================

这是无需证书、无需安装的 Windows 便携版。

使用方法：
1. 将整个文件夹解压到本地磁盘。
2. 双击 AiMemoryManager.exe 启动。
3. 如需执行部分系统级清理或进程操作，请按系统提示授予管理员权限。
4. 卸载时退出程序后直接删除整个文件夹即可。

说明：
- 本包为 self-contained win-x64，不要求目标设备预装 .NET 8 Runtime。
- 不包含 API Key、密码、证书或私钥。
- 配置和运行日志仍按 Windows 用户目录保存，不会写入程序目录。
- 本包未使用 MSIX，因此没有安装快捷方式、自动更新和应用商店集成。
- Windows 可能对未签名程序显示安全提示，请确认来源后再运行。
"@
[IO.File]::WriteAllText((Join-Path $stage '便携版使用说明.txt'), $readme, [Text.UTF8Encoding]::new($false))

if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
Write-Host "生成便携压缩包：$zipPath"
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zipPath -CompressionLevel Optimal
$size = (Get-Item -LiteralPath $zipPath).Length
Write-Host ("便携版已生成：{0} ({1:N1} MB)" -f $zipPath, ($size / 1MB))

if (-not $KeepStage) { Remove-Item -LiteralPath $stage -Recurse -Force }
