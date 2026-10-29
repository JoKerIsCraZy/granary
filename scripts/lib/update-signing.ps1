<#
.SYNOPSIS
    Shared by new-update-key.ps1 and sign-release.ps1: Granary's update signing key in the form the app reads
    (docs/release.md, "Signed releases").

.DESCRIPTION
    ECDSA on P-256 over SHA-256, the signature in IEEE P1363 form (r and s, 32 bytes each) and in base64. The public key is
    a SubjectPublicKeyInfo in PEM ("PUBLIC KEY"), the private key PKCS#8 in PEM ("PRIVATE KEY"). Uses Windows CNG, so it
    runs in Windows PowerShell 5.1 and in PowerShell 7 alike. Nothing here writes or prints a private key.
#>

# Where the app carries the public key; Granary.Core embeds it.
$script:UpdateKeyPath = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'src/Granary.Core/Updates/update-key.pub'

# A P-256 SubjectPublicKeyInfo: the algorithm (id-ecPublicKey on prime256v1), then the 65-byte uncompressed point.
$script:P256SpkiPrefix = [byte[]](0x30, 0x59, 0x30, 0x13, 0x06, 0x07, 0x2A, 0x86, 0x48, 0xCE, 0x3D, 0x02, 0x01, 0x06, 0x08,
    0x2A, 0x86, 0x48, 0xCE, 0x3D, 0x03, 0x01, 0x07, 0x03, 0x42, 0x00)

# BCRYPT_ECCKEY_BLOB of a P-256 public key: magic "ECS1", key length 32, then X and Y.
$script:EccPublicP256Magic = 0x31534345

function Read-PemBlock {
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Text, [Parameter(Mandatory)][string]$Label)
    $match = [regex]::Match($Text, "-----BEGIN $Label-----([A-Za-z0-9+/=\s]+)-----END $Label-----")
    if (-not $match.Success) {
        return $null
    }
    return , [Convert]::FromBase64String(($match.Groups[1].Value -replace '\s', ''))
}

function ConvertTo-Pem {
    param([Parameter(Mandatory)][byte[]]$Der, [Parameter(Mandatory)][string]$Label)
    $base64 = [Convert]::ToBase64String($Der)
    $lines = for ($i = 0; $i -lt $base64.Length; $i += 64) { $base64.Substring($i, [Math]::Min(64, $base64.Length - $i)) }
    return "-----BEGIN $Label-----`n" + ($lines -join "`n") + "`n-----END $Label-----`n"
}

function ConvertTo-P256Spki {
    param([Parameter(Mandatory)][byte[]]$EccPublicBlob)
    if ($EccPublicBlob.Length -ne 72 -or [BitConverter]::ToUInt32($EccPublicBlob, 0) -ne $script:EccPublicP256Magic -or
        [BitConverter]::ToUInt32($EccPublicBlob, 4) -ne 32) {
        throw 'The key is not a P-256 key.'
    }
    return , [byte[]]($script:P256SpkiPrefix + [byte[]]@(4) + $EccPublicBlob[8..71])
}

function ConvertFrom-P256Spki {
    param([Parameter(Mandatory)][byte[]]$Spki)
    if ($Spki.Length -ne 91 -or $Spki[26] -ne 4 -or
        [Convert]::ToBase64String($Spki[0..25]) -ne [Convert]::ToBase64String($script:P256SpkiPrefix)) {
        throw 'The public key is not a P-256 key.'
    }
    return , [byte[]]([BitConverter]::GetBytes([uint32]$script:EccPublicP256Magic) + [BitConverter]::GetBytes([uint32]32) + $Spki[27..90])
}

<#
The PowerShell logging policies turned on here, for Windows PowerShell and PowerShell 7, machine and user. Module logging
can keep what a pipeline carries; script block logging and transcription keep a session's input and output.
#>
function Get-PowerShellLoggingPolicy {
    $found = @()
    foreach ($hive in 'HKLM:', 'HKCU:') {
        foreach ($shell in 'SOFTWARE\Policies\Microsoft\Windows\PowerShell', 'SOFTWARE\Policies\Microsoft\PowerShellCore') {
            foreach ($policy in @(
                    @{ Key = 'ModuleLogging'; Value = 'EnableModuleLogging' },
                    @{ Key = 'ScriptBlockLogging'; Value = 'EnableScriptBlockLogging' },
                    @{ Key = 'Transcription'; Value = 'EnableTranscripting' })) {
                $path = "$hive\$shell\$($policy.Key)"
                $setting = Get-ItemProperty -LiteralPath $path -Name $policy.Value -ErrorAction SilentlyContinue
                if ($null -ne $setting -and $setting.($policy.Value) -eq 1) {
                    $found += $path
                }
            }
        }
    }
    return , [string[]]$found
}

