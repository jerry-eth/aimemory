[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+\.\d+$')]
    [string]$Version = '1.0.0.0',
    [string]$Publisher = 'CN=AiMemoryManager',
    [string]$IdentityName = 'AiMemoryManager',
    [string]$Configuration = 'Release',
    [ValidateSet('win-x64','win-arm64')]
    [string]$Runtime = 'win-x64',
    [string]$CertificatePath,
    [string]$CertificatePassword,
    [switch]$SelfContained,
    [switch]$StoreCompatible,
    [switch]$KeepStage
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifacts = Join-Path $repo 'artifacts\msix'
$stage = Join-Path $repo 'artifacts\msix-stage'
$publish = $stage
$manifestName = if ($StoreCompatible) { 'Package.Store.appxmanifest' } else { 'Package.appxmanifest' }
$manifestTemplate = Join-Path $PSScriptRoot $manifestName
$assetSource = Join-Path $PSScriptRoot 'Assets'
$packageSuffix = if ($StoreCompatible) { "_store" } else { "" }
$packagePath = Join-Path $artifacts ("AiMemoryManager_{0}{1}_{2}.msix" -f $Version,$packageSuffix,$Runtime)

function Assert-WorkspacePath([string]$path) {
    $full = [IO.Path]::GetFullPath($path)
    $root = $repo.TrimEnd('\') + '\'
    if (-not $full.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {
        throw "拒绝操作：路径不在仓库工作区内：$full"
    }
    return $full
}

$null = Assert-WorkspacePath $stage
$null = Assert-WorkspacePath $artifacts
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null
if (Test-Path $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $publish | Out-Null

$selfContainedValue = if ($SelfContained) { 'true' } else { 'false' }
Write-Host "发布 WPF 应用 ($Configuration / $Runtime / self-contained=$selfContainedValue / store=$StoreCompatible)..."
$publishProperties = @()
if ($StoreCompatible) { $publishProperties += '/p:StoreCompatible=true' }
& dotnet publish (Join-Path $repo 'src\AiMemoryManager\AiMemoryManager.csproj') `
    --configuration $Configuration `
    --runtime $Runtime `
    --self-contained $selfContainedValue `
    --output $publish `
    @publishProperties
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败：$LASTEXITCODE" }

Copy-Item -LiteralPath $manifestTemplate -Destination (Join-Path $stage 'AppxManifest.xml')
New-Item -ItemType Directory -Force -Path (Join-Path $stage 'Assets') | Out-Null
Copy-Item -Path (Join-Path $assetSource '*.png') -Destination (Join-Path $stage 'Assets') -Force

$manifestPath = Join-Path $stage 'AppxManifest.xml'
$manifest = [IO.File]::ReadAllText($manifestPath)
$identityReplacement = 'Name="{0}" Publisher="{1}" Version="{2}"' -f `
    $IdentityName, $Publisher, $Version
$manifest = $manifest.Replace(
    'Name="AiMemoryManager" Publisher="CN=AiMemoryManager" Version="1.0.0.0"',
    $identityReplacement)
[IO.File]::WriteAllText($manifestPath, $manifest, [Text.UTF8Encoding]::new($false))

$sdkRoots = @(
    (Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'),
    (Join-Path $env:ProgramFiles 'Windows Kits\10\bin'),
    'C:\Program Files (x86)\Windows Kits\10\bin'
) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique
$sdkTools = foreach ($sdkRoot in $sdkRoots) {
    Get-ChildItem -Path $sdkRoot -Recurse -File -ErrorAction SilentlyContinue
}
$makeAppx = $sdkTools | Where-Object Name -ieq 'makeappx.exe' |
    Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
if ([string]::IsNullOrWhiteSpace($makeAppx)) { throw '未找到 Windows SDK makeappx.exe，请安装 Windows 10/11 SDK。' }
if (Test-Path $packagePath) { Remove-Item -LiteralPath $packagePath -Force }
Write-Host "生成 MSIX：$packagePath"
& $makeAppx pack /d $stage /p $packagePath /nv
if ($LASTEXITCODE -ne 0) { throw "makeappx pack 失败：$LASTEXITCODE" }

if (-not [string]::IsNullOrWhiteSpace($CertificatePath)) {
    $cert = [IO.Path]::GetFullPath($CertificatePath)
    if (-not (Test-Path -LiteralPath $cert)) { throw "签名证书不存在：$cert" }
    $signtool = $sdkTools | Where-Object Name -ieq 'signtool.exe' |
        Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
    if ([string]::IsNullOrWhiteSpace($signtool)) { throw '未找到 Windows SDK signtool.exe。' }
    if ([string]::IsNullOrWhiteSpace($CertificatePassword)) {
        & $signtool sign /fd SHA256 /a /f $cert $packagePath
    } else {
        & $signtool sign /fd SHA256 /a /f $cert /p $CertificatePassword $packagePath
    }
    if ($LASTEXITCODE -ne 0) { throw "signtool sign 失败：$LASTEXITCODE" }
}

$size = (Get-Item -LiteralPath $packagePath).Length
Write-Host ("MSIX 已生成：{0} ({1:N1} MB)" -f $packagePath,($size/1MB))
if (-not $SelfContained) {
    Write-Warning '当前包为 framework-dependent，目标电脑需要 .NET 8 Desktop Runtime；商店提交前请确认依赖策略或使用 -SelfContained。'
}
if (-not $KeepStage) { Remove-Item -LiteralPath $stage -Recurse -Force }
