<#
.SYNOPSIS
Safely prepares a UEFI-bootable USB flash drive for SharpMetal on Windows.

.DESCRIPTION
Mirrors build/scripts/Make-UsbBootable.sh for native Windows hosts:
- Validates build artifacts (BOOTX64.EFI, INITRD.IMG, nvme.img, startup.nsh).
- Refuses system/boot/read-only disks and non-USB disks (unless -ForceNonRemovable is passed).
- Wipes the target disk, initializes GPT, creates a 256 MiB FAT32 partition
  labeled SHARPMETAL, stages SharpMetal payloads + userland PIE binaries,
  verifies file sizes, flushes disk buffers, and marks Partition 1 as an
  EFI System Partition ({c12a7328-f81f-11d2-ba4b-00a0c93ec93b}).

.EXAMPLE
.\build\scripts\Make-UsbBootable.ps1 -List

.EXAMPLE
.\build\scripts\Make-UsbBootable.ps1 -DiskNumber 2
#>
[CmdletBinding()]
param(
    [switch]$List,
    [int]$DiskNumber = -1,
    [switch]$Yes,
    [switch]$Build,
    [switch]$ForceNonRemovable,
    [switch]$KeepDriveLetter,
    [string]$LogFile
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$ScriptPath = $MyInvocation.MyCommand.Path
$RepoRoot = (Resolve-Path (Join-Path (Split-Path -Parent $ScriptPath) '..\..')).Path
$BuildDir = Join-Path $RepoRoot 'build'
$EfiGuid = '{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}'

function Test-IsAdministrator {
    $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Show-DiskList {
    Write-Host 'Available physical disks on host:'
    Get-Disk | Sort-Object Number | Format-Table Number, FriendlyName, BusType, PartitionStyle,
        @{ Label = 'SizeGB'; Expression = { [math]::Round($_.Size / 1GB, 2) } },
        IsBoot, IsSystem, IsReadOnly, OperationalStatus -AutoSize | Out-Host
}

function Find-McopyExecutable {
    $cmd = Get-Command mcopy.exe -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($cmd) { return $cmd.Source }
    $scoopMcopy = Join-Path $env:USERPROFILE 'scoop\apps\msys2\current\ucrt64\bin\mcopy.exe'
    if (Test-Path $scoopMcopy) { return $scoopMcopy }
    return $null
}

function Find-BashExecutable {
    $candidates = @(
        'C:\Program Files\Git\bin\bash.exe',
        (Join-Path $env:USERPROFILE 'scoop\apps\git\current\bin\bash.exe')
    )
    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) { return $candidate }
    }
    $cmds = Get-Command bash.exe -All -ErrorAction SilentlyContinue | Where-Object {
        $_.Source -notmatch '\\Windows\\System32\\bash\.exe$'
    }
    $first = $cmds | Select-Object -First 1
    if ($first) { return $first.Source }
    return $null
}

function Invoke-SelfElevated([string]$EffectiveLogFile) {
    $argList = @(
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', "`"$ScriptPath`"",
        '-DiskNumber', $DiskNumber,
        '-Yes',
        '-LogFile', "`"$EffectiveLogFile`""
    )
    if ($ForceNonRemovable) { $argList += '-ForceNonRemovable' }
    if ($KeepDriveLetter) { $argList += '-KeepDriveLetter' }

    Write-Host "[*] Requesting UAC Administrator elevation to format and flash Disk $DiskNumber..."
    try {
        $proc = Start-Process -FilePath 'powershell.exe' -ArgumentList $argList -Verb RunAs -PassThru -Wait
        if (Test-Path $EffectiveLogFile) {
            Get-Content $EffectiveLogFile | Out-Host
        }
        return $proc.ExitCode
    } catch {
        throw "Could not launch UAC prompt from this session ($($_.Exception.Message)). Open an Administrator PowerShell window and run: powershell -NoProfile -ExecutionPolicy Bypass -File `"$ScriptPath`" -DiskNumber $DiskNumber"
    }
}

function Assert-TargetDiskSafety([int]$TargetDiskNumber) {
    $disk = Get-Disk -Number $TargetDiskNumber -ErrorAction Stop
    if ($disk.IsBoot -or $disk.IsSystem) {
        throw "Refusing to modify Disk $TargetDiskNumber ($($disk.FriendlyName)): marked as Boot/System disk."
    }
    if ($disk.IsReadOnly) {
        throw "Disk $TargetDiskNumber ($($disk.FriendlyName)) is read-only."
    }
    if ($disk.BusType -ne 'USB' -and -not $ForceNonRemovable) {
        throw "Disk $TargetDiskNumber ($($disk.FriendlyName)) has BusType='$($disk.BusType)' (not USB). Pass -ForceNonRemovable to override."
    }
    $protectedLetters = @($env:SystemDrive.TrimEnd(':'))
    $existingParts = Get-Partition -DiskNumber $TargetDiskNumber -ErrorAction SilentlyContinue
    foreach ($p in $existingParts) {
        if ($p.DriveLetter -and [char]::IsLetter([char]$p.DriveLetter) -and ($protectedLetters -contains ([string]$p.DriveLetter).ToUpperInvariant())) {
            throw "Refusing to modify Disk ${TargetDiskNumber}: holds system drive $($p.DriveLetter):."
        }
    }
    return $disk
}

