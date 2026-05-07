/*
 * ESP32 OBD2 Simulator — Web Control Panel Edition
 *
 * Hardware : ESP32 DevKit + SN65HVD230 CAN transceiver
 * CAN pins : TX=GPIO5, RX=GPIO4 — 500 kbps — 120 Ω termination at each end
 * WiFi AP  : "OBD2-Simulator" / "testdrive"  →  http://192.168.4.1
 *
 * Supported OBD2 services
 *   Mode 01 – Live PIDs (00/04/05/0B/0C/0D/0E/0F/10/11/20/2F/40/46/5C)
 *   Mode 03 – Read stored DTCs (ISO-TP multi-frame when > 2 DTCs present)
 *   Mode 04 – Clear DTCs (restored 30 s later to simulate persistent faults)
 *   Mode 09 – VIN via ISO-TP multi-frame
 *
 * Architecture
 *   can_task  (core 1, priority 5) — TWAI driver + OBD2 request dispatch
 *   http_task (core 0, priority 1) — WiFi AP + WebServer control panel
 *   Shared state: volatile float scalars (atomic on Xtensa 32-bit)
 *                 DTC list protected by FreeRTOS mutex
 */

#include <Arduino.h>
#include <WiFi.h>
#include <WebServer.h>
#include <driver/twai.h>
#include <freertos/FreeRTOS.h>
#include <freertos/task.h>
#include <freertos/semphr.h>
#include <string.h>
#include <stdio.h>

// ── Hardware / protocol constants ─────────────────────────────────────────────
#define CAN_TX_PIN   GPIO_NUM_5
#define CAN_RX_PIN   GPIO_NUM_4
#define OBD_FUNC_ID  0x7DF   // functional broadcast — tester → all ECUs
#define OBD_PHYS_ID  0x7E0   // physical request to this ECU (DLL sends FC here)
#define ECU_RESP_ID  0x7E8   // this ECU's response address

// ── WiFi AP ───────────────────────────────────────────────────────────────────
#define WIFI_SSID  "OBD2-Simulator"
#define WIFI_PASS  "testdrive"

// ── Simulation values ─────────────────────────────────────────────────────────
// 32-bit float read/write is atomic on Xtensa LX6; no mutex needed for scalars.
static volatile float s_rpm      = 800.0f;   // RPM
static volatile float s_coolant  = 85.0f;    // coolant temp °C
static volatile float s_speed    = 0.0f;     // vehicle speed km/h
static volatile float s_load     = 20.0f;    // engine load %
static volatile float s_throttle = 15.0f;    // throttle position %
static volatile float s_iat      = 25.0f;    // intake air temp °C
static volatile float s_map_kpa  = 35.0f;    // intake manifold pressure kPa
static volatile float s_timing   = 10.0f;    // timing advance degrees BTDC
static volatile float s_fuel     = 75.0f;    // fuel level %
static volatile float s_ambient  = 22.0f;    // ambient air temp °C
static volatile float s_oil_temp = 90.0f;    // oil temperature °C
// MAF is derived from load at response time: load * 0.25 + 2.0 g/s

// ── DTC store ─────────────────────────────────────────────────────────────────
#define MAX_DTCS 16

typedef struct {
    uint8_t hi;
    uint8_t lo;
    char    code[8];   // null-terminated, e.g. "P0300"
} Dtc;

static Dtc               s_dtcs[MAX_DTCS];
static Dtc               s_dtcs_backup[MAX_DTCS];  // saved on Mode 04 clear
static int               s_dtc_count        = 0;
static int               s_dtc_backup_count = 0;
static SemaphoreHandle_t s_dtc_mutex;

// Auto-restore DTCs 30 s after Mode 04 clear
static volatile bool     s_dtc_cleared  = false;
static volatile uint32_t s_dtc_clear_ms = 0;
#define DTC_RESTORE_MS  30000

// ── ISO-TP TX state machine ───────────────────────────────────────────────────
typedef enum { TX_IDLE, TX_WAIT_FC } IsoTpState;

static IsoTpState s_tx_state  = TX_IDLE;
static uint8_t    s_tx_buf[256];
static uint16_t   s_tx_total  = 0;
static uint16_t   s_tx_offset = 0;
static uint8_t    s_tx_sn     = 1;

// ── WebServer ─────────────────────────────────────────────────────────────────
static WebServer server(80);

