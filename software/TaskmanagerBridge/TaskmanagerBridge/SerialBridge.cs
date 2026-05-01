using System;
using System.Collections.Concurrent;
using System.IO.Ports;
using System.Threading;

namespace TaskmanagerBridge
{
    // ═════════════════════════════════════════════════════════════════════════
    // UART wire protocol — STM32F407 <-> PC
    //
    // ── Firmware v1 (current, 11-bit IDs only) ────────────────────────────
    //
    //   PC → STM32 (send CAN frame):
    //     [0xAA][0x01][ID_H][ID_L][LEN][D0..Dn][XOR]
    //     XOR = ID_H ^ ID_L ^ D0 ^ ... ^ Dn
    //
    //   STM32 → PC (received CAN frame):
    //     [0xBB][LEN][ID_H][ID_L][D0..Dn]
    //     LEN = 1..8, no checksum on receive path
    //
    // ── Firmware v2 (planned, 29-bit IDs + baud rate switching) ──────────
    //
    //   PC → STM32 (send CAN frame, extended ID):
    //     [0xAA][0x01][ID3][ID2][ID1][ID0][LEN][D0..Dn][XOR]
    //     XOR = ID3 ^ ID2 ^ ID1 ^ ID0 ^ D0 ^ ... ^ Dn
    //
    //   PC → STM32 (set CAN baud rate):
    //     [0xAA][0x02][B3][B2][B1][B0][XOR]
    //     XOR = B3 ^ B2 ^ B1 ^ B0
    //     B3..B0 = baud rate in bits/sec, big-endian (e.g. 0x0007A120 = 500000)
    //
    //   STM32 → PC (received CAN frame, extended ID):
    //     [0xBB][LEN][ID3][ID2][ID1][ID0][D0..Dn]
    //     ID3 bit 31 set → 29-bit extended frame
    //
    // The DLL auto-detects v2 by checking the frame format byte reserved bit.
    // ═════════════════════════════════════════════════════════════════════════
    public static class SerialBridge
    {
        // ── Serial port ───────────────────────────────────────────────────────
        private static SerialPort     _port;
        private static readonly object _portLock = new object();

        // ── RX thread ─────────────────────────────────────────────────────────
        private static Thread        _rxThread;
        private static volatile bool _rxRunning;

        // ── Incoming CAN frame queue — BlockingCollection for efficient router ─
        // Bounded at 4096 frames (~100 ms of burst at 500 kbps). TryAdd drops
        // frames when full so the RX thread is never stalled by a slow consumer.
        public static readonly BlockingCollection<CanFrame> RxQueue
            = new BlockingCollection<CanFrame>(4096);

        // ── Statistics ────────────────────────────────────────────────────────
        private static int _framesTx;
        private static int _framesRx;
        private static int _parseErrors;

        public static int FramesTx    => _framesTx;
        public static int FramesRx    => _framesRx;
        public static int ParseErrors => _parseErrors;

        public static bool IsOpen
        {
            get { lock (_portLock) { return _port != null && _port.IsOpen; } }
        }

        // ── Open ──────────────────────────────────────────────────────────────
        public static void Open(string comPort)
        {
            lock (_portLock)
            {
                if (_port != null && _port.IsOpen) return;
                _port = new SerialPort(comPort, 921600, Parity.None, 8, StopBits.One)
                {
                    ReadTimeout  = 200,
                    WriteTimeout = 500,
                    DtrEnable    = false,
                    RtsEnable    = false
                };
                _port.Open();
                _port.DiscardInBuffer();
                _port.DiscardOutBuffer();
            }

            while (RxQueue.TryTake(out _)) { }
            Interlocked.Exchange(ref _framesTx,    0);
            Interlocked.Exchange(ref _framesRx,    0);
            Interlocked.Exchange(ref _parseErrors, 0);

            _rxRunning = true;
            _rxThread  = new Thread(RxWorker) { IsBackground = true, Name = "SerialBridge_RX" };
            _rxThread.Start();

            Sniffa.LogTraffic("SYS_OPEN", 0,
                System.Text.Encoding.ASCII.GetBytes(comPort));
        }