<#
A new P-256 key that lives only in this process: no name, so Windows keeps nothing of it once it is disposed. Its private
part can be exported once, for gh secret set.
#>
function New-EphemeralP256Key {
    $parameters = [System.Security.Cryptography.CngKeyCreationParameters]::new()
    $parameters.ExportPolicy = [System.Security.Cryptography.CngExportPolicies]::AllowPlaintextExport
    # [NullString]::Value, not $null: PowerShell passes $null to a string as "", and CNG stores a key under that name.
    $key = [System.Security.Cryptography.CngKey]::Create([System.Security.Cryptography.CngAlgorithm]::ECDsaP256, [NullString]::Value,
        $parameters)
    if (-not $key.IsEphemeral) {
        $key.Delete()
        throw 'Windows stored the new key instead of keeping it in memory; it was deleted again.'
    }
    return $key
}

<# The public key in update-key.pub, as a CNG public blob. Fails while the file is the placeholder. #>
function Read-UpdatePublicKey {
    param([string]$Path = $script:UpdateKeyPath)
    $text = [IO.File]::ReadAllText($Path)
    if ($text -match 'PRIVATE KEY') {
        throw "$Path holds a private key. It must never be committed: delete it, and make a new key (docs/release.md)."
    }
    $spki = Read-PemBlock -Text $text -Label 'PUBLIC KEY'
    if ($null -eq $spki) {
        throw "$Path has no update signing key yet. Run scripts/new-update-key.ps1 once (docs/release.md, 'Signed releases')."
    }
    return , (ConvertFrom-P256Spki -Spki $spki)
}

<# The P-256 key in a PKCS#8 PEM, opened for signing; the caller disposes it. The PEM is never echoed. #>
function Open-UpdatePrivateKey {
    param([Parameter(Mandatory)][string]$Pem)
    $der = Read-PemBlock -Text $Pem -Label 'PRIVATE KEY'
    if ($null -eq $der) {
        throw 'The signing key is not a private key in PKCS#8 PEM ("BEGIN PRIVATE KEY").'
    }
    try {
        $key = [System.Security.Cryptography.CngKey]::Import($der, [System.Security.Cryptography.CngKeyBlobFormat]::Pkcs8PrivateBlob)
    }
    catch {
        throw 'The signing key could not be read as a P-256 private key.'
    }
    finally {
        [Array]::Clear($der, 0, $der.Length)
    }
    if (-not $key.IsEphemeral) {
        $key.Delete()
        throw 'Windows stored the signing key instead of keeping it in memory; it was deleted again.'
    }
    if ($key.Algorithm -ne [System.Security.Cryptography.CngAlgorithm]::ECDsaP256) {
        $key.Dispose()
        throw 'The signing key is not a P-256 ECDSA key.'
    }
    return $key
}

function New-UpdateSignature {
    param([Parameter(Mandatory)][System.Security.Cryptography.CngKey]$Key, [Parameter(Mandatory)][byte[]]$Data)
    $ecdsa = [System.Security.Cryptography.ECDsaCng]::new($Key)
    try {
        # CNG signs in IEEE P1363 form, r and s side by side, as the app reads it.
        return , $ecdsa.SignData($Data, [System.Security.Cryptography.HashAlgorithmName]::SHA256)
    }
    finally {
        $ecdsa.Dispose()
    }
}

function Test-UpdateSignature {
    param([Parameter(Mandatory)][byte[]]$PublicBlob, [Parameter(Mandatory)][byte[]]$Data, [Parameter(Mandatory)][byte[]]$Signature)
    if ($Signature.Length -ne 64) {
        return $false
    }
    $key = [System.Security.Cryptography.CngKey]::Import($PublicBlob, [System.Security.Cryptography.CngKeyBlobFormat]::EccPublicBlob)
    $ecdsa = [System.Security.Cryptography.ECDsaCng]::new($key)
    try {
        return $ecdsa.VerifyData($Data, $Signature, [System.Security.Cryptography.HashAlgorithmName]::SHA256)
    }
    finally {
        $ecdsa.Dispose()
        $key.Dispose()
    }
}
