# Taskmanager J2534 Bridge

A CAN/ISO15765-focused SAE J2534-1 v04.04 PassThru DLL written in C# (.NET Framework 4.7.2, x86) that bridges a custom STM32F407-based CAN adapter to Windows OBD2 diagnostic software.

Current support focuses on CAN and ISO15765. Additional J2534 protocols are planned for future hardware/firmware updates.

Tested against: **Toyota Techstream**, **VW ODIS 25**, **Ford IDS**, and CAN/ISO15765-based J2534-1 v04.04 diagnostic applications.

Not supported: **VCDS (VAG-COM)**, because Ross-Tech requires its own proprietary HEX/HEX-NET/HEX-V2 hardware and does not provide normal third-party J2534 operation.
---

## Hardware Stack

```
PC (USB) ──► FT232H USB-UART ──► STM32F407VET6 ──► ADuM1201 isolation ──► SN65HVD230 ──► CAN bus
              921600 baud                              galvanic isolation     CAN transceiver
```

| Component | Part | Notes |
|-----------|------|-------|
| MCU | STM32F407VET6 | Custom UART-CAN bridge firmware |
| USB-UART | FT232H | 921600 baud, 8N1 |
| Isolation | ADuM1201 | Galvanic isolation between PC and vehicle |
| CAN transceiver | SN65HVD230 | 3.3 V compatible |
| CAN termination | 120 Ω DIP-switch | Enable when adapter is at bus end |

---

## UART Protocol

### PC → STM32 (Transmit)

```
[0xAA] [0x01] [ID_H] [ID_L] [LEN] [D0] ... [Dn] [XOR]
```

| Byte | Value | Description |
|------|-------|-------------|
| 0 | `0xAA` | Start-of-frame marker |
| 1 | `0x01` | Command: send CAN frame |
| 2 | `ID_H` | CAN ID bits 15–8 |
| 3 | `ID_L` | CAN ID bits 7–0 |
| 4 | `LEN` | Payload length (0–8) |
| 5..4+LEN | `Dn` | CAN payload bytes |
| last | `XOR` | XOR of ID_H, ID_L, and all payload bytes |

### STM32 → PC (Receive)

```
[0xBB] [LEN] [ID_H] [ID_L] [D0] ... [Dn]
```

| Byte | Value | Description |
|------|-------|-------------|
| 0 | `0xBB` | Start-of-frame marker |
| 1 | `LEN` | Payload length (1–8) |
| 2 | `ID_H` | CAN ID bits 15–8 |
| 3 | `ID_L` | CAN ID bits 7–0 |
| 4..3+LEN | `Dn` | CAN payload bytes |

> **Note:** The current firmware uses 11-bit standard CAN IDs. The two ID bytes carry bits 10–0 only. 29-bit extended ID support requires a firmware update.

---

## Building from Source

### Prerequisites