        // ── Close ─────────────────────────────────────────────────────────────
        public static void Close()
        {
            _rxRunning = false;
            _rxThread?.Join(600);
            _rxThread = null;

            lock (_portLock)
            {
                try { _port?.Close(); _port?.Dispose(); } catch { }
                _port = null;
            }

            Sniffa.LogTraffic("SYS_CLOSE", 0, null);
        }

        // ── Send a CAN frame to the STM32 (firmware v1 — 11-bit IDs) ─────────
        // Packet: [0xAA][0x01][ID_H][ID_L][LEN][D0..Dn][XOR]
        // XOR covers ID_H, ID_L, and all data bytes.
        //
        // Firmware v2 with 29-bit IDs requires a 4-byte ID field; update this
        // method once the STM32 firmware is upgraded.
        public static bool SendCanFrame(uint canId, byte[] data, int dataLen)
        {
            if (dataLen < 0 || dataLen > 8) return false;

            byte idHigh   = (byte)((canId >> 8) & 0xFF);
            byte idLow    = (byte)( canId        & 0xFF);
            byte checksum = (byte)(idHigh ^ idLow);

            byte[] packet = new byte[6 + dataLen];
            packet[0] = 0xAA;
            packet[1] = 0x01;
            packet[2] = idHigh;
            packet[3] = idLow;
            packet[4] = (byte)dataLen;

            for (int i = 0; i < dataLen; i++)
            {
                packet[5 + i] = data[i];
                checksum ^= data[i];
            }
            packet[5 + dataLen] = checksum;

            try
            {
                lock (_portLock)
                {
                    if (_port == null || !_port.IsOpen) return false;
                    _port.Write(packet, 0, packet.Length);
                }
                Interlocked.Increment(ref _framesTx);
                return true;
            }
            catch { return false; }
        }

        // ── Background RX parser ──────────────────────────────────────────────
        // Parses firmware v1 receive frames: [0xBB][LEN][ID_H][ID_L][D0..Dn]
        private static void RxWorker()
        {
            int    state   = 0;
            int    frmLen  = 0;
            byte   idHigh  = 0;
            byte   idLow   = 0;
            byte[] frmData = null;
            int    dataIdx = 0;

            while (_rxRunning)
            {
                SerialPort port;
                lock (_portLock) { port = _port; }
                if (port == null || !port.IsOpen) { Thread.Sleep(10); continue; }

                int raw;
                try                               { raw = port.ReadByte(); }
                catch (TimeoutException)          { continue; }
                catch (InvalidOperationException) { Thread.Sleep(10); continue; }
                catch                             { state = 0; continue; }

                if (raw < 0) continue;
                byte b = (byte)raw;

                switch (state)
                {
                    case 0:
                        if (b == 0xBB) state = 1;
                        break;

                    case 1: // LEN — valid 1..8
                        if (b == 0 || b > 8)
                        {
                            Interlocked.Increment(ref _parseErrors);
                            state = 0;
                            break;
                        }
                        frmLen = b;
                        state  = 2;
                        break;

                    case 2: // ID high byte
                        idHigh = b;
                        state  = 3;
                        break;

                    case 3: // ID low byte
                        idLow   = b;
                        frmData = new byte[frmLen];
                        dataIdx = 0;
                        state   = 4;
                        break;

                    case 4: // Data bytes
                        frmData[dataIdx++] = b;
                        if (dataIdx >= frmLen)
                        {
                            uint canId = ((uint)idHigh << 8) | idLow;
                            var frame  = new CanFrame(canId, frmData);
                            RxQueue.TryAdd(frame);
                            Sniffa.LogTraffic("RX", canId, frmData);
                            Interlocked.Increment(ref _framesRx);
                            state = 0;
                        }
                        break;
                }
            }
        }
    }

    // ── CAN frame value object ────────────────────────────────────────────────
    public class CanFrame
    {
        public uint   Id        { get; }
        public byte[] Data      { get; }
        public uint   Timestamp { get; }

        public CanFrame(uint id, byte[] data)
        {
            Id        = id;
            Data      = data;
            Timestamp = (uint)(Environment.TickCount & 0xFFFFFFFF);
        }
    }
}
