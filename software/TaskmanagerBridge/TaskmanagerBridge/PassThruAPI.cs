using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace TaskmanagerBridge
{
    // ── J2534 structures ──────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    public struct PASSTHRU_MSG
    {
        public uint ProtocolID;
        public uint RxStatus;
        public uint TxFlags;
        public uint Timestamp;
        public uint DataSize;
        public uint ExtraDataIndex;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4128)]
        public byte[] Data;
    }

    /// <summary>Single parameter entry used in GET_CONFIG / SET_CONFIG Ioctl.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SCONFIG
    {
        public uint Parameter;
        public uint Value;
    }

    /// <summary>List of SCONFIG entries passed to PassThruIoctl.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SCONFIG_LIST
    {
        public uint NumOfParams;
        public IntPtr ConfigPtr;   // Pointer to array of SCONFIG
    }

    // ── J2534 Protocol IDs ────────────────────────────────────────────────────
    internal static class ProtocolID
    {
        public const uint CAN = 5;
        public const uint ISO15765 = 6;
    }

    // ── J2534 Ioctl IDs ───────────────────────────────────────────────────────
    internal static class IoctlID
    {
        public const uint GET_CONFIG = 0x01;
        public const uint SET_CONFIG = 0x02;
        public const uint READ_VBATT = 0x03;
        public const uint CLEAR_TX_BUFFER = 0x05;
        public const uint CLEAR_RX_BUFFER = 0x06;
        public const uint CLEAR_PERIODIC_MSGS = 0x07;
        public const uint CLEAR_MSG_FILTERS = 0x08;
        public const uint CLEAR_FUNCT_MSG_LOOKUP_TABLE = 0x09;
        public const uint ADD_TO_FUNCT_MSG_LOOKUP_TABLE = 0x0A;
        public const uint DELETE_FROM_FUNCT_MSG_LOOKUP_TABLE = 0x0B;
        public const uint READ_PROG_VOLTAGE = 0x0C;
        public const uint READ_VBATT_EXT = 0x10001; // DrewTech extension, used by Techstream/ODIS
    }

    // ── SCONFIG Parameter IDs ─────────────────────────────────────────────────
    internal static class ConfigParam
    {
        public const uint DATA_RATE = 0x01;
        public const uint LOOPBACK = 0x03;
        public const uint BIT_SAMPLE_POINT = 0x04;
        public const uint SYNC_MODE = 0x05;
        public const uint J1962_PINS = 0x09;
        public const uint ISO15765_BS = 0x14;
        public const uint ISO15765_STMIN = 0x15;
        public const uint BS_TX = 0x16;
        public const uint STMIN_TX = 0x17;
    }

    // ── J2534 Return codes ────────────────────────────────────────────────────
    internal static class J2534Err
    {
        public const int STATUS_NOERROR = 0x00;
        public const int ERR_BUFFER_EMPTY = 0x10;
        public const int ERR_BUFFER_FULL = 0x11;
        public const int ERR_TIMEOUT = 0x14;
        public const int STATUS_FAILED = 0xFF;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Main J2534 API class — 14 mandatory exports
    // ═════════════════════════════════════════════════════════════════════════
    public class PassThruAPI
    {
        private static readonly ThreadLocal<string> _lastError
            = new ThreadLocal<string>(() => "No error");

        // ── Config dialog ─────────────────────────────────────────────────────
        /// <summary>
        /// Shows the WinForms config dialog on a dedicated STA thread.
        /// J2534 host apps may call from an MTA thread, so we must create
        /// our own STA thread for WinForms to work correctly.
        /// </summary>
        private static bool ShowConfigDialog()
        {
            bool confirmed = false;
            var t = new Thread(() =>
            {
                Application.EnableVisualStyles();
                using (var dlg = new ConfigDialog())
                {
                    confirmed = dlg.ShowDialog() == DialogResult.OK;
                }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true; // FIX C4: don't hold the CLR alive if host crashes before Join()
            t.Start();
            t.Join();
            return confirmed;
        }

        // ─────────────────────────────────────────────────────────────────────
        // 1. PassThruOpen
        //    Called first by every diagnostic app.
        //    We show the config dialog here (unless RememberSettings is set).
        // ─────────────────────────────────────────────────────────────────────
        [DllExport("PassThruOpen", CallingConvention = CallingConvention.StdCall)]
        public static int PassThruOpen(IntPtr pName, out uint pDeviceID)
        {
            pDeviceID = 1;
            _lastError.Value = "No error";

            try
            {
                if (SerialBridge.IsOpen) return J2534Err.STATUS_NOERROR;

                if (!BridgeConfig.RememberSettings)
                {
                    bool ok = ShowConfigDialog();
                    if (!ok)
                    {
                        _lastError.Value = "User cancelled configuration.";
                        return J2534Err.STATUS_FAILED;
                    }
                }

                SerialBridge.Open(BridgeConfig.ComPort);
                return J2534Err.STATUS_NOERROR;
            }
            catch (Exception ex)
            {
                _lastError.Value = $"PassThruOpen: {ex.Message}";
                BridgeConfig.ClearRemembered();
                return J2534Err.STATUS_FAILED;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // 2. PassThruConnect
        //    Called after Open. Protocol 5 = CAN, 6 = ISO15765.
        // ─────────────────────────────────────────────────────────────────────
        [DllExport("PassThruConnect", CallingConvention = CallingConvention.StdCall)]
        public static int PassThruConnect(uint DeviceID, uint ProtocolID, uint Flags,
                                          uint BaudRate, out uint pChannelID)
        {
            pChannelID = 1;
            _lastError.Value = "No error";

            Sniffa.LogTraffic("SYS_CONNECT",
                BaudRate,
                new byte[] { (byte)(ProtocolID & 0xFF), (byte)(Flags & 0xFF) });

            // NOTE: CAN speed is fixed by STM32 firmware (500 kbps default).
            return J2534Err.STATUS_NOERROR;
        }

        // ─────────────────────────────────────────────────────────────────────
        // 3. PassThruWriteMsgs
        //    Iterates through the message array, builds UART packets,
        //    and sends each CAN frame to the STM32.
        // ─────────────────────────────────────────────────────────────────────
        [DllExport("PassThruWriteMsgs", CallingConvention = CallingConvention.StdCall)]
        public static int PassThruWriteMsgs(uint ChannelID, IntPtr pMsg,
                                            ref uint pNumMsgs, uint Timeout)
        {
            if (pMsg == IntPtr.Zero || pNumMsgs == 0) return J2534Err.STATUS_FAILED;
            if (!SerialBridge.IsOpen)
            {
                _lastError.Value = "Serial port not open.";
                return J2534Err.STATUS_FAILED;
            }

            uint sent = 0;
            int structSz = Marshal.SizeOf(typeof(PASSTHRU_MSG));

            try
            {
                for (int i = 0; i < pNumMsgs; i++)
                {
                    IntPtr ptr = new IntPtr(pMsg.ToInt64() + (i * structSz));
                    PASSTHRU_MSG msg = (PASSTHRU_MSG)Marshal.PtrToStructure(ptr, typeof(PASSTHRU_MSG));

                    if (msg.DataSize == 0 || msg.DataSize > 4128) { sent++; continue; }

                    // J2534 CAN: first 4 bytes of Data[] are the CAN ID (big-endian)
                    uint canId = 0;
                    int offset = 0;
                    if (msg.DataSize >= 4)
                    {
                        canId = (uint)(msg.Data[0] << 24 | msg.Data[1] << 16
                                      | msg.Data[2] << 8 | msg.Data[3]);
                        offset = 4;
                    }

                    int payloadLen = (int)msg.DataSize - offset;
                    if (payloadLen < 0) payloadLen = 0;
                    if (payloadLen > 8) payloadLen = 8;

                    byte[] payload = new byte[payloadLen];
                    if (payloadLen > 0)
                        Array.Copy(msg.Data, offset, payload, 0, payloadLen);

                    Sniffa.LogTraffic("TX", canId, payload);
                    SerialBridge.SendCanFrame(canId, payload, payloadLen);
                    sent++;
                }

                pNumMsgs = sent;
                _lastError.Value = "No error";
                return J2534Err.STATUS_NOERROR;
            }
            catch (Exception ex)
            {
                _lastError.Value = $"WriteMsgs: {ex.Message}";
                pNumMsgs = sent;
                return J2534Err.STATUS_FAILED;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // 4. PassThruReadMsgs
        //    Dequeues CAN frames received from the STM32.
        //    Polls the queue for up to Timeout ms before returning.
        // ─────────────────────────────────────────────────────────────────────
        [DllExport("PassThruReadMsgs", CallingConvention = CallingConvention.StdCall)]
        public static int PassThruReadMsgs(uint ChannelID, IntPtr pMsg,
                                           ref uint pNumMsgs, uint Timeout)
        {
            if (pMsg == IntPtr.Zero || pNumMsgs == 0) return J2534Err.STATUS_FAILED;

            int structSz = Marshal.SizeOf(typeof(PASSTHRU_MSG));
            uint delivered = 0;
            uint maxMsgs = pNumMsgs;
            int elapsed = 0;
            const int POLL = 5; // ms per poll cycle

            try
            {
                while (delivered < maxMsgs)
                {
                    if (SerialBridge.RxQueue.TryDequeue(out CanFrame frame))
                    {
                        PASSTHRU_MSG outMsg = new PASSTHRU_MSG
                        {
                            ProtocolID = ProtocolID.CAN,
                            RxStatus = 0,
                            TxFlags = 0,
                            Timestamp = frame.Timestamp,
                            DataSize = (uint)(4 + frame.Data.Length),
                            ExtraDataIndex = 0,
                            Data = new byte[4128]
                        };

                        // Pack CAN ID into first 4 bytes (big-endian, J2534 spec)
                        outMsg.Data[0] = (byte)((frame.Id >> 24) & 0xFF);
                        outMsg.Data[1] = (byte)((frame.Id >> 16) & 0xFF);
                        outMsg.Data[2] = (byte)((frame.Id >> 8) & 0xFF);
                        outMsg.Data[3] = (byte)(frame.Id & 0xFF);
                        Array.Copy(frame.Data, 0, outMsg.Data, 4, frame.Data.Length);

                        IntPtr slot = new IntPtr(pMsg.ToInt64() + (delivered * structSz));
                        Marshal.StructureToPtr(outMsg, slot, false);
                        delivered++;
                        elapsed = 0;
                    }
                    else
                    {
                        if (elapsed >= (int)Timeout) break;
                        if (delivered > 0) break;
                        Thread.Sleep(POLL);
                        elapsed += POLL;
                    }
                }

                pNumMsgs = delivered;
                _lastError.Value = "No error";

                // ERR_BUFFER_EMPTY is the standard J2534 code for "no messages ready".
                // Returning STATUS_NOERROR with 0 messages confuses some apps (e.g. Techstream).
                return (delivered == 0) ? J2534Err.ERR_BUFFER_EMPTY : J2534Err.STATUS_NOERROR;
            }
            catch (Exception ex)
            {
                _lastError.Value = $"ReadMsgs: {ex.Message}";
                pNumMsgs = delivered;
                return J2534Err.STATUS_FAILED;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // 5. PassThruClose
        // ─────────────────────────────────────────────────────────────────────
        [DllExport("PassThruClose", CallingConvention = CallingConvention.StdCall)]
        public static int PassThruClose(uint DeviceID)
        {
            _lastError.Value = "No error";
            SerialBridge.Close();
            return J2534Err.STATUS_NOERROR;
        }

        // ─────────────────────────────────────────────────────────────────────
        // 6. PassThruDisconnect
        // ─────────────────────────────────────────────────────────────────────
        [DllExport("PassThruDisconnect", CallingConvention = CallingConvention.StdCall)]
        public static int PassThruDisconnect(uint ChannelID)
        {
            _lastError.Value = "No error";
            return J2534Err.STATUS_NOERROR;
        }

        // ─────────────────────────────────────────────────────────────────────
        // 7. PassThruIoctl
        //    This is the most important function for professional tool compat.
        //    Techstream and ODIS hammer this constantly.
        //
        //    FIX A3: renamed parameter from IoctlID to ioctlId to eliminate
        //    the name collision with the static class IoctlID in this namespace.
        // ─────────────────────────────────────────────────────────────────────
        [DllExport("PassThruIoctl", CallingConvention = CallingConvention.StdCall)]
        public static int PassThruIoctl(uint ChannelID, uint ioctlId,
                                        IntPtr pInput, IntPtr pOutput)
        {
            _lastError.Value = "No error";

            try
            {
                switch (ioctlId)
                {
                    // ── Clear buffers ─────────────────────────────────────────
                    case IoctlID.CLEAR_RX_BUFFER:
                    case IoctlID.CLEAR_TX_BUFFER:
                        while (SerialBridge.RxQueue.TryDequeue(out _)) { }
                        Sniffa.LogTraffic("SYS_CLEAR_BUF", 0, new byte[] { (byte)(ioctlId & 0xFF) });
                        return J2534Err.STATUS_NOERROR;

                    case IoctlID.CLEAR_PERIODIC_MSGS:
                    case IoctlID.CLEAR_MSG_FILTERS:
                    case IoctlID.CLEAR_FUNCT_MSG_LOOKUP_TABLE:
                        return J2534Err.STATUS_NOERROR;

                    // ── Battery voltage ───────────────────────────────────────
                    // Both 0x03 and 0x10001 are used depending on the app.
                    // Return 14200 mV (14.2 V) — a healthy running engine value.
                    // Techstream and ODIS reject connections if this returns 0.
                    case IoctlID.READ_VBATT:
                    case IoctlID.READ_VBATT_EXT:
                    case IoctlID.READ_PROG_VOLTAGE:
                        if (pOutput != IntPtr.Zero)
                            Marshal.WriteInt32(pOutput, 14200); // millivolts
                        Sniffa.LogTraffic("SYS_VBATT", 14200, new byte[] { 0x00 });
                        return J2534Err.STATUS_NOERROR;

                    // ── GET_CONFIG ────────────────────────────────────────────
                    // The app sends an SCONFIG_LIST asking for current settings.
                    // We return sensible defaults for all known parameters.
                    case IoctlID.GET_CONFIG:
                        if (pInput != IntPtr.Zero)
                        {
                            SCONFIG_LIST list = (SCONFIG_LIST)Marshal.PtrToStructure(
                                pInput, typeof(SCONFIG_LIST));

                            // FIX A4: guard ConfigPtr before pointer arithmetic
                            if (list.ConfigPtr == IntPtr.Zero)
                                return J2534Err.STATUS_NOERROR;

                            for (int i = 0; i < list.NumOfParams; i++)
                            {
                                IntPtr itemPtr = new IntPtr(
                                    list.ConfigPtr.ToInt64() + i * Marshal.SizeOf(typeof(SCONFIG)));
                                SCONFIG cfg = (SCONFIG)Marshal.PtrToStructure(itemPtr, typeof(SCONFIG));

                                cfg.Value = GetConfigValue(cfg.Parameter);
                                Marshal.StructureToPtr(cfg, itemPtr, false);
                            }
                        }
                        return J2534Err.STATUS_NOERROR;

                    // ── SET_CONFIG ────────────────────────────────────────────
                    // The app sets parameters (baud rate, loopback, etc.).
                    // We log them for debugging; future work is forwarding to STM32.
                    case IoctlID.SET_CONFIG:
                        if (pInput != IntPtr.Zero)
                        {
                            SCONFIG_LIST list = (SCONFIG_LIST)Marshal.PtrToStructure(
                                pInput, typeof(SCONFIG_LIST));

                            // FIX A4: guard ConfigPtr before pointer arithmetic
                            if (list.ConfigPtr == IntPtr.Zero)
                                return J2534Err.STATUS_NOERROR;

                            for (int i = 0; i < list.NumOfParams; i++)
                            {
                                IntPtr itemPtr = new IntPtr(
                                    list.ConfigPtr.ToInt64() + i * Marshal.SizeOf(typeof(SCONFIG)));
                                SCONFIG cfg = (SCONFIG)Marshal.PtrToStructure(itemPtr, typeof(SCONFIG));

                                Sniffa.LogTraffic("SYS_SET_CFG",
                                    cfg.Parameter,
                                    BitConverter.GetBytes(cfg.Value));
                            }
                        }
                        return J2534Err.STATUS_NOERROR;

                    default:
                        Sniffa.LogTraffic("SYS_IOCTL_UNKNOWN", ioctlId, new byte[] { 0x00 });
                        return J2534Err.STATUS_NOERROR;
                }
            }
            catch (Exception ex)
            {
                _lastError.Value = $"Ioctl 0x{ioctlId:X}: {ex.Message}";
                return J2534Err.STATUS_NOERROR; // Never fail Ioctl — it kills the app session
            }
        }

        // ── GET_CONFIG value lookup ───────────────────────────────────────────
        private static uint GetConfigValue(uint param)
        {
            switch (param)
            {
                case ConfigParam.DATA_RATE:          return (uint)BridgeConfig.CanBaudRate;
                case ConfigParam.LOOPBACK:           return 0;       // Off
                case ConfigParam.BIT_SAMPLE_POINT:   return 80;      // 80% (typical)
                case ConfigParam.SYNC_MODE:          return 0;       // Edge
                case ConfigParam.J1962_PINS:         return 0x030B;  // Pin 6 & 14 (standard CAN)
                case ConfigParam.ISO15765_BS:        return 0;
                case ConfigParam.ISO15765_STMIN:     return 0;
                case ConfigParam.BS_TX:              return 0;
                case ConfigParam.STMIN_TX:           return 0;
                default:                             return 0;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // 8. PassThruStartMsgFilter
        // ─────────────────────────────────────────────────────────────────────
        [DllExport("PassThruStartMsgFilter", CallingConvention = CallingConvention.StdCall)]
        public static int PassThruStartMsgFilter(uint ChannelID, uint FilterType,
            IntPtr pMaskMsg, IntPtr pPatternMsg, IntPtr pFlowControlMsg, out uint pFilterID)
        {
            pFilterID = 1;
            _lastError.Value = "No error";

            try
            {
                if (pMaskMsg != IntPtr.Zero && pPatternMsg != IntPtr.Zero)
                {
                    PASSTHRU_MSG mask    = (PASSTHRU_MSG)Marshal.PtrToStructure(pMaskMsg,    typeof(PASSTHRU_MSG));
                    PASSTHRU_MSG pattern = (PASSTHRU_MSG)Marshal.PtrToStructure(pPatternMsg, typeof(PASSTHRU_MSG));

                    if (mask.DataSize >= 4 && pattern.DataSize >= 4)
                    {
                        uint maskId    = (uint)(mask.Data[0]    << 24 | mask.Data[1]    << 16 | mask.Data[2]    << 8 | mask.Data[3]);
                        uint patternId = (uint)(pattern.Data[0] << 24 | pattern.Data[1] << 16 | pattern.Data[2] << 8 | pattern.Data[3]);

                        Sniffa.LogTraffic("SYS_FILTER_SET", maskId, BitConverter.GetBytes(patternId));
                    }
                }
            }
            catch { }

            return J2534Err.STATUS_NOERROR;
        }

        // ─────────────────────────────────────────────────────────────────────
        // 9. PassThruStopMsgFilter
        // ─────────────────────────────────────────────────────────────────────
        [DllExport("PassThruStopMsgFilter", CallingConvention = CallingConvention.StdCall)]
        public static int PassThruStopMsgFilter(uint ChannelID, uint FilterID)
        {
            _lastError.Value = "No error";
            return J2534Err.STATUS_NOERROR;
        }

        // ─────────────────────────────────────────────────────────────────────
        // 10. PassThruReadVersion
        //     API version MUST be "04.04" — Techstream and ODIS verify this.
        // ─────────────────────────────────────────────────────────────────────
        [DllExport("PassThruReadVersion", CallingConvention = CallingConvention.StdCall)]
        public static int PassThruReadVersion(uint DeviceID, IntPtr pFirmwareVersion,
                                              IntPtr pDllVersion, IntPtr pApiVersion)
        {
            if (pFirmwareVersion == IntPtr.Zero ||
                pDllVersion      == IntPtr.Zero ||
                pApiVersion      == IntPtr.Zero)
            {
                _lastError.Value = "Null version pointer.";
                return J2534Err.STATUS_FAILED;
            }

            try
            {
                CopyString(pFirmwareVersion, "STM32F407_v1.0.0");
                CopyString(pDllVersion,      "TaskmanagerBridge_v1.0.0");
                CopyString(pApiVersion,      "04.04"); // Must match PassThruSupport.04.04 registry key

                _lastError.Value = "No error";
                return J2534Err.STATUS_NOERROR;
            }
            catch (Exception ex)
            {
                _lastError.Value = $"ReadVersion: {ex.Message}";
                return J2534Err.STATUS_FAILED;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // 11. PassThruGetLastError
        // ─────────────────────────────────────────────────────────────────────
        [DllExport("PassThruGetLastError", CallingConvention = CallingConvention.StdCall)]
        public static int PassThruGetLastError(IntPtr pErrorDescription)
        {
            if (pErrorDescription == IntPtr.Zero) return J2534Err.STATUS_FAILED;
            try
            {
                CopyString(pErrorDescription, _lastError.Value ?? "No error");
                return J2534Err.STATUS_NOERROR;
            }
            catch { return J2534Err.STATUS_FAILED; }
        }

        // ─────────────────────────────────────────────────────────────────────
        // 12. PassThruStartPeriodicMsg
        // ─────────────────────────────────────────────────────────────────────
        [DllExport("PassThruStartPeriodicMsg", CallingConvention = CallingConvention.StdCall)]
        public static int PassThruStartPeriodicMsg(uint ChannelID, IntPtr pMsg,
                                                   out uint pMsgID, uint TimeInterval)
        {
            pMsgID = 1;
            _lastError.Value = "No error";
            return J2534Err.STATUS_NOERROR;
        }

        // ─────────────────────────────────────────────────────────────────────
        // 13. PassThruStopPeriodicMsg
        // ─────────────────────────────────────────────────────────────────────
        [DllExport("PassThruStopPeriodicMsg", CallingConvention = CallingConvention.StdCall)]
        public static int PassThruStopPeriodicMsg(uint ChannelID, uint MsgID)
        {
            _lastError.Value = "No error";
            return J2534Err.STATUS_NOERROR;
        }

        // ─────────────────────────────────────────────────────────────────────
        // 14. PassThruSetProgrammingVoltage
        // ─────────────────────────────────────────────────────────────────────
        [DllExport("PassThruSetProgrammingVoltage", CallingConvention = CallingConvention.StdCall)]
        public static int PassThruSetProgrammingVoltage(uint DeviceID, uint PinNumber, uint Voltage)
        {
            _lastError.Value = "No error";
            Sniffa.LogTraffic("SYS_PROG_VOLT", Voltage, new byte[] { (byte)(PinNumber & 0xFF) });
            return J2534Err.STATUS_NOERROR;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Utility: copy a string into a caller-provided unmanaged buffer.
        // FIX B1: cap at 79 chars so that the null terminator always fits
        // within the 80-byte J2534 version string buffers.
        // ─────────────────────────────────────────────────────────────────────
        private static void CopyString(IntPtr dest, string text)
        {
            if (text.Length > 79) text = text.Substring(0, 79);
            byte[] bytes = Encoding.ASCII.GetBytes(text + "\0");
            Marshal.Copy(bytes, 0, dest, bytes.Length);
        }
    }
}
