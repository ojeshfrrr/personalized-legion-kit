[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)][string]$ApplicationDirectory,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{40}$')][string]$CertificateThumbprint,
    [Parameter(Mandatory)][uri]$TimestampUrl,
    [Parameter(Mandatory)][string]$SignTool
)
$ErrorActionPreference = 'Stop'
$ApplicationDirectory = [IO.Path]::GetFullPath($ApplicationDirectory)
if ($TimestampUrl.Scheme -ne 'https') { throw 'An HTTPS RFC3161 timestamp service is required.' }
if (Get-ChildItem -LiteralPath (Split-Path $ApplicationDirectory) -Filter '*.zip' -File) {
    throw 'Use a new staging directory before packaging; never modify an existing release candidate.'
}
$certificate = Get-Item "Cert:\CurrentUser\My\$CertificateThumbprint"
if (!$certificate.HasPrivateKey -or $certificate.NotAfter -le (Get-Date) -or
    $certificate.EnhancedKeyUsageList.ObjectId.Value -notcontains '1.3.6.1.5.5.7.3.3') { throw 'A valid code-signing certificate with an accessible key is required.' }
$files = @('Project Legion.exe', 'Project Legion.dll', 'Project Legion UpdateHost.exe', 'Project Legion UpdateHost.dll',
    'LenovoLegionToolkit.Lib.dll', 'LenovoLegionToolkit.Lib.Automation.dll', 'LenovoLegionToolkit.Lib.Macro.dll', 'LenovoLegionToolkit.CLI.Lib.dll')
$results = @()
foreach ($name in $files) {
    $path = Join-Path $ApplicationDirectory $name
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing owned binary: $name" }
    if (!$PSCmdlet.ShouldProcess($path, 'Sign and timestamp with the configured certificate')) { continue }
    $before = (Get-FileHash -LiteralPath $path).Hash
    & $SignTool sign /sha1 $CertificateThumbprint /fd SHA256 /tr $TimestampUrl.AbsoluteUri /td SHA256 $path
    if ($LASTEXITCODE -ne 0) { throw "Signing failed: $name" }
    & $SignTool verify /pa /all $path
    if ($LASTEXITCODE -ne 0) { throw "Authenticode verification failed: $name" }
    $signature = Get-AuthenticodeSignature -LiteralPath $path
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Thumbprint -ne $CertificateThumbprint -or !$signature.TimeStamperCertificate) {
        throw "Signer/timestamp verification failed: $name"
    }
    $results += [pscustomobject]@{File=$name;UnsignedSHA256=$before;SignedSHA256=(Get-FileHash -LiteralPath $path).Hash;Signer=$CertificateThumbprint}
}
if ($results.Count) { $results | ConvertTo-Json | Set-Content (Join-Path $ApplicationDirectory 'SIGNING-RECORD.txt') }
