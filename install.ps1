#Requires -Version 5.1
<#
.SYNOPSIS
    Installs or uninstalls the Taskmanager J2534 Bridge PassThru DLL.

.DESCRIPTION
    Copies TaskmanagerBridge.dll to Program Files (x86) and writes the
    required J2534 registry keys so OBD2 diagnostic tools can discover it.
    Run as Administrator.

.PARAMETER DllSource
    Path to TaskmanagerBridge.dll. Defaults to .\TaskmanagerBridge.dll.

.PARAMETER Uninstall
    Remove the DLL and registry keys instead of installing.

.EXAMPLE
    .\install.ps1
    .\install.ps1 -DllSource "C:\Downloads\TaskmanagerBridge.dll"
    .\install.ps1 -Uninstall
#>

param(
    [string]$DllSource = ".\TaskmanagerBridge.dll",
    [switch]$Uninstall
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$InstallDir  = "C:\Program Files (x86)\TaskmanagerBridge"
$DllDest     = "$InstallDir\TaskmanagerBridge.dll"
$RegPath     = "HKLM:\SOFTWARE\WOW6432Node\PassThruSupport.04.04\Taskmanager J2534 Bridge"

# ── Elevation check ───────────────────────────────────────────────────────────
$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "Re-launching as Administrator..." -ForegroundColor Yellow
    $args = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $MyInvocation.MyCommand.Path)
    if ($Uninstall)   { $args += "-Uninstall" }
    if ($DllSource)   { $args += "-DllSource"; $args += $DllSource }
    Start-Process powershell.exe -ArgumentList $args -Verb RunAs
    exit
}

# ── Uninstall ─────────────────────────────────────────────────────────────────
if ($Uninstall) {
    Write-Host "Uninstalling Taskmanager J2534 Bridge..." -ForegroundColor Cyan

    if (Test-Path $RegPath) {
        Remove-Item -Path $RegPath -Recurse -Force
        Write-Host "  Registry key removed." -ForegroundColor Green
    } else {
        Write-Host "  Registry key not found (already removed?)." -ForegroundColor Yellow
    }

    if (Test-Path $DllDest) {
        Remove-Item -Path $DllDest -Force
        Write-Host "  DLL removed: $DllDest" -ForegroundColor Green
    } else {
        Write-Host "  DLL not found (already removed?)." -ForegroundColor Yellow
    }

    if (Test-Path $InstallDir) {
        $remaining = Get-ChildItem $InstallDir -ErrorAction SilentlyContinue
        if (-not $remaining) {
            Remove-Item -Path $InstallDir -Force
            Write-Host "  Install directory removed." -ForegroundColor Green
        }
    }

    Write-Host "Uninstall complete." -ForegroundColor Green
    exit
}

# ── Install ───────────────────────────────────────────────────────────────────
Write-Host "Installing Taskmanager J2534 Bridge..." -ForegroundColor Cyan

# Resolve DLL source
$DllSource = Resolve-Path $DllSource -ErrorAction SilentlyContinue
if (-not $DllSource -or -not (Test-Path $DllSource)) {
    Write-Host "ERROR: DLL not found at: $DllSource" -ForegroundColor Red
    Write-Host "Usage: .\install.ps1 [-DllSource <path to TaskmanagerBridge.dll>]"
    exit 1
}

# Copy DLL
if (-not (Test-Path $InstallDir)) {
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
}
Copy-Item -Path $DllSource -Destination $DllDest -Force
Write-Host "  DLL installed: $DllDest" -ForegroundColor Green

# Write registry keys
if (-not (Test-Path $RegPath)) {
    New-Item -Path $RegPath -Force | Out-Null
}
Set-ItemProperty -Path $RegPath -Name "Name"               -Value "Taskmanager J2534 Bridge"
Set-ItemProperty -Path $RegPath -Name "Vendor"             -Value "Taskmanager"
Set-ItemProperty -Path $RegPath -Name "FunctionLibrary"    -Value $DllDest
Set-ItemProperty -Path $RegPath -Name "ConfigApplication"  -Value ""
Set-ItemProperty -Path $RegPath -Name "ProtocolsSupported" -Value 0x30   -Type DWord
Set-ItemProperty -Path $RegPath -Name "MessageVersion"     -Value 0x0404 -Type DWord
Write-Host "  Registry keys written." -ForegroundColor Green

# Verify
$check = Get-ItemProperty -Path $RegPath -ErrorAction SilentlyContinue
if ($check -and (Test-Path $DllDest)) {
    Write-Host ""
    Write-Host "Installation successful." -ForegroundColor Green
    Write-Host "  DLL:      $DllDest"
    Write-Host "  Registry: $RegPath"
    Write-Host ""
    Write-Host "The adapter will appear in J2534 tools as: Taskmanager J2534 Bridge"
} else {
    Write-Host "WARNING: Verification failed. Check for errors above." -ForegroundColor Red
    exit 1
}
