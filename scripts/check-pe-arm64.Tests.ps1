BeforeAll {
    $script:checker = Join-Path $PSScriptRoot 'check-pe-arm64.ps1'
    $script:powerShell = (Get-Process -Id $PID).Path

    function New-PeFixture {
        param(
            [string] $FilePath,
            [int] $Machine,
            [int] $PeOffset = 0x80,
            [int] $Size = 0x100
        )
        $bytes = [byte[]]::new($Size)
        $bytes[0] = 0x4D
        $bytes[1] = 0x5A
        [BitConverter]::GetBytes([int] $PeOffset).CopyTo($bytes, 0x3C)
        if ($PeOffset -ge 0 -and $PeOffset -le ($Size - 4)) {
            [BitConverter]::GetBytes([uint32] 0x00004550).CopyTo($bytes, $PeOffset)
        }
        if ($PeOffset -ge 0 -and $PeOffset -le ($Size - 6)) {
            [BitConverter]::GetBytes([uint16] $Machine).CopyTo($bytes, ($PeOffset + 4))
        }
        [System.IO.File]::WriteAllBytes($FilePath, $bytes)
    }

    function Invoke-PeChecker {
        param([string[]] $FilePaths)
        $lines = & $script:powerShell -NoProfile -NonInteractive -File $script:checker @FilePaths 2>&1
        [pscustomobject]@{
            ExitCode = $LASTEXITCODE
            Output = ($lines -join "`n").TrimEnd()
        }
    }
}

Describe 'check-pe-arm64.ps1' {
    It 'accepts a valid ARM64 PE header' {
        $file = Join-Path $TestDrive 'arm64.exe'
        New-PeFixture -FilePath $file -Machine 0xAA64
        $result = Invoke-PeChecker -FilePaths @($file)
        $result.ExitCode | Should -Be 0
        $result.Output | Should -BeExactly "OK   ${file}: machine 0xAA64 (ARM64)"
    }

    It 'rejects x64 and ARM64EC machine 0x8664' {
        $file = Join-Path $TestDrive 'x64.exe'
        New-PeFixture -FilePath $file -Machine 0x8664
        $result = Invoke-PeChecker -FilePaths @($file)
        $result.ExitCode | Should -Be 1
        $result.Output | Should -BeExactly "FAIL ${file}: machine 0x8664 (x64 (or ARM64EC))"
    }

    It 'rejects x86 machine 0x014C' {
        $file = Join-Path $TestDrive 'x86.exe'
        New-PeFixture -FilePath $file -Machine 0x014C
        $result = Invoke-PeChecker -FilePaths @($file)
        $result.ExitCode | Should -Be 1
        $result.Output | Should -BeExactly "FAIL ${file}: machine 0x014C (x86)"
    }

    It 'rejects non-PE content without an exception' {
        $file = Join-Path $TestDrive 'not-pe.bin'
        [System.IO.File]::WriteAllBytes($file, [byte[]]::new(0x100))
        $result = Invoke-PeChecker -FilePaths @($file)
        $result.ExitCode | Should -Be 1
        $result.Output | Should -BeExactly "FAIL ${file}: not a PE file"
    }

    It 'rejects an empty file without an exception' {
        $file = Join-Path $TestDrive 'empty.bin'
        [System.IO.File]::WriteAllBytes($file, [byte[]]::new(0))
        $result = Invoke-PeChecker -FilePaths @($file)
        $result.ExitCode | Should -Be 1
        $result.Output | Should -BeExactly "FAIL ${file}: not a PE file"
    }

    It 'rejects a 63-byte DOS header without an exception' {
        $file = Join-Path $TestDrive 'tiny.exe'
        $bytes = [byte[]]::new(0x3F)
        $bytes[0] = 0x4D
        $bytes[1] = 0x5A
        [System.IO.File]::WriteAllBytes($file, $bytes)
        $result = Invoke-PeChecker -FilePaths @($file)
        $result.ExitCode | Should -Be 1
        $result.Output | Should -BeExactly "FAIL ${file}: not a PE file"
    }

    It 'rejects an e_lfanew offset beyond the file end' {
        $file = Join-Path $TestDrive 'offset-past-end.exe'
        New-PeFixture -FilePath $file -Machine 0xAA64 -PeOffset 0x100
        $result = Invoke-PeChecker -FilePaths @($file)
        $result.ExitCode | Should -Be 1
        $result.Output | Should -BeExactly "FAIL ${file}: not a PE file"
    }

    It 'rejects an e_lfanew offset leaving too few bytes for Machine' {
        $file = Join-Path $TestDrive 'truncated-machine.exe'
        New-PeFixture -FilePath $file -Machine 0xAA64 -PeOffset 0xFB
        $result = Invoke-PeChecker -FilePaths @($file)
        $result.ExitCode | Should -Be 1
        $result.Output | Should -BeExactly "FAIL ${file}: not a PE file"
    }

    It 'rejects a negative e_lfanew offset without an exception' {
        $file = Join-Path $TestDrive 'negative-offset.exe'
        New-PeFixture -FilePath $file -Machine 0xAA64 -PeOffset (-1)
        $result = Invoke-PeChecker -FilePaths @($file)
        $result.ExitCode | Should -Be 1
        $result.Output | Should -BeExactly "FAIL ${file}: not a PE file"
    }

    It 'rejects an invalid PE signature without an exception' {
        $file = Join-Path $TestDrive 'bad-signature.exe'
        New-PeFixture -FilePath $file -Machine 0xAA64
        $bytes = [System.IO.File]::ReadAllBytes($file)
        $bytes[0x80] = 0
        [System.IO.File]::WriteAllBytes($file, $bytes)
        $result = Invoke-PeChecker -FilePaths @($file)
        $result.ExitCode | Should -Be 1
        $result.Output | Should -BeExactly "FAIL ${file}: not a PE file"
    }

    It 'reports a missing file with exit code 1' {
        $file = Join-Path $TestDrive 'missing.exe'
        $result = Invoke-PeChecker -FilePaths @($file)
        $result.ExitCode | Should -Be 1
        $result.Output | Should -BeExactly "FAIL ${file}: file not found"
    }

    It 'rejects a mixed batch while preserving successful output' {
        $good = Join-Path $TestDrive 'good.exe'
        $bad = Join-Path $TestDrive 'bad.exe'
        New-PeFixture -FilePath $good -Machine 0xAA64
        [System.IO.File]::WriteAllBytes($bad, [byte[]]::new(2))
        $result = Invoke-PeChecker -FilePaths @($good, $bad)
        $result.ExitCode | Should -Be 1
        $result.Output | Should -Match "(?m)^OK   .*good\.exe: machine 0xAA64 \(ARM64\)$"
        $result.Output | Should -Match "(?m)^FAIL .*bad\.exe: not a PE file$"
    }
}
