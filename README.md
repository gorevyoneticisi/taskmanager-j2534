# Taskmanager J2534 Bridge

[![Build J2534 DLL](https://github.com/gorevyoneticisi/taskmanager-j2534/actions/workflows/build.yml/badge.svg)](https://github.com/gorevyoneticisi/taskmanager-j2534/actions/workflows/build.yml)

A **SAE J2534-1 v04.04** compliant PassThru DLL written in C# (.NET Framework 4.7.2, x86) that connects a custom STM32F407-based CAN adapter to any Windows OBD2 diagnostic application. No proprietary drivers, no locked hardware.

**Confirmed working with:**
- Toyota Techstream
- VW ODIS 25
- Ford IDS
- BMW ISTA-D
- Any J2534-1 v04.04 compliant application

---

## Why This Exists

Every commercial J2534 interface ships with a proprietary DLL that locks you to their hardware. This project breaks that dependency: the DLL implements the **complete J2534-1 v04.04 specification in software**, while the STM32 firmware is kept intentionally thin. It only moves raw CAN frames over UART. Swap the hardware, keep the software.

---

## Diagnostic Software Compatibility

### Tools that work with this adapter

The adapter supports CAN and ISO15765 (UDS). Any tool that uses J2534 to communicate over those protocols on modern vehicles (roughly 2008 and newer) is compatible.

| Manufacturer | Tool | J2534 | Works |
|---|---|---|---|
| Toyota / Lexus / Scion | Techstream | Yes | **Yes** - confirmed |
| VW / Audi / Seat / Skoda / Bentley | ODIS | Yes | **Yes** - confirmed |
| BMW / Mini / Rolls Royce | ISTA-D, ISTA-P | Yes | **Yes** |
| Mercedes-Benz / Smart | XENTRY / DAS | Yes | **Yes** (CAN vehicles) |
| Ford / Lincoln | IDS | Yes | **Yes** - confirmed |
| Ford / Lincoln | FDRS | Yes | **Yes** |
| Ford / Lincoln (3rd party) | FORScan | Yes | **Yes** |
| GM (Chevrolet / GMC / Buick / Cadillac) | GDS2 | Yes | **Yes** (standard CAN modules) |
| GM | Tech2Win | Yes | **Yes** |
| Honda / Acura | i-HDS, HDS | Yes | **Yes** |
| Nissan / Infiniti | Consult III Plus | Yes | **Yes** |
| FCA (Chrysler / Dodge / Jeep / Ram / Fiat / Alfa) | wiTECH 2.0 | Yes | **Yes** |
| FCA (3rd party) | MultiECUScan | Yes | **Yes** (CAN vehicles) |
| PSA (Peugeot / Citroen) | DiagBox | Yes | **Yes** |
| Volvo | VIDA | Yes | **Yes** |
| Subaru | SSM4 (Select Monitor 4) | Yes | **Yes** |
| Mazda | MDARS, IDS Legacy | Yes | **Yes** |
| Hyundai / Kia | GDS, KDS | Yes | **Yes** |
| Jaguar / Land Rover | Pathfinder, SDD | Yes | **Yes** |
| Porsche | PIWIS III | Yes | **Yes** |
| VW Group (3rd party) | VCDS (VAG-COM) | No | **No** - Ross-Tech hardware only |
| Renault / Dacia | CAN Clip | Partial | **Partial** - requires special registry setup |

### Why VCDS does not work

VCDS by Ross-Tech requires their own proprietary HEX-NET or HEX-V2 hardware. It does not have a J2534 interface mode. You cannot use a third-party J2534 device with VCDS.

### Why some vehicles in the list may not work

This adapter supports **CAN and ISO15765 only**. It does not support K-line (ISO9141 / ISO14230 KWP2000) or J1850. If your specific vehicle uses K-line diagnostics (typically pre-2008 non-European vehicles), the adapter will not communicate with that vehicle even if the diagnostic software supports J2534.

Examples of what will not work on unsupported vehicles:
- Honda pre-2008 K-line ECUs with i-HDS
- Older Subaru vehicles using the proprietary SSM3 serial protocol
- Older FCA vehicles using K-line with MultiECUScan
- Pre-CAN Jaguar / Land Rover vehicles with SDD

### GM SWCAN modules

GDS2 communicates over SWCAN (Single Wire CAN at 33.3 kbps) for some GM-specific modules such as HVAC and body control. SWCAN requires different hardware and a different J2534 protocol ID (0x08). This adapter does not support SWCAN. GDS2 will work correctly for all standard OBD2 and powertrain diagnostics over regular 500 kbps CAN.

### CAN FD vehicles

The SN65HVD230 transceiver and STM32F407 CAN peripheral do not support CAN FD. For VAG Group vehicles from approximately 2021 onward, the diagnostic gateway uses CAN FD. Hardware v2 will require an MCU with an FDCAN peripheral (STM32G4 series) and a CAN FD capable transceiver.

---

## Feature Matrix

| Feature | Status |
|---------|--------|
| All 14 J2534 mandatory exports | Yes |
| Correct J2534 v04.04 constants (IoctlID, ConfigParam, error codes) | Yes |
| Multi-channel support (CAN + ISO15765 simultaneously) | Yes |
| ISO15765-2 transport layer in DLL (segmentation, reassembly, FC) | Yes |
| FC_WAIT handling in transmit path | Yes |
| Configurable ISO15765_FRAME_PAD_VAL per channel (required for BMW ISTA) | Yes |
| Configurable ISO15765_WFT_MAX per channel | Yes |
| PASS_FILTER / BLOCK_FILTER / FLOW_CONTROL_FILTER per channel | Yes |
| Periodic messages with real System.Threading.Timer | Yes |
| SET_CONFIG persists ISO15765_BS / STMIN / BS_TX / STMIN_TX per channel | Yes |
| RxStatus flags (START_OF_MESSAGE, CAN_29BIT_ID) | Yes |
| Battery voltage 14200 mV for Techstream/ODIS compatibility | Yes |
| Async non-blocking traffic log to %LOCALAPPDATA% | Yes |
| 29-bit extended CAN IDs | Pending firmware v2 |
| Dynamic baud rate switching | Pending firmware v2 |
| UART RX checksum verification | Pending firmware v2 |

---

## Architecture

```
+---------------------------------------------------+
|           Diagnostic Application                  |
|   (Techstream / ODIS / ISTA / XENTRY / IDS / ...) |
+----------------------+----------------------------+
                       | J2534-1 v04.04 API (Win32)
+----------------------+----------------------------+
|           TaskmanagerBridge.dll                   |
|                                                   |
|  PassThruAPI.cs  - 14 exported functions          |
|  ChannelManager  - per-channel queues,            |
|                    filters, ISO-TP sessions        |
|  FrameRouter     - background thread,             |
|                    PASS/BLOCK filter dispatch     |
|  IsoTpEngine     - SF/FF/CF/FC segmentation,      |
|                    FC_WAIT loop, reassembly        |
|                                                   |
|  SerialBridge.cs - UART owner, TX builder,        |
|                    RX state machine               |
|  Sniffa.cs       - async traffic log              |
+----------------------+----------------------------+
                       | 921600 baud 8N1 UART
+----------------------+----------------------------+
|           STM32F407VET6 firmware                  |
|           raw CAN to UART bridge only             |
+----------------------+----------------------------+
                       | CAN bus (ISO 11898-2)
                  Vehicle ECUs
```

The STM32 firmware is deliberately kept simple. Every protocol decision lives in the DLL, making the firmware stable and the DLL independently updatable without reflashing cables.

---

## Repository Layout

```
taskmanager-j2534/
+-- software/
|   +-- TaskmanagerBridge/
|       +-- TaskmanagerBridge/
|       |   +-- PassThruAPI.cs    14 DllExport functions, channel mgr, ISO-TP
|       |   +-- SerialBridge.cs   UART owner, TX builder, RX state machine
|       |   +-- Sniffa.cs         Async traffic logger
|       |   +-- Bridgeconfig.cs   Registry persistence
|       |   +-- ConfigDialog.cs   WinForms COM port / CAN speed picker
|       +-- packages/             DllExport 1.8.1 (committed for offline builds)
|       +-- TaskmanagerBridge.sln
+-- firmware/
|   +-- stm32-bridge/             STM32CubeIDE project for STM32F407VET6
+-- .github/workflows/build.yml   CI: builds the DLL on every push
```

---

## Hardware Stack

```
PC (USB)
  |
  v 921600 baud 8N1
FT232H USB-UART adapter
  |
  v UART TTL 3.3V
STM32F407VET6
  |
  v
ADuM1201 (galvanic isolation)
  |
  v
SN65HVD230 (3.3V CAN transceiver, ISO 11898-2)
  |
  v OBD2 pins 6 and 14
CAN bus (vehicle)
```

| Component | Part | Detail |
|---|---|---|
| MCU | STM32F407VET6 | UART-CAN bridge firmware |
| USB-UART | FT232H | 921600 baud, no extra driver on Win 10/11 |
| Isolation | ADuM1201 | 2-channel digital isolator, 1 Mbit/s |
| CAN transceiver | SN65HVD230 | 3.3V, ISO 11898-2 |
| Termination | 120 ohm DIP-switch | Enable when adapter is the bus endpoint |

---

## UART Wire Protocol

### Firmware v1 (current, 11-bit standard CAN IDs)

**PC to STM32** (send a CAN frame):

```
Byte:  0      1      2      3      4      5 ... 4+N   5+N
       0xAA   0x01   ID_H   ID_L   LEN    D0 ... Dn   XOR
```

- `ID_H / ID_L` = CAN ID bits 10-8 / 7-0 (11-bit)
- `LEN` = payload byte count, 0-8
- `XOR` = `ID_H ^ ID_L ^ D0 ^ ... ^ Dn`

**STM32 to PC** (received CAN frame):

```
Byte:  0      1      2      3      4 ... 3+N
       0xBB   LEN    ID_H   ID_L   D0 ... Dn
```

No checksum on the v1 receive path. See known vulnerabilities below.

### Firmware v2 (planned, 29-bit IDs + baud rate switching + RX checksum)

**PC to STM32** (send CAN frame, 4-byte ID):

```
Byte:  0      1      2      3      4      5      6      7 ... 6+N   7+N
       0xAA   0x01   ID3    ID2    ID1    ID0    LEN    D0 ... Dn   XOR
```

**PC to STM32** (set CAN baud rate):

```
Byte:  0      1      2      3      4      5      6
       0xAA   0x02   B3     B2     B1     B0     XOR
```

**STM32 to PC** (received CAN frame, with checksum):

```
Byte:  0      1      2      3      4      5      6      7 ... 6+N   7+N
       0xBB   LEN    ID3    ID2    ID1    ID0    D0 ... Dn   XOR
```

- `XOR` = `ID3 ^ ID2 ^ ID1 ^ ID0 ^ D0 ^ ... ^ Dn`
- Bit 7 of ID3 set = 29-bit extended CAN frame
- DLL verifies XOR and drops corrupted frames

---

## ISO15765-2 Transport Layer

The DLL handles the full ISO-TP protocol. Your application sends and receives complete SDUs; the DLL handles all framing.

**Transmit:**

| SDU length | Frame type |
|---|---|
| 1-7 bytes | Single Frame |
| >7 bytes | First Frame, wait FC, Consecutive Frames |

FC_WAIT frames from the ECU are looped until FC_CTS is received or timeout expires. The maximum FC_WAIT count is configurable via `SET_CONFIG(ISO15765_WFT_MAX)`.

**Receive (via FrameRouter background thread):**

1. Single Frame delivered immediately as a complete message
2. First Frame delivers `START_OF_MESSAGE` indication, sends Flow Control automatically, accumulates Consecutive Frames
3. Reassembled SDU delivered as one PASSTHRU_MSG

---

## Building the DLL

**Requirements:** Visual Studio 2022 with .NET desktop development workload.

```powershell
cd software\TaskmanagerBridge

# First time only
msbuild TaskmanagerBridge.sln /t:Restore /p:Configuration=Release /p:Platform=x86

# Build
msbuild TaskmanagerBridge.sln `
  /p:Configuration=Release `
  /p:Platform=x86 `
  /p:DllExportNoRestore=true `
  /p:TreatWarningsAsErrors=true
```

Output: `TaskmanagerBridge\bin\x86\Release\TaskmanagerBridge.dll`

The DLL must be x86 (PE32). AnyCPU and x64 builds will not work with 32-bit diagnostic tools.

CI builds on every push to `main` that touches `software/**`. Download the latest artifact from the Actions tab.

---

## Installing the DLL

### Step 1 - Copy the DLL

```powershell
New-Item "C:\J2534" -ItemType Directory -Force
Copy-Item "software\TaskmanagerBridge\TaskmanagerBridge\bin\x86\Release\TaskmanagerBridge.dll" "C:\J2534\"
```

### Step 2 - Register in Windows (run as Administrator)

```powershell
$dll  = "C:\J2534\TaskmanagerBridge.dll"
$root = "HKLM:\SOFTWARE\WOW6432Node\PassThruSupport.04.04\Taskmanager J2534 Bridge"

New-Item  $root -Force | Out-Null
Set-ItemProperty $root "Name"               "Taskmanager J2534 Bridge"
Set-ItemProperty $root "Vendor"             "Taskmanager"
Set-ItemProperty $root "ProtocolsSupported" 0x30   -Type DWord
Set-ItemProperty $root "MessageVersion"     0x0404 -Type DWord
Set-ItemProperty $root "FunctionLibrary"    $dll
Set-ItemProperty $root "ConfigApplication"  ""
Write-Host "Registered."
```

### Step 3 - First launch

Open your diagnostic app. The DLL shows a COM port picker on the first run. Select the FT232H port (usually the highest COM number), choose CAN speed (500 kbps for most vehicles), and click Connect. Check "Remember these settings" to skip the dialog on future launches.

### Uninstalling

```powershell
Remove-Item "HKLM:\SOFTWARE\WOW6432Node\PassThruSupport.04.04\Taskmanager J2534 Bridge" -Recurse
```

---

## Application-Specific Notes

### Toyota Techstream
- Calls `READ_VBATT_EXT (0x10001)` - returns 14200 mV
- Expects `ERR_BUFFER_EMPTY (0x10)` when no frames are queued

### VW / Audi ODIS
- Calls `READ_VBATT_EXT` frequently; below 8000 mV triggers abort
- Sends `GET_CONFIG` / `SET_CONFIG` frequently

### BMW ISTA-D / ISTA-P
- Calls `SET_CONFIG(ISO15765_FRAME_PAD_VAL)` to configure the padding byte
- The DLL now supports this parameter per channel (defaults to 0xCC; ISTA typically sets 0x00)
- Works with ISTA on CAN vehicles; vehicles requiring DoIP are not supported

### Mercedes-Benz XENTRY / DAS
- Standard J2534 v04.04 over CAN/ISO15765
- Requires valid battery voltage from `READ_VBATT`

### Ford IDS / FDRS
- IDS is sensitive to null-terminated version strings; `PassThruReadVersion` guarantees null termination within 80 bytes
- FDRS uses J2534 for CAN-based vehicles

### GM GDS2 / Tech2Win
- Standard CAN and ISO15765 diagnostics work
- SWCAN modules (body control, HVAC) are not supported by this hardware

### Honda i-HDS / HDS
- Standard J2534 over CAN/ISO15765 for vehicles from approximately 2008 onward

### Nissan Consult III Plus
- Standard J2534 over CAN/ISO15765

### FCA wiTECH 2.0 / MultiECUScan
- Modern CAN vehicles work
- K-line FCA vehicles (pre-2007) are not supported

### PSA DiagBox / Volvo VIDA / Mazda MDARS
- Standard J2534 CAN/ISO15765

### Subaru SSM4
- SSM4 supports J2534; works for CAN-based Subaru vehicles
- SSM3 uses a proprietary Subaru serial protocol and does not use J2534

### Hyundai / Kia GDS / KDS
- Standard J2534 over CAN/ISO15765

### Jaguar / Land Rover Pathfinder / SDD
- Modern CAN vehicles work
- Pre-CAN vehicles require K-line which is not supported

### Porsche PIWIS III
- Supports J2534 as a VCI interface option

---

## Known Vulnerabilities and Limitations

### RX path has no checksum (firmware v1)

The firmware v1 receive protocol `[0xBB][LEN][ID_H][ID_L][D0..Dn]` has no checksum. The STM32 guarantees CAN framing but not the UART line between the STM32 and the FT232H. A single dropped byte due to FTDI buffer overrun or electromagnetic interference will cause the DLL to parse misaligned bytes as valid CAN IDs.

**Fix:** Firmware v2 adds an XOR checksum byte at the end of every receive packet. The DLL verifies and drops corrupted frames. Until firmware v2 is deployed, avoid using this adapter for ECU flashing in high-EMI environments.

### USB polling latency in the ISO-TP path

Because the DLL handles ISO-TP segmentation on the PC side, the turnaround path for each flow control exchange is:

```
PC calculates CF -> USB to FT232H -> UART to STM32 -> CAN to ECU
ECU replies -> CAN to STM32 -> UART to FT232H -> USB polling tick (1ms) -> PC
```

The FT232H USB polling interval introduces 1-2 ms of latency per round trip. For standard UDS diagnostics this is invisible. For flashing large firmware images, this latency extends flash times compared to OEM tools. This cannot be eliminated without hardware v2.

### UART baud rate is not the bottleneck

921600 baud carries roughly 92000 bytes per second. A fully saturated 500 kbps CAN bus at 12 bytes per frame produces approximately 48000 bytes per second. The UART link has headroom. Raising the baud rate does not improve diagnostic speed.

### CAN FD not supported

The SN65HVD230 and STM32F407 CAN peripheral do not support CAN FD. VAG Group vehicles from approximately 2021 onward use CAN FD at the diagnostic gateway. Hardware v2 requires an STM32G4 (FDCAN peripheral) and a CAN FD transceiver.

---

## Hardware v2 Upgrade Path

The single highest-impact improvement for hardware v2 is removing the FT232H entirely.

The STM32F407 has a native Full-Speed USB 2.0 peripheral (12 Mbit/s) built into the silicon. By programming the STM32 as a WinUSB Bulk endpoint or USB CDC device, the PC talks directly to the MCU over USB. The UART baud rate limit disappears, the FT232H chip is removed from the BOM, and latency drops to the minimum the Windows USB stack allows. That is the architecture that matches the throughput of OEM dealership tools.

---

## Traffic Log

```
%LOCALAPPDATA%\TaskmanagerBridge\traffic.log
```

Example:

```
[14:22:01.123] SYS_OPEN         | ID: 0x00000000 | DATA: 43 4F 4D 37
[14:22:01.456] SYS_CONNECT      | ID: 0x0001D4C0 | DATA: 06 00
[14:22:01.789] TX_ISO_SF        | ID: 0x000007DF | DATA: 02 01 00 CC CC CC CC CC
[14:22:01.812] TX_ISO_FC        | ID: 0x000007E0 | DATA: 30 00 00 CC CC CC CC CC
[14:22:01.834] RX               | ID: 0x000007E8 | DATA: 10 14 49 02 01 31 47 31
```

The log queue is bounded at 10000 entries. Entries are dropped (never queued) when full so the J2534 caller thread is never delayed by disk I/O.

---

## J2534 API Reference

| Function | Behaviour |
|---|---|
| `PassThruOpen` | Shows config dialog on STA thread unless "Remember" is set; starts FrameRouter |
| `PassThruClose` | Stops router, disposes all channels and timers, closes serial port |
| `PassThruConnect` | Creates channel with RxQueue and ISO-TP sessions; CAN (5) and ISO15765 (6) |
| `PassThruDisconnect` | Removes channel, disposes all resources |
| `PassThruWriteMsgs` | CAN: raw frames; ISO15765: SF/FF/CF/FC segmentation handled automatically |
| `PassThruReadMsgs` | Dequeues from per-channel queue (filtered, ISO-TP reassembled) |
| `PassThruStartMsgFilter` | PASS/BLOCK/FLOW_CONTROL filters; FC filter stores reply CAN ID + payload |
| `PassThruStopMsgFilter` | Removes filter by ID |
| `PassThruStartPeriodicMsg` | Real timer per message; up to 10 per channel; interval 5-65535 ms |
| `PassThruStopPeriodicMsg` | Disposes timer by message ID |
| `PassThruIoctl` | READ_VBATT/EXT = 14200 mV; GET_CONFIG/SET_CONFIG; CLEAR_*; FIVE_BAUD/FAST_INIT = ERR_NOT_SUPPORTED |
| `PassThruReadVersion` | Firmware STM32F407_v2.0.0, DLL TaskmanagerBridge_v2.0.0, API 04.04 |
| `PassThruGetLastError` | Per-thread error string |
| `PassThruSetProgrammingVoltage` | Logged only |

---

## Flashing the Firmware

Open `firmware/stm32-bridge` in STM32CubeIDE:

1. File > Import > Existing Projects into Workspace
2. Build > Build Project (Ctrl+B)
3. Connect ST-Link, then Run > Debug (F11) to flash

The MCU exposes UART at **921600 baud 8N1**, no hardware flow control.

---

## License

**Proprietary - All Rights Reserved.**

See [LICENSE](LICENSE) for full terms:

- You may study and build for personal non-commercial use only.
- You may not copy, redistribute, modify, or use commercially without written permission.
- You may not reverse engineer or create derivative works.

Copyright (c) 2026 Taskmanager Project.