- Visual Studio 2022 with **.NET desktop development** workload
- .NET Framework 4.7.2 targeting pack
- DllExport 1.8.1 (already in `packages\`)

### Build

```powershell
# From the solution root
msbuild TaskmanagerBridge.sln /t:Restore /p:Configuration=Release /p:Platform=x86
msbuild TaskmanagerBridge.sln /p:Configuration=Release /p:Platform=x86 /p:DllExportNoRestore=true
```

Output: `TaskmanagerBridge\bin\x86\Release\TaskmanagerBridge.dll`

The DLL must be built as **x86 (PE32)**. AnyCPU or x64 builds will be rejected by 32-bit diagnostic tools and by the DllExport PE check (`DllExportPeCheck=6`).

---

## Installation & Registry Registration

J2534 tools discover PassThru DLLs through the Windows registry. On a 64-bit OS, 32-bit diagnostic tools read from `HKLM\SOFTWARE\WOW6432Node\PassThruSupport.04.04`.

### Option A — PowerShell (recommended)

Run the following as **Administrator**:

```powershell
$dllPath = "C:\J2534\TaskmanagerBridge.dll"   # <-- set to your actual path
Copy-Item "TaskmanagerBridge\bin\x86\Release\TaskmanagerBridge.dll" $dllPath

$root  = "HKLM:\SOFTWARE\WOW6432Node\PassThruSupport.04.04\Taskmanager J2534 Bridge"
New-Item  -Path $root -Force | Out-Null
Set-ItemProperty -Path $root -Name "Name"               -Value "Taskmanager J2534 Bridge"
Set-ItemProperty -Path $root -Name "Vendor"             -Value "Taskmanager"
Set-ItemProperty -Path $root -Name "ProtocolsSupported" -Value 0x30          -Type DWord
Set-ItemProperty -Path $root -Name "MessageVersion"     -Value 0x0404        -Type DWord
Set-ItemProperty -Path $root -Name "FunctionLibrary"    -Value $dllPath
Set-ItemProperty -Path $root -Name "ConfigApplication"  -Value ""
Write-Host "Registration complete."
```

`ProtocolsSupported = 0x30` sets bits for **CAN (0x10)** and **ISO 15765 (0x20)**.

### Option B — .reg file

Create `register.reg` with your actual DLL path and double-click it:

```reg
Windows Registry Editor Version 5.00

[HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\PassThruSupport.04.04\Taskmanager J2534 Bridge]
"Name"="Taskmanager J2534 Bridge"
"Vendor"="Taskmanager"
"ProtocolsSupported"=dword:00000030
"MessageVersion"=dword:00000404
"FunctionLibrary"="C:\\J2534\\TaskmanagerBridge.dll"
"ConfigApplication"=""
```

### Uninstalling

```powershell
Remove-Item "HKLM:\SOFTWARE\WOW6432Node\PassThruSupport.04.04\Taskmanager J2534 Bridge" -Recurse
```

---

## Application-Specific Notes

### Toyota Techstream

- Requires `PassThruSupport.04.04` registry key (✓ set by `PassThruReadVersion` returning `"04.04"`)
- Calls `READ_VBATT_EXT (0x10001)` on connect; returns 14 200 mV (✓)
- Returns `ERR_BUFFER_EMPTY (0x10)` when no frames are queued — **not** `STATUS_NOERROR` with 0 messages

### VW ODIS 25

- Also calls `READ_VBATT_EXT (0x10001)`; any value below ~8 000 mV will trigger a "battery too low" abort
- Sends `GET_CONFIG` / `SET_CONFIG` frequently; the bridge handles both and returns sensible defaults

### Ford IDS

- Sensitive to null-terminated version strings; `PassThruReadVersion` guarantees a `\0` within 80 bytes

### VCDS (VAG-COM)

- Primarily uses CAN and ISO 15765; both protocol IDs are accepted by `PassThruConnect`

---

## Traffic Log

The DLL writes a human-readable log to:

```
%LOCALAPPDATA%\TaskmanagerBridge\traffic.log
```

Each line format:

```
[HH:mm:ss.fff] DIRECTION        | ID: 0x00000000 | DATA: AA BB CC ...
```

Example:

```
[14:22:01.123] SYS_OPEN         | ID: 0x00000000 | DATA: 43 4F 4D 37
[14:22:01.456] SYS_CONNECT      | ID: 0x0001D4C0 | DATA: 05 00
[14:22:01.789] TX               | ID: 0x000007DF | DATA: 02 01 00 00 00 00 00 00
[14:22:01.812] RX               | ID: 0x000007E8 | DATA: 06 41 00 BE 3F A8 13
```

The log queue is bounded at 10 000 entries. Entries are dropped (not queued) when full so the J2534 caller thread is never delayed by disk I/O.

---

## Project Structure

```
TaskmanagerBridge/
├── PassThruAPI.cs       — 14 DllExport functions (J2534 ABI)
├── SerialBridge.cs      — SerialPort owner, TX builder, RX state machine
├── Sniffa.cs            — Async traffic logger (BlockingCollection + background thread)
├── Bridgeconfig.cs      — Registry persistence (HKCU\Software\TaskmanagerBridge)
├── ConfigDialog.cs      — WinForms COM port / baud rate picker (code-only, no designer)
└── Properties/
    └── AssemblyInfo.cs
```

---

## License

Proprietary - All Rights Reserved
