using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace TaskmanagerOBD2Reader
{
    internal class ObdSession : IDisposable
    {
        private uint _deviceId;
        private uint _channelId;
        private uint _filterId;
        private bool _open;

        private const uint BAUD     = 500000;
        private const uint PROTOCOL = Protocol.ISO15765;

        public void Open()
        {
            int r = J2534Api.Open(IntPtr.Zero, out _deviceId);
            Check(r, "PassThruOpen");

            r = J2534Api.Connect(_deviceId, PROTOCOL, 0, BAUD, out _channelId);
            Check(r, "PassThruConnect");

            ApplyConfig();
            InstallFilter();
            ClearBuffers();

            _open = true;
        }

        private void ApplyConfig()
        {
            // Disable loopback, set block size and STmin to 0 (let remote ECU control pacing)
            var cfgs = new SCONFIG[]
            {
                new SCONFIG { Parameter = ConfigParam.LOOPBACK,               Value = 0 },
                new SCONFIG { Parameter = ConfigParam.ISO15765_BS,            Value = 0 },
                new SCONFIG { Parameter = ConfigParam.ISO15765_STMIN,         Value = 0 },
                new SCONFIG { Parameter = ConfigParam.ISO15765_FRAME_PAD_VAL, Value = 0x00 },
            };

            int structSize = Marshal.SizeOf<SCONFIG>();
            IntPtr buf = Marshal.AllocHGlobal(structSize * cfgs.Length);
            try
            {
                for (int i = 0; i < cfgs.Length; i++)
                    Marshal.StructureToPtr(cfgs[i], buf + i * structSize, false);

                var list = new SCONFIG_LIST
                {
                    NumOfParams = (uint)cfgs.Length,
                    ConfigPtr   = buf
                };
                IntPtr pList = Marshal.AllocHGlobal(Marshal.SizeOf<SCONFIG_LIST>());
                try
                {
                    Marshal.StructureToPtr(list, pList, false);
                    J2534Api.Ioctl(_channelId, J2534Api.SET_CONFIG, pList, IntPtr.Zero);
                }
                finally { Marshal.FreeHGlobal(pList); }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        private void InstallFilter()
        {
            // Flow control filter: pass responses from 0x7E8-0x7EF, FC to 0x7E0-0x7E7
            var mask    = BuildCanMsg(0xFFFFFFF8, new byte[0]);
            var pattern = BuildCanMsg(0x000007E8, new byte[0]);
            var flowCtl = BuildCanMsg(0x000007E0, new byte[] { 0x30, 0x00, 0x00 });

            IntPtr pMask    = AllocMsg(mask);
            IntPtr pPattern = AllocMsg(pattern);
            IntPtr pFlow    = AllocMsg(flowCtl);
            try
            {
                int r = J2534Api.StartMsgFilter(_channelId, FilterType.FLOW_CONTROL_FILTER,
                    pMask, pPattern, pFlow, out _filterId);
                Check(r, "PassThruStartMsgFilter");
            }
            finally
            {
                Marshal.FreeHGlobal(pMask);
                Marshal.FreeHGlobal(pPattern);
                Marshal.FreeHGlobal(pFlow);
            }
        }

        private void ClearBuffers()
        {
            J2534Api.Ioctl(_channelId, J2534Api.CLEAR_RX_BUFFER, IntPtr.Zero, IntPtr.Zero);
            J2534Api.Ioctl(_channelId, J2534Api.CLEAR_TX_BUFFER, IntPtr.Zero, IntPtr.Zero);
        }

        // Send a Mode 01 PID request and return the two payload bytes A, B.
        // Returns false on timeout or invalid response.
        public bool RequestPid(byte pid, out byte a, out byte b)
        {
            a = 0; b = 0;
            byte[] req = { 0x01, pid };
            if (!WriteObd(req)) return false;

            return ReadPidResponse(pid, out a, out b);
        }

        private bool ReadPidResponse(byte pid, out byte a, out byte b)
        {
            a = 0; b = 0;
            long deadline = Environment.TickCount + 500;
            while (Environment.TickCount < deadline)
            {
                byte[] resp = ReadMsg(200);
                if (resp == null) continue;
                // resp[0..3] = CAN ID, resp[4] = 0x41, resp[5] = pid, resp[6] = A, resp[7] = B
                if (resp.Length >= 7 && resp[4] == 0x41 && resp[5] == pid)
                {
                    a = resp[6];
                    b = (resp.Length >= 8) ? resp[7] : (byte)0;
                    return true;
                }
            }
            return false;
        }

        // Read DTCs (Mode 03). Returns list of decoded DTC strings.
        public List<string> ReadDtcs()
        {
            byte[] req = { 0x03 };
            if (!WriteObd(req)) return new List<string>();

            byte[] resp = ReadMsg(1000);
            if (resp == null || resp.Length < 5) return new List<string>();

            // resp[0..3] = CAN ID, resp[4] = 0x43, resp[5] = count, resp[6..] = DTC pairs
            if (resp[4] != 0x43) return new List<string>();
            int count = resp[5];
            if (count == 0) return new List<string>();

            return PidDecoder.DecodeDtcs(resp, 6, count * 2);
        }

        // Clear DTCs (Mode 04).
        public bool ClearDtcs()
        {
            byte[] req = { 0x04 };
            if (!WriteObd(req)) return false;
            byte[] resp = ReadMsg(1000);
            return resp != null && resp.Length >= 5 && resp[4] == 0x44;
        }

        // Read battery voltage via PassThruIoctl READ_VBATT.
        public double ReadBatteryVoltage()
        {
            IntPtr pOut = Marshal.AllocHGlobal(4);
            try
            {
                int r = J2534Api.Ioctl(_channelId, J2534Api.READ_VBATT, IntPtr.Zero, pOut);
                if (r != J2534Err.STATUS_NOERROR) return 0;
                uint mv = (uint)Marshal.ReadInt32(pOut);
                return mv / 1000.0;
            }
            finally { Marshal.FreeHGlobal(pOut); }
        }

        // Read VIN (Mode 09, PID 0x02).
        public string ReadVin()
        {
            byte[] req = { 0x09, 0x02 };
            if (!WriteObd(req)) return null;

            // VIN can be multi-frame; collect up to 3 responses
            var vinBytes = new System.Collections.Generic.List<byte>();
            long deadline = Environment.TickCount + 1500;
            while (Environment.TickCount < deadline)
            {
                byte[] resp = ReadMsg(300);
                if (resp == null) break;
                // resp[0..3] = CAN ID, resp[4] = 0x49, resp[5] = 0x02, resp[6] = msg count, resp[7..] = VIN bytes
                if (resp.Length >= 7 && resp[4] == 0x49 && resp[5] == 0x02)
                {
                    for (int i = 7; i < resp.Length; i++) vinBytes.Add(resp[i]);
                    if (vinBytes.Count >= 17) break;
                }
            }
            if (vinBytes.Count == 0) return null;
            return Encoding.ASCII.GetString(vinBytes.ToArray()).TrimEnd('\0');
        }

        private bool WriteObd(byte[] payload)
        {
            var msg = PASSTHRU_MSG.Create(PROTOCOL, TxFlags.ISO15765_FRAME_PAD, payload);
            IntPtr p = AllocMsg(msg);
            try
            {
                uint count = 1;
                int r = J2534Api.WriteMsgs(_channelId, p, ref count, 500);
                return r == J2534Err.STATUS_NOERROR;
            }
            finally { Marshal.FreeHGlobal(p); }
        }

        private byte[] ReadMsg(uint timeoutMs)
        {
            int size = Marshal.SizeOf<PASSTHRU_MSG>();
            IntPtr p = Marshal.AllocHGlobal(size);
            try
            {
                // Zero out the struct
                for (int i = 0; i < size; i++) Marshal.WriteByte(p, i, 0);
                uint count = 1;
                int r = J2534Api.ReadMsgs(_channelId, p, ref count, timeoutMs);
                if (r != J2534Err.STATUS_NOERROR || count == 0) return null;

                uint dataSize = (uint)Marshal.ReadInt32(p, 4 * 4); // DataSize field offset
                if (dataSize == 0) return null;

                byte[] data = new byte[dataSize];
                IntPtr dataStart = p + 6 * 4; // offset past 6 uint fields
                Marshal.Copy(dataStart, data, 0, (int)dataSize);
                return data;
            }
            finally { Marshal.FreeHGlobal(p); }
        }

        private static PASSTHRU_MSG BuildCanMsg(uint canId, byte[] payload)
        {
            var msg = new PASSTHRU_MSG
            {
                ProtocolID    = Protocol.ISO15765,
                TxFlags       = TxFlags.ISO15765_FRAME_PAD,
                DataSize      = (uint)(4 + payload.Length),
                ExtraDataIndex = (uint)(4 + payload.Length),
                Data          = new byte[4128]
            };
            msg.Data[0] = (byte)((canId >> 24) & 0xFF);
            msg.Data[1] = (byte)((canId >> 16) & 0xFF);
            msg.Data[2] = (byte)((canId >>  8) & 0xFF);
            msg.Data[3] = (byte)( canId        & 0xFF);
            Array.Copy(payload, 0, msg.Data, 4, payload.Length);
            return msg;
        }

        private static IntPtr AllocMsg(PASSTHRU_MSG msg)
        {
            int size = Marshal.SizeOf<PASSTHRU_MSG>();
            IntPtr p = Marshal.AllocHGlobal(size);
            Marshal.StructureToPtr(msg, p, false);
            return p;
        }

        private static void Check(int result, string call)
        {
            if (result != J2534Err.STATUS_NOERROR)
                throw new InvalidOperationException($"{call} failed (0x{result:X2}): {J2534Api.GetError()}");
        }

        public void Dispose()
        {
            if (!_open) return;
            _open = false;
            try { J2534Api.StopMsgFilter(_channelId, _filterId); } catch { }
            try { J2534Api.Disconnect(_channelId); } catch { }
            try { J2534Api.Close(_deviceId); } catch { }
        }
    }
}