// ── Embedded HTML control panel ───────────────────────────────────────────────
static const char HTML[] = R"HTML(
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>OBD2 Simulator</title>
<style>
*{box-sizing:border-box;margin:0;padding:0}
body{background:#1a1a2e;color:#e0e0e0;font-family:'Segoe UI',sans-serif;padding:20px}
h1{color:#00d4ff;margin-bottom:20px;font-size:1.4em;letter-spacing:1px}
h2{color:#00a8cc;font-size:.9em;margin-bottom:12px;text-transform:uppercase;letter-spacing:.5px}
.card{background:#16213e;border:1px solid #0f3460;border-radius:8px;padding:16px;margin-bottom:16px}
.row{display:flex;align-items:center;margin-bottom:8px;gap:10px}
.row label{width:165px;font-size:.82em;color:#aaa;flex-shrink:0}
.row input[type=range]{flex:1;accent-color:#00d4ff;cursor:pointer}
.row .val{width:75px;text-align:right;font-size:.85em;color:#00d4ff;font-weight:700}
.dtc-controls{display:flex;gap:8px;margin-bottom:12px;flex-wrap:wrap;align-items:center}
select,input[type=text]{background:#0f3460;color:#e0e0e0;border:1px solid #00a8cc;
  border-radius:4px;padding:5px 9px;font-size:.82em}
button{background:#0f3460;color:#00d4ff;border:1px solid #00a8cc;border-radius:4px;
  padding:5px 13px;cursor:pointer;font-size:.82em;transition:.15s}
button:hover{background:#00a8cc;color:#1a1a2e}
button.danger{color:#ff6b6b;border-color:#ff6b6b}
button.danger:hover{background:#ff6b6b;color:#1a1a2e}
#dtc-list{list-style:none}
#dtc-list li{display:flex;align-items:center;justify-content:space-between;
  padding:6px 10px;background:#0f3460;border-radius:4px;margin-bottom:6px;font-size:.83em}
#dtc-list li span{color:#ff6b6b;font-weight:700;margin-right:8px;font-family:monospace}
#status{font-size:.72em;color:#556;margin-top:8px;min-height:14px}
</style>
</head>
<body>
<h1>&#9881; OBD2 Simulator Control Panel</h1>

<div class="card">
<h2>Live Data</h2>
<div class="row"><label>RPM</label>
  <input type="range" id="rpm" min="0" max="8000" step="50" value="800"
    oninput="send('rpm',this.value,'F0')">
  <span class="val" id="rpm_v">800</span></div>
<div class="row"><label>Speed (km/h)</label>
  <input type="range" id="speed" min="0" max="300" step="1" value="0"
    oninput="send('speed',this.value,'F0')">
  <span class="val" id="speed_v">0</span></div>
<div class="row"><label>Engine Load (%)</label>
  <input type="range" id="load" min="0" max="100" step="1" value="20"
    oninput="send('load',this.value,'F1')">
  <span class="val" id="load_v">20.0</span></div>
<div class="row"><label>Throttle Position (%)</label>
  <input type="range" id="throttle" min="0" max="100" step="1" value="15"
    oninput="send('throttle',this.value,'F1')">
  <span class="val" id="throttle_v">15.0</span></div>
<div class="row"><label>Coolant Temp (&deg;C)</label>
  <input type="range" id="coolant" min="-40" max="150" step="1" value="85"
    oninput="send('coolant',this.value,'F0')">
  <span class="val" id="coolant_v">85</span></div>
<div class="row"><label>Intake Air Temp (&deg;C)</label>
  <input type="range" id="iat" min="-40" max="80" step="1" value="25"
    oninput="send('iat',this.value,'F0')">
  <span class="val" id="iat_v">25</span></div>
<div class="row"><label>MAP Pressure (kPa)</label>
  <input type="range" id="map_kpa" min="10" max="250" step="1" value="35"
    oninput="send('map_kpa',this.value,'F0')">
  <span class="val" id="map_kpa_v">35</span></div>
<div class="row"><label>Timing Advance (&deg;)</label>
  <input type="range" id="timing" min="-64" max="63" step="1" value="10"
    oninput="send('timing',this.value,'F1')">
  <span class="val" id="timing_v">10.0</span></div>
<div class="row"><label>Fuel Level (%)</label>
  <input type="range" id="fuel" min="0" max="100" step="1" value="75"
    oninput="send('fuel',this.value,'F1')">
  <span class="val" id="fuel_v">75.0</span></div>
<div class="row"><label>Ambient Temp (&deg;C)</label>
  <input type="range" id="ambient" min="-40" max="80" step="1" value="22"
    oninput="send('ambient',this.value,'F0')">
  <span class="val" id="ambient_v">22</span></div>
<div class="row"><label>Oil Temperature (&deg;C)</label>
  <input type="range" id="oil_temp" min="-40" max="150" step="1" value="90"
    oninput="send('oil_temp',this.value,'F0')">
  <span class="val" id="oil_temp_v">90</span></div>
<div id="status">Ready</div>
</div>

<div class="card">
<h2>Fault Codes (DTCs)</h2>
<div class="dtc-controls">
  <select id="preset">
    <option value="P0300">P0300 &mdash; Random Misfire</option>
    <option value="P0301">P0301 &mdash; Cylinder 1 Misfire</option>
    <option value="P0302">P0302 &mdash; Cylinder 2 Misfire</option>
    <option value="P0171">P0171 &mdash; System Too Lean (B1)</option>
    <option value="P0172">P0172 &mdash; System Too Rich (B1)</option>
    <option value="P0420">P0420 &mdash; Catalyst Efficiency Low</option>
    <option value="P0442">P0442 &mdash; EVAP Leak Detected</option>
    <option value="P0500">P0500 &mdash; VSS Malfunction</option>
  </select>
  <button onclick="addPreset()">Add Preset</button>
  <input type="text" id="custom" placeholder="e.g. P0101" maxlength="5" style="width:110px">
  <button onclick="addCustom()">Add Custom</button>
  <button class="danger" onclick="clearAll()">Clear All</button>
</div>
<ul id="dtc-list"></ul>
</div>

<script>
var _t=null;
function send(name,val,fmt){
  var v=parseFloat(val);
  document.getElementById(name+'_v').textContent=fmt==='F0'?Math.round(v):v.toFixed(1);
  clearTimeout(_t);
  _t=setTimeout(function(){
    fetch('/set?'+name+'='+v)
      .then(function(){document.getElementById('status').textContent=name+' = '+v+' ✓';})
      .catch(function(){document.getElementById('status').textContent='Connection error';});
  },80);
}
function addPreset(){addDtc(document.getElementById('preset').value);}
function addCustom(){
  var c=document.getElementById('custom').value.trim().toUpperCase();
  if(/^[PCBU][0-9A-F]{4}$/.test(c))addDtc(c);
  else alert('Format: letter (P/C/B/U) + 4 hex digits, e.g. P0300');
}
function addDtc(code){fetch('/dtc/add?code='+code).then(function(){refreshDtcs();});}
function clearDtc(code){fetch('/dtc/clear?code='+code).then(function(){refreshDtcs();});}
function clearAll(){fetch('/dtc/clearall').then(function(){refreshDtcs();});}
function refreshDtcs(){
  fetch('/status').then(function(r){return r.json();}).then(function(d){
    var ul=document.getElementById('dtc-list');
    ul.innerHTML='';
    if(d.dtcs.length===0){
      var li=document.createElement('li');
      li.style.color='#555';li.style.justifyContent='center';
      li.textContent='No active fault codes';ul.appendChild(li);
    }else{
      d.dtcs.forEach(function(c){
        var li=document.createElement('li');
        li.innerHTML='<div><span>'+c+'</span></div>'
          +'<button class="danger" onclick="clearDtc(\''+c+'\')">Clear</button>';
        ul.appendChild(li);
      });
    }
    var msg=d.dtcs.length+' active DTC(s)';
    if(d.restore_in>0)msg+=' — auto-restore in '+d.restore_in+'s';
    document.getElementById('status').textContent=msg;
  }).catch(function(){});
}
refreshDtcs();
setInterval(refreshDtcs,5000);
</script>
</body>
</html>
)HTML";

// ── DTC wire encoding ─────────────────────────────────────────────────────────
// "P0300" → hi=0x03, lo=0x00  (matches C# PidDecoder.DecodeDtcs bit layout)
// hi bits[7:6] = type (0=P,1=C,2=B,3=U), hi bits[5:0] = num>>8, lo = num&0xFF
static bool dtc_encode(const char *code, uint8_t *hi, uint8_t *lo) {
    if (!code || strlen(code) < 5) return false;
    uint8_t type;
    switch (code[0]) {
        case 'P': case 'p': type = 0; break;
        case 'C': case 'c': type = 1; break;
        case 'B': case 'b': type = 2; break;
        case 'U': case 'u': type = 3; break;
        default: return false;
    }
    uint16_t num = 0;
    for (int i = 1; i <= 4; i++) {
        char c = code[i];
        uint8_t nib;
        if      (c >= '0' && c <= '9') nib = (uint8_t)(c - '0');
        else if (c >= 'A' && c <= 'F') nib = (uint8_t)(c - 'A' + 10);
        else if (c >= 'a' && c <= 'f') nib = (uint8_t)(c - 'a' + 10);
        else return false;
        num = (uint16_t)((num << 4) | nib);
    }
    *hi = (uint8_t)((type << 6) | ((num >> 8) & 0x3F));
    *lo = (uint8_t)(num & 0xFF);
    return true;
}

// ── CAN TX primitives ─────────────────────────────────────────────────────────
static void can_tx_sf(const uint8_t *payload, uint8_t len) {
    twai_message_t tx = {};
    tx.identifier       = ECU_RESP_ID;
    tx.data_length_code = 8;
    tx.data[0]          = (uint8_t)(len & 0x0F);   // SF PCI: type=0, N_PCIlen=len
    memcpy(&tx.data[1], payload, len);
    for (int i = len + 1; i < 8; i++) tx.data[i] = 0xCC;
    twai_transmit(&tx, pdMS_TO_TICKS(10));
}

static void can_tx_ff(const uint8_t *payload, uint16_t total) {
    twai_message_t tx = {};
    tx.identifier       = ECU_RESP_ID;
    tx.data_length_code = 8;
    tx.data[0]          = (uint8_t)(0x10 | ((total >> 8) & 0x0F));  // FF PCI
    tx.data[1]          = (uint8_t)(total & 0xFF);
    memcpy(&tx.data[2], payload, 6);
    twai_transmit(&tx, pdMS_TO_TICKS(10));
}

static void can_tx_cf(uint8_t sn, const uint8_t *chunk, uint8_t len) {
    twai_message_t tx = {};
    tx.identifier       = ECU_RESP_ID;
    tx.data_length_code = 8;
    tx.data[0]          = (uint8_t)(0x20 | (sn & 0x0F));   // CF PCI
    memcpy(&tx.data[1], chunk, len);
    for (int i = len + 1; i < 8; i++) tx.data[i] = 0xCC;
    twai_transmit(&tx, pdMS_TO_TICKS(10));
}

// ── ISO-TP segmented send ─────────────────────────────────────────────────────
// Sends SF if payload ≤ 7 bytes; otherwise sends FF and enters TX_WAIT_FC.
static void isotp_send(const uint8_t *payload, uint16_t len) {
    if (len <= 7) {
        can_tx_sf(payload, (uint8_t)len);
        s_tx_state = TX_IDLE;
        return;
    }
    if (len > (uint16_t)sizeof(s_tx_buf)) len = (uint16_t)sizeof(s_tx_buf);
    memcpy(s_tx_buf, payload, len);
    s_tx_total  = len;
    s_tx_offset = 6;   // FF carries bytes 0-5; CFs start from byte 6
    s_tx_sn     = 1;
    can_tx_ff(payload, len);
    s_tx_state  = TX_WAIT_FC;
}

// Called when a ContinueToSend FC is received.  Sends CFs respecting BS/STmin.
static void isotp_on_fc(uint8_t bs, uint8_t stmin_ms) {
    uint8_t sent = 0;
    while (s_tx_offset < s_tx_total) {
        uint16_t remain = s_tx_total - s_tx_offset;
        uint8_t  chunk  = (remain > 7) ? 7 : (uint8_t)remain;
        can_tx_cf(s_tx_sn, &s_tx_buf[s_tx_offset], chunk);
        s_tx_sn      = (uint8_t)((s_tx_sn + 1) & 0x0F);
        s_tx_offset += chunk;
        sent++;
        if (stmin_ms > 0) vTaskDelay(pdMS_TO_TICKS(stmin_ms));
        if (bs > 0 && sent >= bs) {
            s_tx_state = TX_WAIT_FC;   // wait for the next FC block
            return;
        }
    }
    s_tx_state = TX_IDLE;
}

// ── Supported-PID bitmask ─────────────────────────────────────────────────────
// OBD2 bit convention: bit 31 = PID (group+1), bit 0 = PID (group+32).
// Bit position for PID p in group g = 32 - (p - g).
static uint32_t supported_pids_mask(uint8_t group) {
    static const uint8_t g00[] = {0x04,0x05,0x0B,0x0C,0x0D,0x0E,0x0F,0x10,0x11,0x20};
    static const uint8_t g20[] = {0x2F, 0x40};
    static const uint8_t g40[] = {0x46, 0x5C};
    const uint8_t *pids;
    uint8_t count;
    if      (group == 0x00) { pids = g00; count = sizeof(g00); }
    else if (group == 0x20) { pids = g20; count = sizeof(g20); }
    else if (group == 0x40) { pids = g40; count = sizeof(g40); }
    else return 0;
    uint32_t mask = 0;
    for (uint8_t i = 0; i < count; i++) {
        uint8_t offset = (uint8_t)(pids[i] - group);   // 1..32
        mask |= (1UL << (32u - offset));
    }
    return mask;
}

// ── Mode 01 PID response builder ─────────────────────────────────────────────
static void respond_mode01(uint8_t pid) {
    uint8_t  buf[8];
    uint8_t  len;
    uint16_t u16;
    uint32_t mask;
    float    v;

    buf[0] = 0x41;
    buf[1] = pid;
    len    = 2;

    switch (pid) {
        case 0x00: case 0x20: case 0x40:
            mask   = supported_pids_mask(pid);
            buf[2] = (uint8_t)(mask >> 24);
            buf[3] = (uint8_t)(mask >> 16);
            buf[4] = (uint8_t)(mask >>  8);
            buf[5] = (uint8_t)(mask);
            len    = 6;
            break;

        case 0x04:   // Engine load:    A = load% * 2.55
            buf[2] = (uint8_t)(s_load * 2.55f + 0.5f);
            len    = 3;
            break;

        case 0x05:   // Coolant temp:   A = temp + 40
            buf[2] = (uint8_t)(s_coolant + 40.5f);
            len    = 3;
            break;

        case 0x0B:   // MAP pressure:   A = kPa
            buf[2] = (uint8_t)(s_map_kpa + 0.5f);
            len    = 3;
            break;

        case 0x0C:   // Engine RPM:     (A*256 + B) / 4
            u16    = (uint16_t)(s_rpm * 4.0f);
            buf[2] = (uint8_t)(u16 >> 8);
            buf[3] = (uint8_t)(u16 & 0xFF);
            len    = 4;
            break;

        case 0x0D:   // Vehicle speed:  A = km/h
            buf[2] = (uint8_t)(s_speed + 0.5f);
            len    = 3;
            break;

        case 0x0E:   // Timing advance: A/2 - 64  →  A = (timing + 64) * 2
            buf[2] = (uint8_t)((s_timing + 64.5f) * 2.0f);
            len    = 3;
            break;

        case 0x0F:   // Intake air temp: A = IAT + 40
            buf[2] = (uint8_t)(s_iat + 40.5f);
            len    = 3;
            break;

        case 0x10: { // MAF:  (A*256 + B) / 100 g/s  — derived from load
            float maf = s_load * 0.25f + 2.0f;
            u16    = (uint16_t)(maf * 100.0f);
            buf[2] = (uint8_t)(u16 >> 8);
            buf[3] = (uint8_t)(u16 & 0xFF);
            len    = 4;
            break;
        }

        case 0x11:   // Throttle position: A/2.55
            buf[2] = (uint8_t)(s_throttle * 2.55f + 0.5f);
            len    = 3;
            break;

        case 0x2F:   // Fuel level: A/2.55
            buf[2] = (uint8_t)(s_fuel * 2.55f + 0.5f);
            len    = 3;
            break;

        case 0x46:   // Ambient temp: A = ambient + 40
            buf[2] = (uint8_t)(s_ambient + 40.5f);
            len    = 3;
            break;

        case 0x5C:   // Oil temperature: A = oil_temp + 40
            buf[2] = (uint8_t)(s_oil_temp + 40.5f);
            len    = 3;
            break;

        default:
            return;   // unsupported PID — no response is correct OBD2 behaviour
    }
    (void)v;
    can_tx_sf(buf, len);
}

// ── Mode 03 payload builder ───────────────────────────────────────────────────
static uint16_t build_mode03(uint8_t *out, uint16_t out_max) {
    xSemaphoreTake(s_dtc_mutex, portMAX_DELAY);
    int n  = s_dtc_count;
    out[0] = 0x43;
    out[1] = (uint8_t)n;
    uint16_t len = 2;
    for (int i = 0; i < n && (len + 1) < out_max; i++) {
        out[len++] = s_dtcs[i].hi;
        out[len++] = s_dtcs[i].lo;
    }
    xSemaphoreGive(s_dtc_mutex);
    return len;
}

// ── CAN RX dispatcher ─────────────────────────────────────────────────────────
static void handle_rx(const twai_message_t *rx) {
    uint8_t pci      = rx->data[0];
    uint8_t pci_type = (pci >> 4) & 0x0F;

    // Flow Control frame (type=3) — tester ACKs our First Frame
    if (pci_type == 0x03) {
        if (s_tx_state == TX_WAIT_FC) {
            uint8_t fs = pci & 0x0F;           // 0=CTS, 1=Wait, 2=Overflow
            if (fs == 0) {
                uint8_t bs      = rx->data[1];
                uint8_t stmin   = rx->data[2];
                // STmin: 0x00-0x7F = 0-127 ms; 0xF1-0xF9 = 100-900 µs (round to 1 ms)
                uint8_t delay_ms = (stmin <= 0x7F) ? stmin : 1;
                isotp_on_fc(bs, delay_ms);
            }
            // fs=1 (Wait): tester will send another FC — nothing to do
            // fs=2 (Overflow): abort; tester will retry
        }
        return;
    }

    // Only dispatch Single Frames (type=0)
    if (pci_type != 0x00) return;

    uint8_t data_len = pci & 0x0F;
    if (data_len < 1 || rx->data_length_code < (uint8_t)(data_len + 1)) return;

    uint8_t service = rx->data[1];
    uint8_t sub     = (data_len >= 2) ? rx->data[2] : 0;

    // Abort any stale in-progress multi-frame; new request takes priority
    s_tx_state = TX_IDLE;

    switch (service) {

        case 0x01:   // Mode 01 — Live data PID
            respond_mode01(sub);
            break;

        case 0x03: { // Mode 03 — Read stored DTCs
            uint8_t  buf[2 + MAX_DTCS * 2];
            uint16_t len = build_mode03(buf, (uint16_t)sizeof(buf));
            isotp_send(buf, len);
            break;
        }

        case 0x04: { // Mode 04 — Clear DTCs; backs up list; restores in DTC_RESTORE_MS
            xSemaphoreTake(s_dtc_mutex, portMAX_DELAY);
            memcpy(s_dtcs_backup, s_dtcs, (size_t)s_dtc_count * sizeof(Dtc));
            s_dtc_backup_count = s_dtc_count;
            s_dtc_count        = 0;
            s_dtc_cleared      = true;
            s_dtc_clear_ms     = millis();
            xSemaphoreGive(s_dtc_mutex);
            uint8_t resp = 0x44;
            can_tx_sf(&resp, 1);
            break;
        }

        case 0x09:   // Mode 09 — Vehicle information
            if (sub == 0x02) {
                // VIN: payload = [0x49][0x02][0x01] + 17 VIN bytes = 20 bytes (multi-frame)
                static const char vin[] = "1HGCM82633A004352";
                uint8_t buf[20];
                buf[0] = 0x49;
                buf[1] = 0x02;
                buf[2] = 0x01;   // InfoType count
                memcpy(&buf[3], vin, 17);
                isotp_send(buf, 20);
            }
            break;

        default:
            break;   // no negative response — real ECUs silently ignore unknown services
    }
}

// ── CAN task (core 1, priority 5) ────────────────────────────────────────────
static void can_task(void *arg) {
    twai_general_config_t g_cfg = TWAI_GENERAL_CONFIG_DEFAULT(CAN_TX_PIN, CAN_RX_PIN, TWAI_MODE_NORMAL);
    g_cfg.tx_queue_len = 16;
    g_cfg.rx_queue_len = 32;
    twai_timing_config_t  t_cfg = TWAI_TIMING_CONFIG_500KBITS();
    twai_filter_config_t  f_cfg = TWAI_FILTER_CONFIG_ACCEPT_ALL();

    ESP_ERROR_CHECK(twai_driver_install(&g_cfg, &t_cfg, &f_cfg));
    ESP_ERROR_CHECK(twai_start());
    Serial.printf("[CAN] 500 kbps  TX=GPIO%d  RX=GPIO%d\n",
                  (int)CAN_TX_PIN, (int)CAN_RX_PIN);

    for (;;) {
        twai_message_t rx;
        if (twai_receive(&rx, pdMS_TO_TICKS(5)) == ESP_OK) {
            if (rx.identifier == OBD_FUNC_ID || rx.identifier == OBD_PHYS_ID) {
                handle_rx(&rx);
            }
        }

        // Auto-restore DTCs DTC_RESTORE_MS after a Mode 04 clear
        if (s_dtc_cleared && (millis() - s_dtc_clear_ms) >= DTC_RESTORE_MS) {
            xSemaphoreTake(s_dtc_mutex, portMAX_DELAY);
            memcpy(s_dtcs, s_dtcs_backup, (size_t)s_dtc_backup_count * sizeof(Dtc));
            s_dtc_count   = s_dtc_backup_count;
            s_dtc_cleared = false;
            xSemaphoreGive(s_dtc_mutex);
            Serial.println("[CAN] DTCs auto-restored");
        }
    }
}

// ── HTTP helpers ──────────────────────────────────────────────────────────────
static float clampf(float v, float lo, float hi) {
    if (v < lo) return lo;
    if (v > hi) return hi;
    return v;
}

static float arg_float(const String &name, float def) {
    return server.hasArg(name) ? server.arg(name).toFloat() : def;
}

// ── HTTP handlers ─────────────────────────────────────────────────────────────
static void handle_root()          { server.send(200, "text/html", HTML); }

static void handle_set() {
    if (server.hasArg("rpm"))      s_rpm      = clampf(arg_float("rpm",      s_rpm),       0.0f,  8000.0f);
    if (server.hasArg("coolant"))  s_coolant  = clampf(arg_float("coolant",  s_coolant),  -40.0f,  150.0f);
    if (server.hasArg("speed"))    s_speed    = clampf(arg_float("speed",    s_speed),     0.0f,   300.0f);
    if (server.hasArg("load"))     s_load     = clampf(arg_float("load",     s_load),      0.0f,   100.0f);
    if (server.hasArg("throttle")) s_throttle = clampf(arg_float("throttle", s_throttle),  0.0f,   100.0f);
    if (server.hasArg("iat"))      s_iat      = clampf(arg_float("iat",      s_iat),      -40.0f,   80.0f);
    if (server.hasArg("map_kpa"))  s_map_kpa  = clampf(arg_float("map_kpa",  s_map_kpa),  10.0f,   250.0f);
    if (server.hasArg("timing"))   s_timing   = clampf(arg_float("timing",   s_timing),  -64.0f,    63.0f);
    if (server.hasArg("fuel"))     s_fuel     = clampf(arg_float("fuel",     s_fuel),      0.0f,   100.0f);
    if (server.hasArg("ambient"))  s_ambient  = clampf(arg_float("ambient",  s_ambient),  -40.0f,   80.0f);
    if (server.hasArg("oil_temp")) s_oil_temp = clampf(arg_float("oil_temp", s_oil_temp), -40.0f,  150.0f);
    server.send(200, "text/plain", "OK");
}

static void handle_dtc_add() {
    if (!server.hasArg("code")) { server.send(400, "text/plain", "Missing code"); return; }
    String code = server.arg("code");
    code.toUpperCase();
    uint8_t hi, lo;
    if (!dtc_encode(code.c_str(), &hi, &lo)) {
        server.send(400, "text/plain", "Invalid DTC format");
        return;
    }
    xSemaphoreTake(s_dtc_mutex, portMAX_DELAY);
    bool dup = false;
    for (int i = 0; i < s_dtc_count; i++) {
        if (s_dtcs[i].hi == hi && s_dtcs[i].lo == lo) { dup = true; break; }
    }
    if (!dup && s_dtc_count < MAX_DTCS) {
        s_dtcs[s_dtc_count].hi = hi;
        s_dtcs[s_dtc_count].lo = lo;
        strncpy(s_dtcs[s_dtc_count].code, code.c_str(), 7);
        s_dtcs[s_dtc_count].code[7] = '\0';
        s_dtc_count++;
    }
    xSemaphoreGive(s_dtc_mutex);
    server.send(200, "text/plain", "OK");
}

static void handle_dtc_clear() {
    if (!server.hasArg("code")) { server.send(400, "text/plain", "Missing code"); return; }
    String code = server.arg("code");
    code.toUpperCase();
    uint8_t hi, lo;
    if (!dtc_encode(code.c_str(), &hi, &lo)) {
        server.send(400, "text/plain", "Invalid DTC format");
        return;
    }
    xSemaphoreTake(s_dtc_mutex, portMAX_DELAY);
    for (int i = 0; i < s_dtc_count; i++) {
        if (s_dtcs[i].hi == hi && s_dtcs[i].lo == lo) {
            s_dtcs[i] = s_dtcs[s_dtc_count - 1];   // O(1) swap-remove
            s_dtc_count--;
            break;
        }
    }
    xSemaphoreGive(s_dtc_mutex);
    server.send(200, "text/plain", "OK");
}

static void handle_dtc_clearall() {
    xSemaphoreTake(s_dtc_mutex, portMAX_DELAY);
    s_dtc_count = 0;
    xSemaphoreGive(s_dtc_mutex);
    server.send(200, "text/plain", "OK");
}

static void handle_status() {
    String json;
    json.reserve(300);
    json  = "{\"rpm\":";       json += String(s_rpm, 1);
    json += ",\"coolant\":";   json += String(s_coolant, 1);
    json += ",\"speed\":";     json += String(s_speed, 1);
    json += ",\"load\":";      json += String(s_load, 1);
    json += ",\"throttle\":";  json += String(s_throttle, 1);
    json += ",\"restore_in\":";
    if (s_dtc_cleared) {
        uint32_t elapsed = millis() - s_dtc_clear_ms;
        uint32_t remain  = (elapsed < DTC_RESTORE_MS) ? (DTC_RESTORE_MS - elapsed) / 1000 : 0;
        json += String(remain);
    } else {
        json += "0";
    }
    json += ",\"dtcs\":[";
    xSemaphoreTake(s_dtc_mutex, portMAX_DELAY);
    for (int i = 0; i < s_dtc_count; i++) {
        if (i > 0) json += ",";
        json += "\"";
        json += s_dtcs[i].code;
        json += "\"";
    }
    xSemaphoreGive(s_dtc_mutex);
    json += "]}";
    server.send(200, "application/json", json);
}

static void handle_not_found() { server.send(404, "text/plain", "Not found"); }

// ── HTTP task (core 0, priority 1) ────────────────────────────────────────────
static void http_task(void *arg) {
    WiFi.softAP(WIFI_SSID, WIFI_PASS);
    Serial.printf("[WiFi] AP up  SSID=%-16s  IP=%s\n",
                  WIFI_SSID, WiFi.softAPIP().toString().c_str());

    server.on("/",             HTTP_GET, handle_root);
    server.on("/set",          HTTP_GET, handle_set);
    server.on("/dtc/add",      HTTP_GET, handle_dtc_add);
    server.on("/dtc/clear",    HTTP_GET, handle_dtc_clear);
    server.on("/dtc/clearall", HTTP_GET, handle_dtc_clearall);
    server.on("/status",       HTTP_GET, handle_status);
    server.onNotFound(handle_not_found);
    server.begin();

    for (;;) {
        server.handleClient();
        vTaskDelay(pdMS_TO_TICKS(5));
    }
}

// ── Arduino entry points ──────────────────────────────────────────────────────
void setup() {
    Serial.begin(115200);
    Serial.println("\n[OBD2-SIM] Web Control Panel Edition");

    s_dtc_mutex = xSemaphoreCreateMutex();

    xTaskCreatePinnedToCore(can_task,  "can_task",  4096, NULL, 5, NULL, 1);
    xTaskCreatePinnedToCore(http_task, "http_task", 8192, NULL, 1, NULL, 0);
}

void loop() {
    vTaskDelay(pdMS_TO_TICKS(1000));   // main task idles; all work is in pinned tasks
}
