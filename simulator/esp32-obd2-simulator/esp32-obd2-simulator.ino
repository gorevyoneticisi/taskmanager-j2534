/*
 * ESP32 OBD2 ECU Simulator
 * ========================
 * Simulates a single ECU responding to OBD2 requests over CAN (ISO 15765-4)
 * for bench-testing the Taskmanager J2534 Bridge without a real vehicle.
 *
 * Supported services:
 *   Mode 01 — Live PIDs (engine load, coolant, RPM, speed, MAF, throttle, etc.)
 *   Mode 03 — Read DTCs  (P0300 random misfire, P0171 system too lean)
 *   Mode 04 — Clear DTCs (codes return after 30 s to simulate a persistent fault)
 *   Mode 09 — VIN        (multi-frame ISO-TP, 17-char string)
 *
 * Hardware:
 *   ESP32 DevKit + SN65HVD230 (or MCP2551) CAN transceiver
 *   CAN TX  → GPIO5
 *   CAN RX  → GPIO4
 *   BOOT button (GPIO0) → hold to simulate engine acceleration
 *
 * Wiring to J2534 bridge:
 *   ESP32 CANH  ──┬── STM32 CANH
 *   ESP32 CANL  ──┴── STM32 CANL
 *   120 Ω terminator at each end of the bus
 *
 * Board: ESP32 Dev Module, 240 MHz, Arduino framework
 */

#include "driver/twai.h"
#include <math.h>

// ── Pin configuration ────────────────────────────────────────────────────────
#define CAN_TX_PIN   GPIO_NUM_5
#define CAN_RX_PIN   GPIO_NUM_4
#define BOOT_BTN_PIN 0   // built-in BOOT button (active LOW) for acceleration demo

// ── OBD2 CAN IDs ─────────────────────────────────────────────────────────────
#define OBD2_FUNC_ID   0x7DF   // functional broadcast — tester → all ECUs
#define ECU_RESP_ID    0x7E8   // this ECU's response address
#define ECU_PHYS_ID    0x7E0   // this ECU's physical request address (DLL sends FC here)

// ── Simulated VIN  (17 chars, change freely) ─────────────────────────────────
static const char VIN[] = "1HGCM82633A004352";

// ── DTC re-arm delay: codes come back this many ms after being cleared ────────
#define DTC_RETURN_MS  30000UL

// ─────────────────────────────────────────────────────────────────────────────
// Simulation state
// ─────────────────────────────────────────────────────────────────────────────
static float s_rpm        = 820.0f;
static float s_speed      = 0.0f;
static float s_coolant    = 20.0f;   // cold-start; warms up over ~2 min
static float s_load       = 18.0f;
static float s_throttle   = 4.5f;
static float s_maf        = 3.6f;
static float s_timing     = 8.0f;
static float s_intake_t   = 24.0f;

static bool     dtcs_cleared  = false;
static uint32_t cleared_at    = 0;

// ISO-TP state — used while waiting for Flow Control after sending VIN FF
static bool     isotp_wait_fc   = false;
static uint32_t isotp_fc_expiry = 0;

// ─────────────────────────────────────────────────────────────────────────────
// CAN helpers
// ─────────────────────────────────────────────────────────────────────────────
static void can_tx(uint32_t id,
                   uint8_t d0, uint8_t d1, uint8_t d2, uint8_t d3,
                   uint8_t d4, uint8_t d5, uint8_t d6, uint8_t d7)
{
    twai_message_t m = {};
    m.identifier      = id;
    m.data_length_code = 8;
    m.data[0] = d0; m.data[1] = d1; m.data[2] = d2; m.data[3] = d3;
    m.data[4] = d4; m.data[5] = d5; m.data[6] = d6; m.data[7] = d7;
    if (twai_transmit(&m, pdMS_TO_TICKS(10)) != ESP_OK)
        Serial.println("TX dropped");
}

// Single-frame OBD2 response — pci = payload length (1-7)
static void sf(uint8_t pci,
               uint8_t b1, uint8_t b2, uint8_t b3,
               uint8_t b4, uint8_t b5, uint8_t b6)
{
    can_tx(ECU_RESP_ID, pci, b1, b2, b3, b4, b5, b6, 0x00);
}

