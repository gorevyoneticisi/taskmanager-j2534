using System;
using System.Collections.Concurrent;
using System.IO.Ports;
using System.Threading;

namespace TaskmanagerBridge
{
    /// <summary>
    /// Singleton that owns the SerialPort connection to the STM32.
    /// Runs a dedicated background thread that continuously parses
    /// incoming 0xBB frames and pushes complete CAN frames to a queue
    /// that PassThruReadMsgs can dequeue from.
    /// </summary>
    public static class SerialBridge
    {
        // ── Serial port ───────────────────────────────────────────────────────
        private static SerialPort _port;
        private static readonly object _portLock = new object();

        // ── RX thread ─────────────────────────────────────────────────────────
        private static Thread _rxThread;
        private static volatile bool _rxRunning;

        // ── Incoming CAN frame queue ──────────────────────────────────────────
        // Populated by RX thread, consumed by PassThruReadMsgs.
        public static readonly ConcurrentQueue<CanFrame> RxQueue
            = new ConcurrentQueue<CanFrame>();

        // ── Statistics (visible in log / future UI) ───────────────────────────
        // FIX C3: private backing fields allow Interlocked operations so that
        // the RX thread (FramesRx, ParseErrors) and the caller thread (FramesTx)
        // never race on a non-atomic read-modify-write.
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

            while (RxQueue.TryDequeue(out _)) { }
            Interlocked.Exchange(ref _framesTx,    0);
            Interlocked.Exchange(ref _framesRx,    0);
            Interlocked.Exchange(ref _parseErrors, 0);

            _rxRunning = true;
            _rxThread  = new Thread(RxWorker) { IsBackground = true, Name = "SerialBridge_RX" };
            _rxThread.Start();

            Sniffa.LogTraffic("SYS_OPEN", 0, System.Text.Encoding.ASCII.GetBytes(comPort));
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

            Sniffa.LogTraffic("SYS_CLOSE", 0, new byte[] { 0x00 });
        }

        // ── Send a CAN frame to the STM32 ─────────────────────────────────────
        /// <summary>
        /// Builds and transmits one UART packet:
        ///   [0xAA][0x01][ID_H][ID_L][LEN][D0..Dn][XOR checksum]
        /// XOR covers ID_H, ID_L, and all data bytes.
        /// </summary>
        public static bool SendCanFrame(uint canId, byte[] data, int dataLen)
        {
            if (dataLen < 0 || dataLen > 8) return false;

            byte idHigh   = (byte)((canId >> 8) & 0xFF);
            byte idLow    = (byte)(canId & 0xFF);
            byte checksum = (byte)(idHigh ^ idLow);

            // Packet: header(1) + cmd(1) + idH(1) + idL(1) + len(1) + data(n) + chk(1)
            byte[] packet = new byte[5 + dataLen + 1];
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
        /// <summary>
        /// Continuously reads bytes from the serial port and reassembles
        /// STM32 response frames:
        ///   [0xBB][LEN][ID_H][ID_L][D0..Dn]
        /// Complete frames are pushed into RxQueue.
        /// </summary>
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
                try                              { raw = port.ReadByte(); }
                catch (TimeoutException)         { continue; }
                catch (InvalidOperationException){ Thread.Sleep(10); continue; }
                catch                            { state = 0; continue; }

                if (raw < 0) continue;
                byte b = (byte)raw;

                switch (state)
                {
                    case 0: // Wait for 0xBB header
                        if (b == 0xBB) state = 1;
                        break;

                    case 1: // Read length — valid range 1..8
                        if (b == 0 || b > 8) { Interlocked.Increment(ref _parseErrors); state = 0; break; }
                        frmLen = b;
                        state  = 2;
                        break;

                    case 2: // Read CAN ID high byte
                        idHigh = b;
                        state  = 3;
                        break;

                    case 3: // Read CAN ID low byte
                        idLow   = b;
                        frmData = new byte[frmLen];
                        dataIdx = 0;
                        state   = 4;
                        break;

                    case 4: // Read data bytes
                        frmData[dataIdx++] = b;
                        if (dataIdx >= frmLen)
                        {
                            uint canId = ((uint)idHigh << 8) | idLow;
                            RxQueue.Enqueue(new CanFrame(canId, frmData));
                            Sniffa.LogTraffic("RX", canId, frmData);
                            Interlocked.Increment(ref _framesRx);
                            state = 0;
                        }
                        break;
                }
            }
        }
    }

    // ── CAN frame value type ──────────────────────────────────────────────────
    /// <summary>Immutable snapshot of one received CAN frame, including a timestamp.</summary>
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
