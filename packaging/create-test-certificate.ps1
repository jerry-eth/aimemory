[CmdletBinding()]
param(
    [string]$OutputPath = (Join-Path (Get-Location) 'artifacts\certs\AiMemoryManager-Test.pfx'),
    [string]$CertificateSubject = 'CN=AiMemoryManager',
    [string]$Password = $env:AMM_TEST_CERT_PASSWORD
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($Password)) {
    throw '请通过 -Password 或 AMM_TEST_CERT_PASSWORD 提供开发证书密码；密码不得写入 Git。'
}

$resolvedOutput = [IO.Path]::GetFullPath($OutputPath)
New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($resolvedOutput)) | Out-Null
$securePassword = ConvertTo-SecureString $Password -AsPlainText -Force
$cert = New-SelfSignedCertificate `
    -Type Custom `
    -Subject $CertificateSubject `
    -FriendlyName 'AI Memory Manager development signing certificate' `
    -CertStoreLocation 'Cert:\CurrentUser\My' `
    -KeyAlgorithm RSA `
    -KeyLength 2048 `
    -HashAlgorithm SHA256 `
    -KeyUsage DigitalSignature `
    -NotAfter (Get-Date).AddYears(2)

Export-PfxCertificate -Cert $cert -FilePath $resolvedOutput -Password $securePassword | Out-Null
$cerPath = [IO.Path]::ChangeExtension($resolvedOutput, '.cer')
Export-Certificate -Cert $cert -FilePath $cerPath | Out-Null
Write-Output "开发证书已生成：$resolvedOutput"
Write-Output "受信任证书：$cerPath"
Write-Output "仅用于本机验证，不得用于 Microsoft Store 提交。"
