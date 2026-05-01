# Taskmanager J2534 Bridge

[![Build J2534 DLL](https://github.com/gorevyoneticisi/taskmanager-j2534/actions/workflows/build.yml/badge.svg)](https://github.com/gorevyoneticisi/taskmanager-j2534/actions/workflows/build.yml)

A **SAE J2534-1 v04.04** compliant PassThru DLL written entirely in C# (.NET Framework 4.7.2, x86) that connects a custom STM32F407-based CAN adapter to any Windows OBD2 diagnostic application — no proprietary drivers, no locked hardware.

**Confirmed working with:**
- Toyota Techstream
- VW ODIS 25
- Ford IDS
- VCDS (VAG-COM)
- Any J2534-1 v04.04 compliant application

---

## Why This Exists

Every commercial J2534 interface ships with a proprietary DLL that locks you to their hardware. This project breaks that dependency: the DLL implements the **complete J2534-1 v04.04 specification in software**, while the STM32 firmware is kept intentionally thin — it only moves raw CAN frames over UART. Swap the hardware, keep the software.

This means any PC-side diagnostic tool that speaks J2534 works out of the box, with full ISO15765-2 transport, multi-channel support, and all 14 mandatory API exports.

---

## Feature Matrix

| Feature | Status |
|---------|--------|
| All 14 J2534 mandatory exports | ✅ |
| Correct J2534 v04.04 constants (IoctlID, ConfigParam, error codes) | ✅ |
| Multi-channel support (CAN + ISO15765 simultaneously) | ✅ |
| ISO15765-2 transport layer — segmentation and reassembly in DLL | ✅ |
| Flow Control (FC) auto-sent on First Frame reception | ✅ |
| FC_WAIT handling in transmit path | ✅ |
| PASS_FILTER / BLOCK_FILTER / FLOW_CONTROL_FILTER per channel | ✅ |
| Periodic messages with real `System.Threading.Timer` | ✅ |
| SET_CONFIG persists ISO15765_BS / STMIN per channel | ✅ |
| RxStatus flags (START_OF_MESSAGE, CAN_29BIT_ID) | ✅ |
| Battery voltage 14 200 mV — Techstream/ODIS accepted | ✅ |
| Async non-blocking traffic log to `%LOCALAPPDATA%` | ✅ |
| 29-bit extended CAN IDs | pending firmware v2 |
| Dynamic baud rate switching | pending firmware v2 |

---

## Architecture

```
┌─────────────────────────────────────────────────────┐
│                 Diagnostic Application               │
│         (Techstream / ODIS / IDS / VCDS / ...)      │
└──────────────────────┬──────────────────────────────┘
                       │  J2534-1 v04.04 API (Win32)
┌──────────────────────▼──────────────────────────────┐
│               TaskmanagerBridge.dll                  │
│                                                      │
│  PassThruAPI.cs ── 14 exported functions             │
│  ├── ChannelManager ── per-channel queues,           │
│  │                     filters, ISO-TP sessions      │
│  ├── FrameRouter ───── background thread,            │
│  │                     PASS/BLOCK filter dispatch    │
│  └── IsoTpEngine ───── SF/FF/CF/FC segmentation,     │
│                        FC_WAIT loop, reassembly      │
│                                                      │
│  SerialBridge.cs ── UART owner, TX builder,          │
│                     RX state machine (BlockingColl.) │
│  Sniffa.cs ──────── async traffic log, bounded queue │
│  BridgeConfig.cs ── registry persistence             │
│  ConfigDialog.cs ── WinForms COM port / speed picker │
└──────────────────────┬──────────────────────────────┘
                       │  921 600 baud 8N1 UART
┌──────────────────────▼──────────────────────────────┐
│           STM32F407VET6 firmware                     │
│           (raw CAN ↔ UART bridge only)               │
└──────────────────────┬──────────────────────────────┘
                       │  CAN bus (ISO 11898-2)
                  Vehicle ECUs
```

The STM32 firmware is deliberately kept simple. Every protocol decision — ISO-TP framing, flow control, filter matching, channel isolation — lives in the DLL. This makes the firmware stable and the software independently testable.

---

## Repository Layout

```
taskmanager-j2534/
├── software/
│   └── TaskmanagerBridge/        C# .NET Framework 4.7.2 Visual Studio solution
│       ├── TaskmanagerBridge/    Project source (.cs files, .csproj)
│       │   ├── PassThruAPI.cs    14 DllExport functions, channel mgr, ISO-TP
│       │   ├── SerialBridge.cs   UART owner, TX builder, RX state machine
│       │   ├── Sniffa.cs         Async traffic logger
│       │   ├── Bridgeconfig.cs   Registry persistence
│       │   └── ConfigDialog.cs   WinForms COM port / CAN speed picker
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
  │
  ▼ 921 600 baud 8N1
FT232H USB-UART adapter
  │
  ▼ UART TTL 3.3 V
STM32F407VET6  ◄── custom UART-CAN bridge firmware
  │
  ▼
ADuM1201  ◄── galvanic isolation (protects the PC from vehicle ground loops)
  │
  ▼
SN65HVD230  ◄── 3.3 V CAN transceiver (ISO 11898-2)
  │
  ▼ OBD2 pins 6 & 14
CAN bus (vehicle)
```

| Component | Part | Detail |
|-----------|------|--------|
| MCU | STM32F407VET6 | UART-CAN bridge firmware (this repo) |
| USB-UART | FT232H | 921 600 baud, no extra driver on Win 10/11 |
| Isolation | ADuM1201 | 2-channel digital isolator, 1 Mbit/s |
| CAN transceiver | SN65HVD230 | 3.3 V, ISO 11898-2 |
| Termination | 120 Ω DIP-switch | Enable when adapter is the bus endpoint |

---

## UART Wire Protocol

### Firmware v1 (current — 11-bit standard CAN IDs)

**PC → STM32** (send a CAN frame):

```
Byte:  0      1      2      3      4      5 ... 4+N   5+N
       0xAA   0x01   ID_H   ID_L   LEN    D0 ... Dn   XOR
```

- `ID_H / ID_L` — CAN ID bits 10–8 / 7–0 (11-bit)
- `LEN` — payload byte count, 0–8
- `XOR` — `ID_H ^ ID_L ^ D0 ^ … ^ Dn`

**STM32 → PC** (received CAN frame):

```
Byte:  0      1      2      3      4 ... 3+N
       0xBB   LEN    ID_H   ID_L   D0 ... Dn
```

No checksum on the receive path — the firmware guarantees framing.

### Firmware v2 (planned — 29-bit extended IDs + baud rate switching)

**PC → STM32** (send CAN frame, 4-byte ID):

```
Byte:  0      1      2      3      4      5      6      7 ... 6+N   7+N
       0xAA   0x01   ID3    ID2    ID1    ID0    LEN    D0 ... Dn   XOR
```

**PC → STM32** (set CAN baud rate):

```
Byte:  0      1      2      3      4      5      6
       0xAA   0x02   B3     B2     B1     B0     XOR
```

`B3..B0` = desired baud rate big-endian (e.g. `0x0007A120` = 500 000 bps).

---

## ISO15765-2 Transport Layer

The DLL handles the full ISO-TP protocol transparently — your application sends and receives complete SDUs; the DLL handles all framing.

**Transmit (PassThruWriteMsgs on an ISO15765 channel):**

| SDU length | Frame type |
|-----------|-----------|
| 1–7 bytes | Single Frame (SF): `[PCI+len][data...]` |
| > 7 bytes | First Frame → wait FC → Consecutive Frames |

FC_WAIT frames from the ECU are handled in a loop: the transmitter holds until `FC_CTS` is received or the N_Bs timeout expires.

**Receive (via FrameRouter background thread):**

1. Single Frame → delivered immediately as a complete message
2. First Frame → `START_OF_MESSAGE` indication delivered, Flow Control sent automatically, Consecutive Frames accumulated
3. Reassembled SDU delivered as one `PASSTHRU_MSG`

Flow Control parameters (`ISO15765_BS`, `ISO15765_STMIN`) are configurable per channel via `PassThruIoctl(SET_CONFIG)`.

---

## Building the DLL

### Requirements

- Visual Studio 2022 with the **.NET desktop development** workload
- .NET Framework 4.7.2 targeting pack (included with VS by default)

### Local build

```powershell
cd software\TaskmanagerBridge

# First time only — NuGet restore
msbuild TaskmanagerBridge.sln /t:Restore /p:Configuration=Release /p:Platform=x86

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

Every push to `main` that touches `software/**` triggers the [Build J2534 DLL](.github/workflows/build.yml) workflow. It builds, validates the output is x86 PE32, and uploads the DLL as a downloadable artifact under the Actions tab.

---

## Installing the DLL

### Step 1 — Copy the DLL to a permanent location

```powershell
New-Item "C:\J2534" -ItemType Directory -Force
Copy-Item "software\TaskmanagerBridge\TaskmanagerBridge\bin\x86\Release\TaskmanagerBridge.dll" "C:\J2534\"
```

### Step 2 — Register in Windows (run as Administrator)

J2534 tools discover PassThru DLLs through a well-known registry key. On 64-bit Windows the key lives under `WOW6432Node` because both the DLL and the diagnostic tools are 32-bit.

```powershell
$dll  = "C:\J2534\TaskmanagerBridge.dll"
$root = "HKLM:\SOFTWARE\WOW6432Node\PassThruSupport.04.04\Taskmanager J2534 Bridge"

New-Item  $root -Force | Out-Null
Set-ItemProperty $root "Name"               "Taskmanager J2534 Bridge"
Set-ItemProperty $root "Vendor"             "Taskmanager"
Set-ItemProperty $root "ProtocolsSupported" 0x30   -Type DWord   # CAN (0x10) + ISO15765 (0x20)
Set-ItemProperty $root "MessageVersion"     0x0404 -Type DWord
Set-ItemProperty $root "FunctionLibrary"    $dll
Set-ItemProperty $root "ConfigApplication"  ""
Write-Host "Registered."
```

### Step 3 — First launch

Open your diagnostic app. The DLL shows a COM port picker on the first run. Select the **FT232H** port (usually the highest COM number), choose CAN speed (500 kbps for most vehicles), and click **Connect**. Check **"Remember these settings"** to skip the dialog on future launches.

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
[14:22:01.456] SYS_CONNECT      | ID: 0x0001D4C0 | DATA: 06 00
[14:22:01.789] TX_ISO_SF        | ID: 0x000007DF | DATA: 02 01 00 CC CC CC CC CC
[14:22:01.812] TX_ISO_FC        | ID: 0x000007E0 | DATA: 30 00 00 CC CC CC CC CC
[14:22:01.834] RX               | ID: 0x000007E8 | DATA: 10 14 49 02 01 31 47 31
[14:22:01.856] RX               | ID: 0x000007E8 | DATA: 21 4A 43 35 34 34 34 34
```

The log queue is bounded at 10 000 entries. Entries are dropped (not queued) when full so the J2534 caller thread is never delayed by disk I/O.

---

## J2534 API Reference

| Function | Behaviour |
|----------|-----------|
| `PassThruOpen` | Shows config dialog on a dedicated STA thread unless "Remember" is set; starts FrameRouter |
| `PassThruClose` | Stops router, disposes all channels and periodic timers, closes serial port |
| `PassThruConnect` | Creates an isolated channel with its own RxQueue and ISO-TP sessions; CAN (5) and ISO15765 (6) |
| `PassThruDisconnect` | Removes channel, disposes all its resources |
| `PassThruWriteMsgs` | CAN: sends raw frames; ISO15765: handles SF/FF/CF/FC segmentation automatically |
| `PassThruReadMsgs` | Dequeues from per-channel queue (already filtered and ISO-TP reassembled) |
| `PassThruStartMsgFilter` | PASS/BLOCK/FLOW_CONTROL filters; FC filter stores reply CAN ID + payload |
| `PassThruStopMsgFilter` | Removes filter by ID; returns `ERR_INVALID_FILTER_ID` if not found |
| `PassThruStartPeriodicMsg` | Real timer per message ID; up to 10 per channel; interval 5–65535 ms |
| `PassThruStopPeriodicMsg` | Disposes timer by message ID |
| `PassThruIoctl` | `READ_VBATT`/`EXT` → 14 200 mV; `GET_CONFIG`/`SET_CONFIG`; `CLEAR_*`; `FIVE_BAUD`/`FAST_INIT` → `ERR_NOT_SUPPORTED` |
| `PassThruReadVersion` | Firmware `STM32F407_v2.0.0`, DLL `TaskmanagerBridge_v2.0.0`, API `04.04` |
| `PassThruGetLastError` | Per-thread error string (ThreadLocal) |
| `PassThruSetProgrammingVoltage` | Logged only — hardware does not support pin voltage |

Battery voltage (`READ_VBATT` / `READ_VBATT_EXT`) returns **14 200 mV**. Techstream and ODIS refuse to connect if this returns 0 or below ~8 000 mV.

---

## Application-Specific Notes

### Toyota Techstream
- Requires `PassThruSupport.04.04` registry key ✅
- Calls `READ_VBATT_EXT (0x10001)` → returns 14 200 mV ✅
- Expects `ERR_BUFFER_EMPTY (0x10)` when no frames queued ✅
- Uses ISO15765 for all UDS service communication ✅

### VW ODIS 25
- Calls `READ_VBATT_EXT`; below ~8 000 mV triggers "battery too low" abort ✅
- Sends `GET_CONFIG` / `SET_CONFIG` frequently; all handled ✅

### Ford IDS
- Sensitive to null-terminated version strings; `PassThruReadVersion` guarantees `\0` within 80 bytes ✅

### VCDS (VAG-COM)
- Uses CAN and ISO15765; both accepted by `PassThruConnect` ✅
- Sets up FLOW_CONTROL_FILTER before sending multi-frame requests ✅

---

## Flashing the Firmware

Open `firmware/stm32-bridge` in **STM32CubeIDE**:

1. File > Import > Existing Projects into Workspace, browse to `firmware/stm32-bridge`
2. Build > Build Project (Ctrl+B)
3. Connect ST-Link, then Run > Debug (F11) to flash

The MCU exposes UART at **921 600 baud 8N1** (no hardware flow control).

---

## License

**Proprietary — All Rights Reserved.**

See [LICENSE](LICENSE) for full terms. In short:

- You may study and build for personal non-commercial use only.
- You may NOT copy, redistribute, modify, or use commercially without written permission.
- You may NOT reverse engineer or create derivative works.

Copyright (c) 2026 Taskmanager Project.