// ─────────────────────────────────────────────────────────────────────────────
// Mode 01  —  live PIDs
// ─────────────────────────────────────────────────────────────────────────────
static void handle_pid(uint8_t pid)
{
    uint8_t A = 0, B = 0;

    switch (pid)
    {
        // Supported PIDs 0x01-0x20
        // A=0x18 → 0x04,0x05 | B=0x3F → 0x0B-0x10 | C=0x80 → 0x11
        case 0x00:
            sf(0x06, 0x41, 0x00, 0x18, 0x3F, 0x80, 0x00);
            return;

        case 0x04:   // Engine load  A/2.55 %
            sf(0x03, 0x41, 0x04, (uint8_t)(s_load * 2.55f), 0, 0, 0);
            return;

        case 0x05:   // Coolant temp  A-40 °C
            sf(0x03, 0x41, 0x05, (uint8_t)(s_coolant + 40.0f), 0, 0, 0);
            return;

        case 0x0B:   // Intake MAP  A kPa  (~98 kPa = atmospheric)
            sf(0x03, 0x41, 0x0B, 98, 0, 0, 0);
            return;

        case 0x0C: { // RPM  (256A+B)/4
            uint16_t r = (uint16_t)(s_rpm * 4.0f);
            sf(0x04, 0x41, 0x0C, r >> 8, r & 0xFF, 0, 0);
            return;
        }
        case 0x0D:   // Speed  A km/h
            sf(0x03, 0x41, 0x0D, (uint8_t)s_speed, 0, 0, 0);
            return;

        case 0x0E:   // Timing advance  A/2-64 °
            sf(0x03, 0x41, 0x0E, (uint8_t)((s_timing + 64.0f) * 2.0f), 0, 0, 0);
            return;

        case 0x0F:   // Intake air temp  A-40 °C
            sf(0x03, 0x41, 0x0F, (uint8_t)(s_intake_t + 40.0f), 0, 0, 0);
            return;

        case 0x10: { // MAF  (256A+B)/100 g/s
            uint16_t m = (uint16_t)(s_maf * 100.0f);
            sf(0x04, 0x41, 0x10, m >> 8, m & 0xFF, 0, 0);
            return;
        }
        case 0x11:   // Throttle position  A/2.55 %
            sf(0x03, 0x41, 0x11, (uint8_t)(s_throttle * 2.55f), 0, 0, 0);
            return;

        default:
            // Unsupported PID — no response; tester will time out gracefully
            return;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Mode 03  —  read stored DTCs
// ─────────────────────────────────────────────────────────────────────────────
static void handle_read_dtc()
{
    bool active = !dtcs_cleared ||
                  ((millis() - cleared_at) >= DTC_RETURN_MS);

    if (!active)
    {
        sf(0x02, 0x43, 0x00, 0, 0, 0, 0);   // no DTCs
        Serial.println("Mode 03: no active DTCs");
        return;
    }

    // P0300 = 0x03 0x00 (random misfire)
    // P0171 = 0x01 0x71 (system too lean bank 1)
    sf(0x06, 0x43, 0x02, 0x03, 0x00, 0x01, 0x71);
    Serial.println("Mode 03: P0300, P0171");
}

// ─────────────────────────────────────────────────────────────────────────────
// Mode 04  —  clear DTCs
// ─────────────────────────────────────────────────────────────────────────────
static void handle_clear_dtc()
{
    dtcs_cleared = true;
    cleared_at   = millis();
    sf(0x01, 0x44, 0, 0, 0, 0, 0);
    Serial.printf("Mode 04: DTCs cleared. Will return in %lu s\n",
                  DTC_RETURN_MS / 1000);
}

// ─────────────────────────────────────────────────────────────────────────────
// Mode 09 PID 0x02  —  VIN  (ISO-TP multi-frame)
//
// Total payload = [0x49][0x02][0x01] + 17 VIN bytes = 20 bytes
//   FF:  [0x10][0x14][0x49][0x02][0x01][V0][V1][V2]
//   FC from DLL arrives on 0x7E0
//   CF1: [0x21][V3..V9]
//   CF2: [0x22][V10..V16]
// ─────────────────────────────────────────────────────────────────────────────
static void handle_vin_ff()
{
    const uint8_t *v = (const uint8_t *)VIN;
    can_tx(ECU_RESP_ID, 0x10, 0x14, 0x49, 0x02, 0x01, v[0], v[1], v[2]);
    isotp_wait_fc   = true;
    isotp_fc_expiry = millis() + 1000;
    Serial.printf("VIN FF sent. Waiting for FC...\n");
}

static void send_vin_cfs()
{
    const uint8_t *v = (const uint8_t *)VIN;
    can_tx(ECU_RESP_ID, 0x21, v[3], v[4], v[5], v[6], v[7], v[8], v[9]);
    delayMicroseconds(500);
    can_tx(ECU_RESP_ID, 0x22, v[10], v[11], v[12], v[13], v[14], v[15], v[16]);
    isotp_wait_fc = false;
    Serial.printf("VIN CFs sent. VIN=%s\n", VIN);
}

// ─────────────────────────────────────────────────────────────────────────────
// Request dispatcher
// ─────────────────────────────────────────────────────────────────────────────
static void dispatch(const uint8_t *d)
{
    // d[0] = ISO-TP PCI — only handle single-frame (upper nibble = 0)
    if ((d[0] & 0xF0) != 0x00) return;

    uint8_t svc = d[1];
    uint8_t pid = d[2];

    Serial.printf("  SVC=%02X PID=%02X\n", svc, pid);

    switch (svc)
    {
        case 0x01: handle_pid(pid);   break;
        case 0x03: handle_read_dtc(); break;
        case 0x04: handle_clear_dtc(); break;
        case 0x09: if (pid == 0x02) handle_vin_ff(); break;
        default:
            // Negative response: serviceNotSupported
            sf(0x03, 0x7F, svc, 0x11, 0, 0, 0);
            break;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Simulation update  (called at ~10 Hz)
// ─────────────────────────────────────────────────────────────────────────────
static void update_sim()
{
    static uint32_t last = 0;
    uint32_t now = millis();
    if (now - last < 100) return;
    last = now;

    float t = now / 1000.0f;

    // BOOT button held → acceleration mode
    bool accel = (digitalRead(BOOT_BTN_PIN) == LOW);

    if (accel)
    {
        s_rpm      += 80.0f;
        if (s_rpm > 4500.0f) s_rpm = 4500.0f;
        s_speed    += 2.0f;
        if (s_speed > 120.0f) s_speed = 120.0f;
        s_load      = 65.0f + sinf(t * 3.0f) * 8.0f;
        s_throttle  = 55.0f + sinf(t * 2.5f) * 10.0f;
        s_maf       = 18.0f + sinf(t * 4.0f) * 3.0f;
        s_timing    = 22.0f + sinf(t * 2.0f) * 4.0f;
    }
    else
    {
        // Coast back to idle
        if (s_rpm > 820.0f)   s_rpm   -= 50.0f;
        else                  s_rpm    = 800.0f + sinf(t * 0.7f) * 40.0f;
        if (s_speed > 0.0f)   s_speed -= 1.5f;
        else                  s_speed  = 0.0f;
        s_load      = 18.0f + sinf(t * 0.5f) * 3.0f;
        s_throttle  = 4.5f  + sinf(t * 0.4f) * 1.5f;
        s_maf       = 3.6f  + sinf(t * 0.6f) * 0.5f;
        s_timing    = 8.0f  + sinf(t * 0.3f) * 2.0f;
    }

    // Coolant warms up from 20 °C to 90 °C over ~100 s
    if (s_coolant < 90.0f) s_coolant += 0.07f;

    s_intake_t = 24.0f + sinf(t * 0.2f) * 2.0f;
}

// ─────────────────────────────────────────────────────────────────────────────
void setup()
{
    Serial.begin(115200);
    delay(500);
    Serial.println("\n╔══════════════════════════════════════╗");
    Serial.println("║   ESP32 OBD2 ECU Simulator  v1.0    ║");
    Serial.println("╚══════════════════════════════════════╝");

    pinMode(BOOT_BTN_PIN, INPUT_PULLUP);

    twai_general_config_t g = TWAI_GENERAL_CONFIG_DEFAULT(CAN_TX_PIN, CAN_RX_PIN, TWAI_MODE_NORMAL);
    g.tx_queue_len = 16;
    g.rx_queue_len = 32;
    twai_timing_config_t  t = TWAI_TIMING_CONFIG_500KBITS();
    twai_filter_config_t  f = TWAI_FILTER_CONFIG_ACCEPT_ALL();

    ESP_ERROR_CHECK(twai_driver_install(&g, &t, &f));
    ESP_ERROR_CHECK(twai_start());

    Serial.printf("CAN: 500 kbps  TX=GPIO%d  RX=GPIO%d\n",
                  (int)CAN_TX_PIN, (int)CAN_RX_PIN);
    Serial.printf("VIN: %s\n", VIN);
    Serial.println("Active DTCs: P0300 (random misfire), P0171 (system too lean)");
    Serial.println("Hold BOOT button to simulate acceleration\n");
    Serial.println("Waiting for OBD2 requests...\n");
}

// ─────────────────────────────────────────────────────────────────────────────
void loop()
{
    update_sim();

    // ── Waiting for ISO-TP Flow Control (VIN multi-frame in progress) ─────────
    if (isotp_wait_fc)
    {
        if (millis() > isotp_fc_expiry)
        {
            Serial.println("VIN: FC timeout — aborting");
            isotp_wait_fc = false;
            return;
        }
        twai_message_t m;
        if (twai_receive(&m, 0) == ESP_OK)
        {
            if (m.identifier == ECU_PHYS_ID && (m.data[0] & 0xF0) == 0x30)
            {
                // Flow Control received — send consecutive frames
                send_vin_cfs();
            }
            else if (m.identifier == OBD2_FUNC_ID || m.identifier == ECU_PHYS_ID)
            {
                dispatch(m.data);
            }
        }
        return;
    }

    // ── Normal request handling ───────────────────────────────────────────────
    twai_message_t m;
    if (twai_receive(&m, pdMS_TO_TICKS(5)) != ESP_OK) return;

    if (m.identifier == OBD2_FUNC_ID || m.identifier == ECU_PHYS_ID)
    {
        Serial.printf("[%03X] %02X %02X %02X %02X %02X %02X %02X %02X\n",
                      m.identifier,
                      m.data[0], m.data[1], m.data[2], m.data[3],
                      m.data[4], m.data[5], m.data[6], m.data[7]);
        dispatch(m.data);
    }
}