function Assert-BuildArtifacts {
    $efiPath = Join-Path $BuildDir 'BOOTX64.EFI'
    $initrdPath = Join-Path $BuildDir 'esp\EFI\BOOT\INITRD.IMG'
    $nvmePath = Join-Path $BuildDir 'nvme.img'
    foreach ($req in @($efiPath, $initrdPath, $nvmePath)) {
        if (-not (Test-Path $req)) {
            throw "Missing required build artifact: $req. Run Make-DiskImage.sh (or pass -Build) first."
        }
    }
    return @{
        Efi = $efiPath
        Initrd = $initrdPath
        Nvme = $nvmePath
        Startup = (Join-Path $BuildDir 'esp\startup.nsh')
    }
}

function Copy-UsbPayloads([string]$RootDrive, [hashtable]$Artifacts) {
    $efiBootDir = Join-Path $RootDrive 'EFI\BOOT'
    $binDir = Join-Path $RootDrive 'bin'
    New-Item -ItemType Directory -Force -Path $efiBootDir | Out-Null
    New-Item -ItemType Directory -Force -Path $binDir | Out-Null

    $copyPairs = @(
        @{ Src = $Artifacts.Efi; Dst = (Join-Path $efiBootDir 'BOOTX64.EFI') },
        @{ Src = $Artifacts.Initrd; Dst = (Join-Path $efiBootDir 'INITRD.IMG') },
        @{ Src = $Artifacts.Nvme; Dst = (Join-Path $RootDrive 'NVME.IMG') }
    )
    if (Test-Path $Artifacts.Startup) {
        $copyPairs += @{ Src = $Artifacts.Startup; Dst = (Join-Path $RootDrive 'startup.nsh') }
    }

    foreach ($pair in $copyPairs) {
        Copy-Item -Force $pair.Src $pair.Dst
        $srcLen = (Get-Item $pair.Src).Length
        $dstLen = (Get-Item $pair.Dst).Length
        if ($srcLen -ne $dstLen) {
            throw "Verification mismatch for $($pair.Dst): expected $srcLen bytes, got $dstLen."
        }
    }

    [System.IO.File]::WriteAllText((Join-Path $RootDrive 'HELLO.TXT'), 'SharpMetal BareMetal OS', (New-Object System.Text.UTF8Encoding $false))

    $mcopy = Find-McopyExecutable
    if ($mcopy) {
        $prevSkip = $env:MTOOLS_SKIP_CHECK
        $env:MTOOLS_SKIP_CHECK = '1'
        Push-Location $binDir
        try {
            foreach ($pie in @('test.pie', 'posix_test', 'hello.pie', 'cat.pie')) {
                # Pass a relative filename because Windows mtools treats 'X:\...' as a DOS drive prefix.
                & $mcopy -o -i $Artifacts.Nvme "::/bin/$pie" ".\$pie" 2>$null
                if ($LASTEXITCODE -ne 0) {
                    Write-Warning "Could not extract ::/bin/$pie from nvme.img (mcopy exit $LASTEXITCODE)."
                }
            }
        } finally {
            Pop-Location
            $env:MTOOLS_SKIP_CHECK = $prevSkip
        }
    } else {
        Write-Warning 'mcopy.exe not found; skipped copying ::/bin/*.pie from nvme.img.'
    }
}

$transcriptStarted = $false
if ($LogFile) {
    Start-Transcript -Path $LogFile -Force | Out-Null
    $transcriptStarted = $true
}

