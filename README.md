# Taskmanager J2534 Bridge

[![Build J2534 DLL](https://github.com/gorevyoneticisi/taskmanager-j2534/actions/workflows/build.yml/badge.svg)](https://github.com/gorevyoneticisi/taskmanager-j2534/actions/workflows/build.yml)

A **SAE J2534-1 v04.04** compliant PassThru DLL written in C# (.NET Framework 4.7.2, x86) that bridges a custom STM32F407-based CAN adapter to Windows OBD2 diagnostic software — with no proprietary drivers required.

**Confirmed working with:**
- Toyota Techstream
- VW ODIS 25
- Ford IDS
- VCDS (VAG-COM)
- Any J2534-1 v04.04 compliant application

---

## Repository Layout

```
taskmanager-j2534/
├── software/
│   └── TaskmanagerBridge/        C# .NET Framework 4.7.2 Visual Studio solution
│       ├── TaskmanagerBridge/    Project source (.cs files, .csproj)
│       ├── packages/             DllExport 1.8.1 (committed for offline builds)
│       └── TaskmanagerBridge.sln
├── firmware/
│   └── stm32-bridge/             STM32CubeIDE project for STM32F407VET6
│       ├── Core/Src/             Application source (main.c, ...)
│       ├── Core/Inc/             Application headers
│       ├── Drivers/              STM32 HAL + BSP
│       └── main.ioc              STM32CubeMX configuration
└── .github/workflows/build.yml   CI: builds the DLL on every push
```

---

## Hardware Stack

```
PC (USB)
  |
  v 921600 baud 8N1
FT232H USB-UART adapter
  |
  v UART TTL 3.3 V
STM32F407VET6  <-- custom UART-CAN bridge firmware
  |
  v
ADuM1201  <-- galvanic isolation (protects the PC from vehicle ground loops)
  |
  v
SN65HVD230  <-- 3.3 V CAN transceiver
  |
  v OBD2 pins 6 & 14
CAN bus (vehicle)
```

| Component | Part | Detail |
|-----------|------|--------|
| MCU | STM32F407VET6 | UART-CAN bridge firmware (this repo) |
| USB-UART | FT232H | 921 600 baud, no extra driver on Win 10/11 |
| Isolation | ADuM1201 | 2-channel digital isolator, 1 Mbit/s |
| CAN transceiver | SN65HVD230 | 3.3 V, ISO 11898-2 |
| Termination | 120 ohm DIP-switch | Enable when adapter is the bus endpoint |

---

## UART Wire Protocol

### PC -> STM32 (send a CAN frame)

```
Byte:  0      1      2      3      4      5 ... 4+N   5+N
       0xAA   0x01   ID_H   ID_L   LEN    D0 ... Dn   XOR
```

- `ID_H / ID_L` — CAN ID bits 15-8 / 7-0 (11-bit standard IDs)
- `LEN` — payload byte count, 0-8
- `XOR` — `ID_H ^ ID_L ^ D0 ^ ... ^ Dn`

### STM32 -> PC (received CAN frame)

```
Byte:  0      1      2      3      4 ... 3+N
       0xBB   LEN    ID_H   ID_L   D0 ... Dn
```

No checksum on the receive path — the firmware guarantees framing.

---

## Building the DLL

### Requirements

- Visual Studio 2022 with the **.NET desktop development** workload
- .NET Framework 4.7.2 targeting pack (included with VS by default)

### Local build

```powershell
cd software\TaskmanagerBridge

# First time only - NuGet restore
nuget restore TaskmanagerBridge.sln

# Build Release x86 (the only supported configuration)
msbuild TaskmanagerBridge.sln `
  /p:Configuration=Release `
  /p:Platform=x86 `
  /p:DllExportNoRestore=true `
  /p:TreatWarningsAsErrors=true
```

Output: `TaskmanagerBridge\bin\x86\Release\TaskmanagerBridge.dll`

> **Important:** The DLL **must** be x86 (PE32). AnyCPU and x64 builds will not work with 32-bit diagnostic tools.

### CI

Every push to `main` that touches `software/**` triggers the [Build J2534 DLL](.github/workflows/build.yml) workflow.
It builds, validates the output is x86 PE32, and uploads the DLL as a downloadable artifact under the Actions tab.

---

## Installing the DLL

### Step 1 — Copy the DLL to a permanent location

```powershell
New-Item "C:\J2534" -ItemType Directory -Force
Copy-Item "software\TaskmanagerBridge\TaskmanagerBridge\bin\x86\Release\TaskmanagerBridge.dll" "C:\J2534\"
```

### Step 2 — Register in Windows (run as Administrator)

J2534 tools discover PassThru DLLs through a well-known registry key.
On 64-bit Windows the key lives under `WOW6432Node` because both the DLL and the diagnostic tools are 32-bit.

```powershell
$dll  = "C:\J2534\TaskmanagerBridge.dll"
$root = "HKLM:\SOFTWARE\WOW6432Node\PassThruSupport.04.04\Taskmanager J2534 Bridge"

New-Item  $root -Force | Out-Null
Set-ItemProperty $root "Name"               "Taskmanager J2534 Bridge"
Set-ItemProperty $root "Vendor"             "Taskmanager"
Set-ItemProperty $root "ProtocolsSupported" 0x30   -Type DWord   # CAN + ISO15765
Set-ItemProperty $root "MessageVersion"     0x0404 -Type DWord
Set-ItemProperty $root "FunctionLibrary"    $dll
Set-ItemProperty $root "ConfigApplication"  ""
Write-Host "Registered."
```

### Step 3 — First launch

Open your diagnostic app. The DLL shows a COM port picker on the first run.
Select the **FT232H** port (usually the highest COM number), choose CAN speed (500 kbps for most vehicles), and click **Connect**.
Check **"Remember these settings"** to skip the dialog on future launches.

### Uninstalling

```powershell
Remove-Item "HKLM:\SOFTWARE\WOW6432Node\PassThruSupport.04.04\Taskmanager J2534 Bridge" -Recurse
```

---

## Traffic Log

The DLL writes an async, non-blocking log to:

```
%LOCALAPPDATA%\TaskmanagerBridge\traffic.log
```

Example entries:

```
[14:22:01.123] SYS_OPEN         | ID: 0x00000000 | DATA: 43 4F 4D 37
[14:22:01.456] SYS_CONNECT      | ID: 0x0001D4C0 | DATA: 05 00
[14:22:01.789] TX               | ID: 0x000007DF | DATA: 02 01 00 00 00 00 00 00
[14:22:01.812] RX               | ID: 0x000007E8 | DATA: 06 41 00 BE 3F A8 13
```

The log queue is bounded at 10 000 entries. Entries are dropped (not queued) when full
so the J2534 caller thread is never delayed by disk I/O.

---

## J2534 API Notes

| Function | Behaviour |
|----------|-----------|
| `PassThruOpen` | Shows config dialog on a dedicated STA thread unless "Remember" is set |
| `PassThruConnect` | Accepts CAN (5) and ISO 15765 (6); actual CAN speed is set in firmware |
| `PassThruReadMsgs` | Returns `ERR_BUFFER_EMPTY (0x10)` when queue is empty — NOT `STATUS_NOERROR` |
| `PassThruWriteMsgs` | Iterates the full message array via pointer arithmetic |
| `PassThruIoctl` | Handles `READ_VBATT (0x03)`, `READ_VBATT_EXT (0x10001)`, `GET_CONFIG`, `SET_CONFIG` |
| `PassThruReadVersion` | Returns API version `"04.04"` — required by Techstream and ODIS |

Battery voltage (`READ_VBATT` / `READ_VBATT_EXT`) returns **14 200 mV**.
Techstream and ODIS will refuse to connect if this returns 0 or below ~8 000 mV.

---

## Flashing the Firmware

Open `firmware/stm32-bridge` in **STM32CubeIDE**:

1. File > Import > Existing Projects into Workspace, browse to `firmware/stm32-bridge`
2. Build > Build Project (Ctrl+B)
3. Connect ST-Link, then Run > Debug (F11) to flash

The MCU exposes UART at **921 600 baud 8N1** (no hardware flow control).

---

## License

MIT — add a `LICENSE` file before making the repository public.
