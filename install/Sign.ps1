<#
.SYNOPSIS
  Sign the Shellvis binaries and the installer, or say clearly that nothing was signed.

.DESCRIPTION
  WHY THIS EXISTS. An unsigned executable is refused outright on a managed desktop. Measured
  on the machine this was written on: Defender's rule "block executable files from running
  unless they meet a prevalence, age, or trusted list criterion" (rule
  01443614-CD74-433A-B99E-2ECDC07BFC25) blocked Shellvis.Shell.exe under
  %LOCALAPPDATA%\Programs\Shellvis for the user and for SYSTEM both, event 1121, while the
  byte-identical file ran from a fixed path. A signature from a signer the machine trusts is
  what that rule is looking for; installing into %ProgramFiles% only avoids the question.

  WHAT IT DOES NOT DO. It does not obtain a certificate, and a self-signed one does not help:
  the issuer has to be trusted where the software runs. Two things do work.

    An internal PKI certificate, for software distributed inside one organisation. The
    domain already trusts its own root, so a binary signed by it is signed by a trusted
    publisher on every member machine. It needs a Code Signing template published on the
    issuing CA and enrolment permission -- neither of which is a thing this script can
    arrange, and on the machine this was written on the only template offered was computer
    authentication.

    A public certificate, for the releases on GitHub. An OV certificate builds reputation
    over weeks; an EV certificate or Azure Trusted Signing carries it immediately.

  WHY IT NEVER FAILS THE BUILD BY DEFAULT. The public CI has no certificate and must still
  produce a package. An unsigned build is a fact to state, not an error -- but a build that
  was MEANT to be signed and quietly was not is worse than either, so -Require turns a
  missing certificate into a failure for the pipeline that does have one.

.PARAMETER Path
  Files to sign. Missing paths are reported rather than ignored.

.PARAMETER Thumbprint
  A certificate already in a store, which is how an internal PKI and a signing machine work.

.PARAMETER PfxPath
  A .pfx file, which is how a CI secret arrives. Read with -PfxPassword.

.PARAMETER TimestampServer
  RFC 3161 timestamp authority. Load-bearing: without a timestamp a signature stops being
  valid the day the certificate expires, so every release signed this year would go bad at
  once. Defaults to DigiCert's free service.

.PARAMETER Require
  Fail when no certificate is configured, instead of reporting the files as unsigned.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string[]] $Path,
    [string] $Thumbprint = $env:SHELLVIS_SIGN_THUMBPRINT,
    [string] $PfxPath = $env:SHELLVIS_SIGN_PFX,
    [string] $PfxPassword = $env:SHELLVIS_SIGN_PFX_PASSWORD,
    [string] $TimestampServer = 'http://timestamp.digicert.com',
    [switch] $Require
)

$ErrorActionPreference = 'Stop'

# Resolved first and reported as a list, because "sign the binaries" silently signing three
# of four is the failure this cannot afford: the one it missed is the one that gets blocked.
$files = @()

foreach ($candidate in $Path) {
    $resolved = @(Get-ChildItem -Path $candidate -File -ErrorAction SilentlyContinue)

    if ($resolved.Count -eq 0) {
        Write-Host "  !!   nothing at $candidate"
        continue
    }

    $files += $resolved
}

if ($files.Count -eq 0) {
    throw 'nothing to sign: none of the given paths matched a file.'
}

# ------------------------------------------------------------------ the certificate

$certificate = $null

if ($Thumbprint) {
    # Both stores, because a signing certificate lives in the machine store on a build
    # server and in the user store on a workstation, and which one is not worth a parameter.
    foreach ($store in 'Cert:\CurrentUser\My', 'Cert:\LocalMachine\My') {
        $found = Get-ChildItem $store -ErrorAction SilentlyContinue |
            Where-Object { $_.Thumbprint -eq $Thumbprint.Replace(' ', '') }

        if ($found) {
            $certificate = $found[0]
            Write-Host "  ok   certificate from $store"
            break
        }
    }

    if (-not $certificate) {
        throw "no certificate with thumbprint $Thumbprint in either personal store."
    }
}
elseif ($PfxPath) {
    if (-not (Test-Path $PfxPath)) {
        throw "no .pfx at $PfxPath"
    }

    $certificate = if ($PfxPassword) {
        $secure = ConvertTo-SecureString $PfxPassword -AsPlainText -Force
        Get-PfxCertificate -FilePath $PfxPath -Password $secure
    }
    else {
        Get-PfxCertificate -FilePath $PfxPath
    }

    Write-Host "  ok   certificate from $(Split-Path $PfxPath -Leaf)"
}

