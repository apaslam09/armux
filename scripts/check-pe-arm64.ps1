<#
.SYNOPSIS
  Fails unless every given PE file has machine type 0xAA64 (IMAGE_FILE_MACHINE_ARM64).

.DESCRIPTION
  Reads e_lfanew at offset 0x3C, checks the "PE\0\0" signature, then reads the
  16-bit Machine field that follows it. ARM64EC binaries report 0x8664 (x64) here
  and are rejected on purpose: armux ships pure ARM64.

.EXAMPLE
  pwsh scripts/check-pe-arm64.ps1 publish/armux.exe
#>
param([Parameter(Mandatory, ValueFromRemainingArguments)] [string[]] $Path)

$names = @{ 0xAA64 = 'ARM64'; 0x8664 = 'x64 (or ARM64EC)'; 0x014C = 'x86'; 0x01C4 = 'ARMv7' }
$failed = $false

foreach ($p in $Path) {
    if (-not (Test-Path -LiteralPath $p -PathType Leaf)) {
        "FAIL ${p}: file not found"
        $failed = $true
        continue
    }
    $bytes = [System.IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $p))
    # A DOS header needs 0x40 bytes before e_lfanew can be read.
    if ($bytes.Length -lt 0x40 -or $bytes[0] -ne 0x4D -or $bytes[1] -ne 0x5A) {
        "FAIL ${p}: not a PE file"
        $failed = $true
        continue
    }
    $peOffset = [BitConverter]::ToInt32($bytes, 0x3C)
    # The signature (4 bytes) and Machine field (2 bytes) must both fit.
    if ($peOffset -lt 0x40 -or $peOffset -gt ($bytes.Length - 6)) {
        "FAIL ${p}: not a PE file"
        $failed = $true
        continue
    }
    $sig = [BitConverter]::ToUInt32($bytes, $peOffset)
    if ($sig -ne 0x00004550) {
        "FAIL ${p}: not a PE file"
        $failed = $true
        continue
    }
    $machine = [BitConverter]::ToUInt16($bytes, $peOffset + 4)
    $label = $names[[int]$machine] ?? 'unknown'
    $line = '{0}: machine 0x{1:X4} ({2})' -f $p, $machine, $label
    if ($machine -eq 0xAA64) { "OK   $line" } else { "FAIL $line"; $failed = $true }
}

if ($failed) { exit 1 }
