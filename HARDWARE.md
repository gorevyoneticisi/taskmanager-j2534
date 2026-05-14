# Hardware Design — Taskmanager J2534 Bridge

## Block diagram

```
OBD2 connector (12V)
        │
   [SMCJ24A TVS]        ← transient/overvoltage protection on 12V and GND lines
        │
   [XL7015 Buck]        ← 12V → 5V DC-DC converter
        │ 5V
   [B0503S-1W]          ← galvanic isolation barrier (5V in → 3.3V isolated out, 1W)
        │ 3.3V (isolated)
   ┌────┴────────────┐
[ADuM1201]      [SN65HVD230]   ← both powered from isolated 3.3V rail
UART isolator    CAN transceiver
        │               │
    FT232H          CAN bus
    (USB)          ┌────┴────┐
                [120Ω SW]  [4x routing DIP]
              termination   pin mux
```

## Component list

| Component | Part | Function |
|-----------|------|----------|
| TVS diode | SMCJ24A | Bidirectional TVS, 24V standoff — clamps transients on 12V and GND OBD2 lines before they reach the converter |
| Buck converter | XL7015 | Steps 12V (8–80V input capable) down to 5V for the isolation module |
| Isolated DC-DC | B0503S-1W | 5V→3.3V with galvanic isolation (1W). Provides a floating 3.3V rail that is electrically separate from the USB/PC side |
| Digital isolator | ADuM1201 | Isolates UART TX/RX between FT232H and STM32. Powered from isolated 3.3V so the PC side never touches car-side ground |
| CAN transceiver | SN65HVD230 | 3.3V CAN PHY. Converts STM32 digital CANH/CANL to differential CAN bus signals |
| Termination | 120Ω + DIP switch | Switchable 120Ω termination resistor across CAN_H / CAN_L. Enable when the adapter is at a bus end; disable when the vehicle already has both terminations |
| Pin routing | 4x DIP switch | Selects which OBD2 connector pins connect to CAN_H and CAN_L (see table below) |

## DIP switch — CAN pin routing

Different manufacturers wire CAN to different OBD2 pins:

| Brand | CAN_H pin | CAN_L pin |
|-------|-----------|-----------|
| Ford / Mazda (HS-CAN) | 6 | 14 |
| Toyota / VAG | 6 | 14 |

> Standard ISO 15765-4 always uses pins 6 and 14. The 4-position DIP switch
> allows manually bridging alternative pins for non-standard or multi-bus
> vehicles (e.g. Ford MS-CAN on pins 3/11) without rewiring.

Switch positions (label face-up, left = ON):

```
[SW1] CAN_H ← OBD2 pin 6   (standard, Ford HS / Toyota / VAG)
[SW2] CAN_H ← OBD2 pin 3   (Ford MS-CAN medium-speed bus)
[SW3] CAN_L ← OBD2 pin 14  (standard)
[SW4] CAN_L ← OBD2 pin 11  (Ford MS-CAN medium-speed bus)
```

Only one of SW1/SW2 and one of SW3/SW4 should be ON at a time.

## Isolation architecture

The B0503S creates a hard galvanic break between the vehicle electrical system and the USB/PC side:

```
PC side (non-isolated)         │  Car side (isolated)
                                │
FT232H ── ADuM1201 (TX/RX) ───┤── STM32F407 ── SN65HVD230 ── CAN bus
USB 5V ── XL7015 ── B0503S ───┤── 3.3V rail (isolated)
                                │
                        ISOLATION BARRIER
```

- **ADuM1201** isolates the UART data lines (TX, RX) — rated 2500 Vrms
- **B0503S-1W** isolates the power rail — prevents ground loops and protects the PC from vehicle faults
- **SMCJ24A** clamps any spike (load dump, jump-start transients up to ~40V peak) before it reaches the converter

## STM32 connection

STM32F407VET6 CAN1 peripheral:
- TX: PD1
- RX: PD0
- UART1 (to FT232H via ADuM1201): TX=PA9, RX=PA10, 921600 baud

## Notes

- The XL7015 input range covers typical vehicle voltages including 24V trucks
- SMCJ24A on GND line catches negative transients (ground shift events)
- 120Ω termination should be **OFF** when connected to a real vehicle (ECUs already terminate the bus)
- 120Ω termination should be **ON** when using the bench demo with the ESP32 simulator (both ends need termination)