if (-not $certificate) {
    $message = @(
        'no signing certificate configured, so nothing was signed.'
        'Set SHELLVIS_SIGN_THUMBPRINT to a certificate in a personal store, or'
        'SHELLVIS_SIGN_PFX and SHELLVIS_SIGN_PFX_PASSWORD to a file.'
        'These files will be refused on a managed desktop; see the comment at the top of'
        'this script for why and for what kind of certificate actually helps.'
    ) -join ' '

    if ($Require) {
        throw $message
    }

    Write-Host "  ..   $message"

    foreach ($file in $files) {
        Write-Host "       unsigned: $($file.Name)"
    }

    exit 0
}

# A certificate without a private key can verify a signature and cannot make one. Caught
# here rather than at the first file, because the message from Set-AuthenticodeSignature
# does not name the cause.
if (-not $certificate.HasPrivateKey) {
    throw "the certificate $($certificate.Subject) has no private key, so it cannot sign."
}

if ($certificate.NotAfter -lt (Get-Date)) {
    throw "the certificate expired on $($certificate.NotAfter.ToString('yyyy-MM-dd'))."
}

Write-Host "       subject:   $($certificate.Subject)"
Write-Host "       issuer:    $($certificate.Issuer)"
Write-Host "       valid to:  $($certificate.NotAfter.ToString('yyyy-MM-dd'))"
Write-Host ''

# ------------------------------------------------------------------ signing

$failures = 0
$untrusted = 0

foreach ($file in $files) {
    # Already-signed files are re-signed rather than skipped. A stale signature from a
    # previous certificate verifies as valid and is not the one that was asked for, so
    # "already signed" is not the same question as "signed with this".
    $result = Set-AuthenticodeSignature `
        -FilePath $file.FullName `
        -Certificate $certificate `
        -TimestampServer $TimestampServer `
        -HashAlgorithm SHA256 `
        -ErrorAction Continue

    # THREE outcomes, not two, and the middle one cost a confusing failure to notice.
    #
    # A signature can be written and still not verify HERE, because verifying walks the
    # chain against this machine's trust: a self-signed certificate and an internal PKI
    # certificate on a runner outside the domain both land there. The bytes are signed. So
    # the question "was a signature written" and the question "does this machine trust the
    # issuer" are separated, because conflating them reports a working signing step as
    # broken -- and reports a release signed by nobody as fine if you squint at the wrong
    # one.
    $signed = $result.SignerCertificate -and
        $result.SignerCertificate.Thumbprint -eq $certificate.Thumbprint

    if ($result.Status -eq 'Valid') {
        Write-Host "  ok   $($file.Name)"
    }
    elseif ($signed) {
        Write-Host "  ..   $($file.Name)  signed, but this machine does not trust the issuer"
        $untrusted++
    }
    else {
        Write-Host "  FAIL $($file.Name)  -> $($result.Status): $($result.StatusMessage)"
        $failures++
    }
}

Write-Host ''

if ($failures -gt 0) {
    throw "$failures of $($files.Count) file(s) came away with no signature at all."
}

if ($untrusted -gt 0) {
    $note = "$untrusted of $($files.Count) file(s) are signed by an issuer this machine does " +
            'not trust. That is expected for a self-signed certificate and for an internal ' +
            'PKI certificate used off the domain; it is not acceptable in a release, because ' +
            'the machines that refuse an unsigned binary will refuse this one too.'

    # -Require is the pipeline that MEANS to ship a signature, so there it is a failure.
    if ($Require) {
        throw $note
    }

    Write-Host "  ..   $note"
}

Write-Host "signed $($files.Count) file(s) with $($certificate.Subject), timestamped by $TimestampServer."