$exitCode = 0
try {
    if ($List -or $DiskNumber -lt 0) {
        Write-Host "Usage: .\build\scripts\Make-UsbBootable.ps1 -DiskNumber <N> [-Yes] [-Build] [-KeepDriveLetter] [-ForceNonRemovable]`n"
        Show-DiskList
        if (-not $List) { $exitCode = 1 }
    } else {
        if ($Build) {
            $bash = Find-BashExecutable
            if (-not $bash) { throw 'bash.exe not found in PATH or C:\Program Files\Git\bin.' }
            Write-Host '[*] Building SharpMetal disk image via Make-DiskImage.sh...'
            & $bash (Join-Path $BuildDir 'scripts\Make-DiskImage.sh')
            if ($LASTEXITCODE -ne 0) { throw "Make-DiskImage.sh failed with exit code $LASTEXITCODE" }
        }

        $artifacts = Assert-BuildArtifacts
        $disk = Assert-TargetDiskSafety $DiskNumber

        Write-Host '================================================================='
        Write-Host '   SharpMetal Microkernel Bare-Metal USB Flashing Tool (Win)     '
        Write-Host '================================================================='
        Write-Host ("Target Disk : {0} ({1}, {2:N2} GB, Bus={3}, Style={4})" -f $disk.Number, $disk.FriendlyName, ($disk.Size / 1GB), $disk.BusType, $disk.PartitionStyle)

        $proceed = $true
        if (-not $Yes) {
            Write-Host ''
            Write-Host "WARNING: ALL DATA ON DISK $DiskNumber ($($disk.FriendlyName)) WILL BE PERMANENTLY ERASED!" -ForegroundColor Yellow
            $confirmation = Read-Host "Type 'YES' in capital letters to proceed"
            if ($confirmation -cne 'YES') {
                Write-Host '[-] Aborted by user.'
                $exitCode = 1
                $proceed = $false
            }
        }

        if ($proceed -and -not (Test-IsAdministrator)) {
            $tempLog = Join-Path $env:TEMP ("sharpmetal-usb-flash-{0}.log" -f $PID)
            $exitCode = Invoke-SelfElevated $tempLog
            Remove-Item -Force -ErrorAction SilentlyContinue $tempLog
        } elseif ($proceed) {
            Write-Host "[*] Clearing partitions and initializing GPT on Disk $DiskNumber..."
            if ($disk.PartitionStyle -ne 'RAW') {
                Clear-Disk -Number $DiskNumber -RemoveData -RemoveOEM -Confirm:$false
                Start-Sleep -Milliseconds 300
                $disk = Get-Disk -Number $DiskNumber
            }
            if ($disk.PartitionStyle -eq 'RAW') {
                Initialize-Disk -Number $DiskNumber -PartitionStyle GPT
            } elseif ($disk.PartitionStyle -ne 'GPT') {
                Set-Disk -Number $DiskNumber -PartitionStyle GPT
            }

            Write-Host '[*] Creating 256 MiB FAT32 partition (SHARPMETAL)...'
            $part = New-Partition -DiskNumber $DiskNumber -Size 256MB -AssignDriveLetter
            $driveLetter = $part.DriveLetter
            if (-not $driveLetter) {
                throw 'Windows did not assign a drive letter to the newly created USB partition.'
            }

            Start-Sleep -Milliseconds 300
            Format-Volume -DriveLetter $driveLetter -FileSystem FAT32 -NewFileSystemLabel 'SHARPMETAL' -Confirm:$false | Out-Null
            $rootDrive = "${driveLetter}:"

            Write-Host "[*] Staging SharpMetal UEFI payloads to $rootDrive\..."
            Copy-UsbPayloads -RootDrive $rootDrive -Artifacts $artifacts

            Write-Host '[*] Staged files verified:'
            Get-ChildItem -Path $rootDrive -Recurse -File | ForEach-Object {
                Write-Host ("    {0,-32} {1,10} bytes" -f $_.FullName.Substring(2), $_.Length)
            }

            Write-Host '[*] Flushing filesystem buffers...'
            Write-VolumeCache -DriveLetter $driveLetter
            Start-Sleep -Milliseconds 300

            if (-not $KeepDriveLetter) {
                Write-Host '[*] Marking Partition 1 as UEFI EFI System Partition (ESP)...'
                try {
                    Remove-PartitionAccessPath -DiskNumber $DiskNumber -PartitionNumber $part.PartitionNumber -AccessPath "${driveLetter}:\"
                    Set-Partition -DiskNumber $DiskNumber -PartitionNumber $part.PartitionNumber -GptType $EfiGuid
                } catch {
                    Write-Warning "Could not switch GPT type to ESP ($($_.Exception.Message)); restoring drive letter as FAT32 Basic Data."
                    Add-PartitionAccessPath -DiskNumber $DiskNumber -PartitionNumber $part.PartitionNumber -AssignDriveLetter -ErrorAction SilentlyContinue
                }
            }

            Write-Host ''
            Write-Host '================================================================='
            Write-Host "   USB FLASHING COMPLETED: Disk $DiskNumber is UEFI-bootable!    "
            Write-Host '================================================================='
            Get-Partition -DiskNumber $DiskNumber | Format-Table PartitionNumber, DriveLetter, Type, GptType,
                @{ Label = 'SizeMB'; Expression = { [math]::Round($_.Size / 1MB, 1) } } -AutoSize | Out-Host
            Write-Host 'Bare-Metal Boot Instructions:'
            Write-Host '  1. Leave the USB drive inserted and reboot into UEFI Firmware (shutdown /r /fw /t 0).'
            Write-Host '  2. Disable Secure Boot and ensure UEFI Boot Mode (non-CSM) is enabled.'
            Write-Host '  3. Select the USB drive from the UEFI Boot Menu (F12 / F11 / F8 / Del / Esc).'
            Write-Host '================================================================='
        }
    }
} catch {
    Write-Host "[-] ERROR: $($_.Exception.Message)" -ForegroundColor Red
    $exitCode = 1
} finally {
    if ($transcriptStarted) {
        try { Stop-Transcript | Out-Null } catch {}
    }
}

exit $exitCode
