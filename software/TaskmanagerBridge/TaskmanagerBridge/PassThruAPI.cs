using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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

    [StructLayout(LayoutKind.Sequential)]
    public struct SCONFIG
    {
        public uint Parameter;
        public uint Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SCONFIG_LIST
    {
        public uint   NumOfParams;
        public IntPtr ConfigPtr;
    }

    // ── Protocol IDs ─────────────────────────────────────────────────────────
    internal static class ProtocolID
    {
        public const uint CAN      = 5;
        public const uint ISO15765 = 6;
    }

    // ── IoctlID: J2534-1 v04.04 correct values ───────────────────────────────
    internal static class IoctlID
    {
        public const uint GET_CONFIG                          = 0x01;
        public const uint SET_CONFIG                          = 0x02;
        public const uint READ_VBATT                          = 0x03;
        public const uint FIVE_BAUD_INIT                      = 0x04;
        public const uint FAST_INIT                           = 0x05;
        // 0x06 reserved
        public const uint CLEAR_TX_BUFFER                     = 0x07;
        public const uint CLEAR_RX_BUFFER                     = 0x08;
        public const uint CLEAR_PERIODIC_MSGS                 = 0x09;
        public const uint CLEAR_MSG_FILTERS                   = 0x0A;
        public const uint CLEAR_FUNCT_MSG_LOOKUP_TABLE        = 0x0B;
        public const uint ADD_TO_FUNCT_MSG_LOOKUP_TABLE       = 0x0C;
        public const uint DELETE_FROM_FUNCT_MSG_LOOKUP_TABLE  = 0x0D;
        public const uint READ_PROG_VOLTAGE                   = 0x0E;
        public const uint READ_VBATT_EXT                      = 0x10001; // DrewTech ext, used by Techstream and ODIS
    }

    // ── ConfigParam: J2534-1 v04.04 correct values ───────────────────────────
    internal static class ConfigParam
    {
        public const uint DATA_RATE              = 0x01;
        public const uint LOOPBACK               = 0x03;
        public const uint BIT_SAMPLE_POINT       = 0x17;  // was wrong: 0x04
        public const uint SYNCH_JUMPWIDTH        = 0x18;
        public const uint SYNC_MODE              = 0x05;
        public const uint J1962_PINS             = 0x19;  // was wrong: 0x09
        public const uint ISO15765_BS            = 0x1E;  // was wrong: 0x14
        public const uint ISO15765_STMIN         = 0x1F;  // was wrong: 0x15
        public const uint ISO15765_FRAME_PAD_VAL = 0x20;  // padding byte for padded frames
        public const uint ISO15765_ADDR_TYPE     = 0x21;  // 0=normal, 1=extended, 2=mixed
        public const uint BS_TX                  = 0x22;  // was wrong: 0x16
        public const uint STMIN_TX               = 0x23;  // was wrong: 0x17
        public const uint ISO15765_WFT_MAX       = 0x24;  // max FC_WAIT frames before abort
    }

    // ── J2534 error codes: v04.04 correct values ─────────────────────────────
    internal static class J2534Err
    {
        public const int STATUS_NOERROR          = 0x00;
        public const int ERR_NOT_SUPPORTED       = 0x01;
        public const int ERR_INVALID_CHANNEL_ID  = 0x02;
        public const int ERR_INVALID_PROTOCOL_ID = 0x03;
        public const int ERR_NULL_PARAMETER      = 0x04;
        public const int ERR_INVALID_FLAGS       = 0x06;
        public const int ERR_FAILED              = 0x07;  // was wrong: STATUS_FAILED=0xFF
        public const int ERR_DEVICE_NOT_CONNECTED= 0x08;
        public const int ERR_TIMEOUT             = 0x09;  // was wrong: 0x14
        public const int ERR_INVALID_MSG         = 0x0A;
        public const int ERR_INVALID_TIME_INTERVAL=0x0B;
        public const int ERR_EXCEEDED_LIMIT      = 0x0C;
        public const int ERR_INVALID_MSG_ID      = 0x0D;
        public const int ERR_BUFFER_EMPTY        = 0x10;
        public const int ERR_BUFFER_FULL         = 0x11;
        public const int ERR_BUFFER_OVERFLOW     = 0x12;
        public const int ERR_CHANNEL_IN_USE      = 0x14;
        public const int ERR_INVALID_FILTER_ID   = 0x16;
        public const int ERR_NO_FLOW_CONTROL     = 0x17;
    }

    // ── Filter types ─────────────────────────────────────────────────────────
    internal static class MsgFilterType
    {
        public const uint PASS_FILTER         = 0x01;
        public const uint BLOCK_FILTER        = 0x02;
        public const uint FLOW_CONTROL_FILTER = 0x03;
    }

    // ── Connect / Tx / Rx flag constants ─────────────────────────────────────
    internal static class ConnectFlags
    {
        public const uint CAN_29BIT_ID        = 0x100;
        public const uint ISO9141_NO_CHECKSUM = 0x200;
        public const uint CAN_ID_BOTH         = 0x800;
    }

    internal static class TxFlags
    {
        public const uint ISO15765_FRAME_PAD = 0x040;
        public const uint ISO15765_ADDR_TYPE = 0x080;
        public const uint CAN_29BIT_ID       = 0x100;
    }

    internal static class RxStatusFlags
    {
        public const uint TX_MSG_TYPE            = 0x0001;
        public const uint START_OF_MESSAGE       = 0x0002;
        public const uint ISO15765_PADDING_ERROR = 0x0010;
        public const uint CAN_29BIT_ID           = 0x0100;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Internal state objects
    // ─────────────────────────────────────────────────────────────────────────

    internal class MsgFilter
    {
        public uint   Id;
        public uint   FilterType;
        public uint   MaskId;
        public uint   PatternId;
        // FLOW_CONTROL_FILTER only: the FC frame to send when this filter triggers
        public uint   FcCanId;
        public byte[] FcPayload;   // bytes after the 4-byte CAN ID header
        public uint   FcDataSize;

        public bool Matches(uint canId) => (canId & MaskId) == (PatternId & MaskId);
    }

    internal class IsoTpRxSession
    {
        public bool   Active;
        public int    TotalLength;
        public int    Received;
        public byte[] Buffer;
        public byte   NextSN;   // expected consecutive-frame sequence number (1-based, wraps mod 16)
        public uint   CanId;    // source CAN ID of the ongoing transfer
    }

    internal class IsoTpTxSession
    {
        public readonly ManualResetEventSlim FcReady = new ManualResetEventSlim(false);
        public volatile bool FcReceived;
        public volatile byte FcStatus;
        public volatile byte FcBlockSize;
        public volatile byte FcStMin;
    }

    internal class PeriodicEntry
    {
        public System.Threading.Timer Timer;
        public uint   CanId;
        public byte[] Payload;
        public int    PayloadLen;
    }

    internal class ReassembledMsg
    {
        public uint   ProtocolId;
        public uint   RxStatus;
        public uint   CanId;
        public byte[] Data;
        public int    DataLen;
        public uint   Timestamp;
    }

    internal class ChannelState
    {
        public uint ProtocolId;
        public uint Flags;
        public uint BaudRate;

        // Per-channel receive queue, filled by FrameRouter and drained by ReadMsgs
        public readonly BlockingCollection<ReassembledMsg> RxQueue
            = new BlockingCollection<ReassembledMsg>(4096);

        // Filters, always accessed under lock(Filters)
        public readonly List<MsgFilter> Filters = new List<MsgFilter>();

        // Periodic messages, always accessed under lock(Periodic)
        public readonly Dictionary<uint, PeriodicEntry> Periodic
            = new Dictionary<uint, PeriodicEntry>();

        // ISO-TP sessions
        public readonly IsoTpRxSession IsoRx = new IsoTpRxSession();
        public readonly IsoTpTxSession IsoTx = new IsoTpTxSession();

        // ISO-TP tuning written by SET_CONFIG, read by router and transmitter
        public volatile uint Iso15765_BS          = 0;    // 0 = no block-size limit
        public volatile uint Iso15765_STMin        = 0;    // ms between sent FC
        public volatile uint Iso15765_BS_Tx        = 0;
        public volatile uint Iso15765_STMin_Tx     = 25;  // ms between sent CF
        public volatile uint Iso15765_FramePadVal  = 0xCC; // padding byte (0xCC = DrewTech default)
        public volatile uint Iso15765_WftMax       = 0;   // 0 = unlimited FC_WAIT frames accepted

        public bool Use29BitId => (Flags & ConnectFlags.CAN_29BIT_ID) != 0;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // J2534-1 v04.04 PassThru DLL: 14 mandatory exports
    // ISO15765-2 transport, multi-channel, filters, periodic messages
    // ═════════════════════════════════════════════════════════════════════════
    public class PassThruAPI
    {
        private static readonly ThreadLocal<string> _lastError
            = new ThreadLocal<string>(() => "No error");

        // ── Channel / ID tables ───────────────────────────────────────────────
        private static readonly Dictionary<uint, ChannelState> _channels
            = new Dictionary<uint, ChannelState>();
        private static readonly object _channelLock = new object();
        private static uint _nextChannelId = 1;
        private static uint _nextFilterId  = 1;
        private static uint _nextMsgId     = 1;

        // ── Frame router ──────────────────────────────────────────────────────
        private static Thread         _routerThread;
        private static volatile bool  _routerRunning;

        // ── ISO-TP PCI nibbles ────────────────────────────────────────────────
        private const byte ISO_SF  = 0x00;
        private const byte ISO_FF  = 0x10;
        private const byte ISO_CF  = 0x20;
        private const byte ISO_FC  = 0x30;
        private const byte FC_CTS  = 0x00;
        private const byte FC_WAIT = 0x01;
        private const byte FC_OVFL = 0x02;
        private const byte ISO_PAD = 0xCC;   // default padding byte (DrewTech compatible)

        // ─────────────────────────────────────────────────────────────────────
        // 1. PassThruOpen
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
                    if (!ShowConfigDialog())
                    {
                        _lastError.Value = "User cancelled configuration.";
                        return J2534Err.ERR_FAILED;
                    }
                }

                SerialBridge.Open(BridgeConfig.ComPort);

                _routerRunning = true;
                _routerThread  = new Thread(RouterWorker)
                    { IsBackground = true, Name = "J2534_FrameRouter" };
                _routerThread.Start();

                return J2534Err.STATUS_NOERROR;
            }
            catch (Exception ex)
            {
                _lastError.Value = $"PassThruOpen: {ex.Message}";
                BridgeConfig.ClearRemembered();
                return J2534Err.ERR_FAILED;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // 2. PassThruClose
        // ─────────────────────────────────────────────────────────────────────
        [DllExport("PassThruClose", CallingConvention = CallingConvention.StdCall)]
        public static int PassThruClose(uint DeviceID)
        {
            _lastError.Value = "No error";

            _routerRunning = false;
            _routerThread?.Join(600);
            _routerThread = null;

            lock (_channelLock)
            {
                foreach (var ch in _channels.Values)
                    DisposeChannel(ch);
                _channels.Clear();
            }

            SerialBridge.Close();
            return J2534Err.STATUS_NOERROR;
        }

        // ─────────────────────────────────────────────────────────────────────
        // 3. PassThruConnect
        // ─────────────────────────────────────────────────────────────────────
        [DllExport("PassThruConnect", CallingConvention = CallingConvention.StdCall)]
        public static int PassThruConnect(uint DeviceID, uint protocolId, uint Flags,
                                          uint BaudRate, out uint pChannelID)
        {
            pChannelID = 0;
            _lastError.Value = "No error";

            if (protocolId != ProtocolID.CAN && protocolId != ProtocolID.ISO15765)
            {
                _lastError.Value = $"Unsupported protocol 0x{protocolId:X}";
                return J2534Err.ERR_INVALID_PROTOCOL_ID;
            }
            if (!SerialBridge.IsOpen)
            {
                _lastError.Value = "Device not open.";
                return J2534Err.ERR_DEVICE_NOT_CONNECTED;
            }

            var ch = new ChannelState
            {
                ProtocolId = protocolId,
                Flags      = Flags,
                BaudRate   = BaudRate
            };

            lock (_channelLock)
            {
                pChannelID = _nextChannelId++;
                _channels[pChannelID] = ch;
            }

            Sniffa.LogTraffic("SYS_CONNECT", BaudRate,
                new byte[] { (byte)(protocolId & 0xFF), (byte)(Flags & 0xFF) });
            return J2534Err.STATUS_NOERROR;
        }

        // ─────────────────────────────────────────────────────────────────────
        // 4. PassThruDisconnect
        // ─────────────────────────────────────────────────────────────────────
        [DllExport("PassThruDisconnect", CallingConvention = CallingConvention.StdCall)]
        public static int PassThruDisconnect(uint ChannelID)
        {
            _lastError.Value = "No error";
            ChannelState ch;
            lock (_channelLock)
            {
                if (!_channels.TryGetValue(ChannelID, out ch))
                    return J2534Err.ERR_INVALID_CHANNEL_ID;
                _channels.Remove(ChannelID);
            }
            DisposeChannel(ch);
            return J2534Err.STATUS_NOERROR;
        }

        // ─────────────────────────────────────────────────────────────────────
        // 5. PassThruWriteMsgs
        // ─────────────────────────────────────────────────────────────────────
        [DllExport("PassThruWriteMsgs", CallingConvention = CallingConvention.StdCall)]
        public static int PassThruWriteMsgs(uint ChannelID, IntPtr pMsg,
                                            ref uint pNumMsgs, uint Timeout)
        {
            if (pMsg == IntPtr.Zero || pNumMsgs == 0) return J2534Err.ERR_NULL_PARAMETER;
            if (!SerialBridge.IsOpen) return J2534Err.ERR_DEVICE_NOT_CONNECTED;

            ChannelState ch;
            lock (_channelLock)
            {
                if (!_channels.TryGetValue(ChannelID, out ch))
                    return J2534Err.ERR_INVALID_CHANNEL_ID;
            }

            uint sent    = 0;
            int  structSz = Marshal.SizeOf(typeof(PASSTHRU_MSG));
            try
            {
                for (int i = 0; i < pNumMsgs; i++)
                {
                    var msg = (PASSTHRU_MSG)Marshal.PtrToStructure(
                        new IntPtr(pMsg.ToInt64() + i * structSz), typeof(PASSTHRU_MSG));

                    if (msg.DataSize < 4) { sent++; continue; }

                    uint canId = ReadCanId(msg.Data);

                    if (ch.ProtocolId == ProtocolID.ISO15765)
                    {
                        int sduLen = (int)msg.DataSize - 4;
                        // ISO 15765-2 s9.6.1: SF_DL must be 1-7. A 0-byte SDU would
                        // produce PCI byte 0x00 (reserved) which ECUs must not receive.
                        if (sduLen <= 0) { sent++; continue; }
                        byte[] sdu = new byte[sduLen];
                        Array.Copy(msg.Data, 4, sdu, 0, sduLen);
                        bool padded = (msg.TxFlags & TxFlags.ISO15765_FRAME_PAD) != 0;

                        int rc = SendIsoTp(ch, canId, sdu, padded, (int)Timeout);
                        if (rc != J2534Err.STATUS_NOERROR) { pNumMsgs = sent; return rc; }
                    }
                    else
                    {
                        int payloadLen = Math.Max(0, Math.Min((int)msg.DataSize - 4, 8));
                        byte[] payload  = new byte[payloadLen];
                        if (payloadLen > 0) Array.Copy(msg.Data, 4, payload, 0, payloadLen);
                        Sniffa.LogTraffic("TX", canId, payload);
                        SerialBridge.SendCanFrame(canId, payload, payloadLen);
                    }
                    sent++;
                }
                pNumMsgs = sent;
                return J2534Err.STATUS_NOERROR;
            }
            catch (Exception ex)
            {
                _lastError.Value = $"WriteMsgs: {ex.Message}";
                pNumMsgs = sent;
                return J2534Err.ERR_FAILED;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // 6. PassThruReadMsgs
        // ─────────────────────────────────────────────────────────────────────
        [DllExport("PassThruReadMsgs", CallingConvention = CallingConvention.StdCall)]
        public static int PassThruReadMsgs(uint ChannelID, IntPtr pMsg,
                                           ref uint pNumMsgs, uint Timeout)
        {
            if (pMsg == IntPtr.Zero || pNumMsgs == 0) return J2534Err.ERR_NULL_PARAMETER;

            ChannelState ch;
            lock (_channelLock)
            {
                if (!_channels.TryGetValue(ChannelID, out ch))
                    return J2534Err.ERR_INVALID_CHANNEL_ID;
            }

            int  structSz  = Marshal.SizeOf(typeof(PASSTHRU_MSG));
            uint delivered = 0;
            uint maxMsgs   = pNumMsgs;
            int  elapsed   = 0;
            // Use a 0ms poll when Timeout=0 so the call is truly non-blocking.
            // A 5ms poll is used otherwise to avoid burning a full CPU core.
            int  pollMs    = (Timeout == 0) ? 0 : 5;

            try
            {
                while (delivered < maxMsgs)
                {
                    ReassembledMsg rm;
                    if (ch.RxQueue.TryTake(out rm, pollMs))
                    {
                        var outMsg = new PASSTHRU_MSG
                        {
                            ProtocolID     = rm.ProtocolId,
                            RxStatus       = rm.RxStatus,
                            TxFlags        = 0,
                            Timestamp      = rm.Timestamp,
                            DataSize       = (uint)(4 + rm.DataLen),
                            ExtraDataIndex = (uint)(4 + rm.DataLen),
                            Data           = new byte[4128]
                        };
                        outMsg.Data[0] = (byte)((rm.CanId >> 24) & 0xFF);
                        outMsg.Data[1] = (byte)((rm.CanId >> 16) & 0xFF);
                        outMsg.Data[2] = (byte)((rm.CanId >>  8) & 0xFF);
                        outMsg.Data[3] = (byte)( rm.CanId        & 0xFF);
                        if (rm.DataLen > 0)
                            Array.Copy(rm.Data, 0, outMsg.Data, 4, rm.DataLen);

                        Marshal.StructureToPtr(outMsg,
                            new IntPtr(pMsg.ToInt64() + delivered * structSz), false);
                        delivered++;
                        elapsed = 0;
                    }
                    else
                    {
                        elapsed += pollMs;
                        if (elapsed >= (int)Timeout) break;
                        if (delivered > 0) break;
                    }
                }

                pNumMsgs = delivered;
                return delivered == 0 ? J2534Err.ERR_BUFFER_EMPTY : J2534Err.STATUS_NOERROR;
            }
            catch (Exception ex)
            {
                _lastError.Value = $"ReadMsgs: {ex.Message}";
                pNumMsgs = delivered;
                return J2534Err.ERR_FAILED;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // 7. PassThruIoctl
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
                    case IoctlID.CLEAR_RX_BUFFER:
                    case IoctlID.CLEAR_TX_BUFFER:
                    {
                        ChannelState ch;
                        lock (_channelLock) { _channels.TryGetValue(ChannelID, out ch); }
                        // Drain only this channel's queue; SerialBridge.RxQueue is shared
                        // across all open channels and must not be touched here.
                        if (ch != null) while (ch.RxQueue.TryTake(out _)) { }
                        Sniffa.LogTraffic("SYS_CLEAR_BUF", ioctlId, null);
                        return J2534Err.STATUS_NOERROR;
                    }

                    case IoctlID.CLEAR_PERIODIC_MSGS:
                    {
                        ChannelState ch;
                        lock (_channelLock) { _channels.TryGetValue(ChannelID, out ch); }
                        if (ch != null)
                            lock (ch.Periodic)
                            {
                                foreach (var e in ch.Periodic.Values) e.Timer?.Dispose();
                                ch.Periodic.Clear();
                            }
                        return J2534Err.STATUS_NOERROR;
                    }

                    case IoctlID.CLEAR_MSG_FILTERS:
                    {
                        ChannelState ch;
                        lock (_channelLock) { _channels.TryGetValue(ChannelID, out ch); }
                        if (ch != null) lock (ch.Filters) { ch.Filters.Clear(); }
                        return J2534Err.STATUS_NOERROR;
                    }

                    case IoctlID.CLEAR_FUNCT_MSG_LOOKUP_TABLE:
                    case IoctlID.ADD_TO_FUNCT_MSG_LOOKUP_TABLE:
                    case IoctlID.DELETE_FROM_FUNCT_MSG_LOOKUP_TABLE:
                        return J2534Err.STATUS_NOERROR;

                    case IoctlID.FIVE_BAUD_INIT:
                    case IoctlID.FAST_INIT:
                        // K-Line protocols are not supported by this CAN/ISO15765 DLL
                        return J2534Err.ERR_NOT_SUPPORTED;

                    case IoctlID.READ_VBATT:
                    case IoctlID.READ_VBATT_EXT:
                    case IoctlID.READ_PROG_VOLTAGE:
                        if (pOutput != IntPtr.Zero)
                            Marshal.WriteInt32(pOutput, 14200); // 14.2 V, healthy engine voltage
                        Sniffa.LogTraffic("SYS_VBATT", 14200, null);
                        return J2534Err.STATUS_NOERROR;

                    case IoctlID.GET_CONFIG:
                        return HandleGetConfig(ChannelID, pInput);

                    case IoctlID.SET_CONFIG:
                        return HandleSetConfig(ChannelID, pInput);

                    default:
                        Sniffa.LogTraffic("SYS_IOCTL_UNK", ioctlId, null);
                        return J2534Err.STATUS_NOERROR;
                }
            }
            catch (Exception ex)
            {
                _lastError.Value = $"Ioctl 0x{ioctlId:X}: {ex.Message}";
                return J2534Err.STATUS_NOERROR; // never kill the session on Ioctl failure
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // 8. PassThruStartMsgFilter
        // ─────────────────────────────────────────────────────────────────────
        [DllExport("PassThruStartMsgFilter", CallingConvention = CallingConvention.StdCall)]
        public static int PassThruStartMsgFilter(uint ChannelID, uint filterType,
            IntPtr pMaskMsg, IntPtr pPatternMsg, IntPtr pFlowControlMsg, out uint pFilterID)
        {
            pFilterID = 0;
            _lastError.Value = "No error";

            if (pMaskMsg == IntPtr.Zero || pPatternMsg == IntPtr.Zero)
                return J2534Err.ERR_NULL_PARAMETER;

            ChannelState ch;
            lock (_channelLock)
            {
                if (!_channels.TryGetValue(ChannelID, out ch))
                    return J2534Err.ERR_INVALID_CHANNEL_ID;
            }

            try
            {
                var mask    = (PASSTHRU_MSG)Marshal.PtrToStructure(pMaskMsg,    typeof(PASSTHRU_MSG));
                var pattern = (PASSTHRU_MSG)Marshal.PtrToStructure(pPatternMsg, typeof(PASSTHRU_MSG));

                if (mask.DataSize < 4 || pattern.DataSize < 4)
                    return J2534Err.ERR_INVALID_MSG;

                var f = new MsgFilter
                {
                    FilterType = filterType,
                    MaskId     = ReadCanId(mask.Data),
                    PatternId  = ReadCanId(pattern.Data)
                };

                if (filterType == MsgFilterType.FLOW_CONTROL_FILTER)
                {
                    if (pFlowControlMsg == IntPtr.Zero) return J2534Err.ERR_NULL_PARAMETER;
                    var fc = (PASSTHRU_MSG)Marshal.PtrToStructure(pFlowControlMsg, typeof(PASSTHRU_MSG));
                    if (fc.DataSize < 4) return J2534Err.ERR_INVALID_MSG;

                    f.FcCanId    = ReadCanId(fc.Data);
                    f.FcDataSize = fc.DataSize - 4;
                    f.FcPayload  = new byte[f.FcDataSize];
                    if (f.FcDataSize > 0)
                        Array.Copy(fc.Data, 4, f.FcPayload, 0, (int)f.FcDataSize);
                }

                lock (_channelLock) { f.Id = _nextFilterId++; }
                lock (ch.Filters)   { ch.Filters.Add(f); }
                pFilterID = f.Id;

                Sniffa.LogTraffic("SYS_FILTER_ADD", f.MaskId,
                    BitConverter.GetBytes(f.PatternId));
                return J2534Err.STATUS_NOERROR;
            }
            catch (Exception ex)
            {
                _lastError.Value = $"StartMsgFilter: {ex.Message}";
                return J2534Err.ERR_FAILED;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // 9. PassThruStopMsgFilter
        // ─────────────────────────────────────────────────────────────────────
        [DllExport("PassThruStopMsgFilter", CallingConvention = CallingConvention.StdCall)]
        public static int PassThruStopMsgFilter(uint ChannelID, uint FilterID)
        {
            _lastError.Value = "No error";
            ChannelState ch;
            lock (_channelLock)
            {
                if (!_channels.TryGetValue(ChannelID, out ch))
                    return J2534Err.ERR_INVALID_CHANNEL_ID;
            }
            lock (ch.Filters)
            {
                int idx = ch.Filters.FindIndex(f => f.Id == FilterID);
                if (idx < 0) return J2534Err.ERR_INVALID_FILTER_ID;
                ch.Filters.RemoveAt(idx);
            }
            return J2534Err.STATUS_NOERROR;
        }

        // ─────────────────────────────────────────────────────────────────────
        // 10. PassThruReadVersion
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
                return J2534Err.ERR_NULL_PARAMETER;
            }
            try
            {
                CopyString(pFirmwareVersion, "STM32F407_v2.0.0");
                CopyString(pDllVersion,      "TaskmanagerBridge_v2.0.0");
                CopyString(pApiVersion,      "04.04");
                return J2534Err.STATUS_NOERROR;
            }
            catch (Exception ex)
            {
                _lastError.Value = $"ReadVersion: {ex.Message}";
                return J2534Err.ERR_FAILED;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // 11. PassThruGetLastError
        // ─────────────────────────────────────────────────────────────────────
        [DllExport("PassThruGetLastError", CallingConvention = CallingConvention.StdCall)]
        public static int PassThruGetLastError(IntPtr pErrorDescription)
        {
            if (pErrorDescription == IntPtr.Zero) return J2534Err.ERR_NULL_PARAMETER;
            try
            {
                CopyString(pErrorDescription, _lastError.Value ?? "No error");
                return J2534Err.STATUS_NOERROR;
            }
            catch { return J2534Err.ERR_FAILED; }
        }

        // ─────────────────────────────────────────────────────────────────────
        // 12. PassThruStartPeriodicMsg
        // ─────────────────────────────────────────────────────────────────────
        [DllExport("PassThruStartPeriodicMsg", CallingConvention = CallingConvention.StdCall)]
        public static int PassThruStartPeriodicMsg(uint ChannelID, IntPtr pMsg,
                                                   out uint pMsgID, uint TimeInterval)
        {
            pMsgID = 0;
            _lastError.Value = "No error";

            if (pMsg == IntPtr.Zero) return J2534Err.ERR_NULL_PARAMETER;
            if (TimeInterval < 5 || TimeInterval > 65535)
                return J2534Err.ERR_INVALID_TIME_INTERVAL;

            ChannelState ch;
            lock (_channelLock)
            {
                if (!_channels.TryGetValue(ChannelID, out ch))
                    return J2534Err.ERR_INVALID_CHANNEL_ID;
            }

            lock (ch.Periodic)
            {
                if (ch.Periodic.Count >= 10) return J2534Err.ERR_EXCEEDED_LIMIT;
            }

            try
            {
                var msg = (PASSTHRU_MSG)Marshal.PtrToStructure(pMsg, typeof(PASSTHRU_MSG));
                if (msg.DataSize < 4) return J2534Err.ERR_INVALID_MSG;

                uint   canId      = ReadCanId(msg.Data);
                int    payloadLen = Math.Max(0, Math.Min((int)msg.DataSize - 4, 8));
                byte[] payload    = new byte[payloadLen];
                if (payloadLen > 0) Array.Copy(msg.Data, 4, payload, 0, payloadLen);

                uint msgId;
                lock (_channelLock) { msgId = _nextMsgId++; }

                var entry = new PeriodicEntry
                {
                    CanId      = canId,
                    Payload    = payload,
                    PayloadLen = payloadLen
                };
                entry.Timer = new System.Threading.Timer(state =>
                {
                    if (SerialBridge.IsOpen)
                    {
                        Sniffa.LogTraffic("TX_PERIODIC", entry.CanId, entry.Payload);
                        SerialBridge.SendCanFrame(entry.CanId, entry.Payload, entry.PayloadLen);
                    }
                }, null, (int)TimeInterval, (int)TimeInterval);

                lock (ch.Periodic) { ch.Periodic[msgId] = entry; }
                pMsgID = msgId;
                return J2534Err.STATUS_NOERROR;
            }
            catch (Exception ex)
            {
                _lastError.Value = $"StartPeriodicMsg: {ex.Message}";
                return J2534Err.ERR_FAILED;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // 13. PassThruStopPeriodicMsg
        // ─────────────────────────────────────────────────────────────────────
        [DllExport("PassThruStopPeriodicMsg", CallingConvention = CallingConvention.StdCall)]
        public static int PassThruStopPeriodicMsg(uint ChannelID, uint MsgID)
        {
            _lastError.Value = "No error";
            ChannelState ch;
            lock (_channelLock)
            {
                if (!_channels.TryGetValue(ChannelID, out ch))
                    return J2534Err.ERR_INVALID_CHANNEL_ID;
            }
            lock (ch.Periodic)
            {
                PeriodicEntry entry;
                if (!ch.Periodic.TryGetValue(MsgID, out entry))
                    return J2534Err.ERR_INVALID_MSG_ID;
                entry.Timer?.Dispose();
                ch.Periodic.Remove(MsgID);
            }
            return J2534Err.STATUS_NOERROR;
        }

        // ─────────────────────────────────────────────────────────────────────
        // 14. PassThruSetProgrammingVoltage
        // ─────────────────────────────────────────────────────────────────────
        [DllExport("PassThruSetProgrammingVoltage", CallingConvention = CallingConvention.StdCall)]
        public static int PassThruSetProgrammingVoltage(uint DeviceID, uint PinNumber, uint Voltage)
        {
            _lastError.Value = "No error";
            Sniffa.LogTraffic("SYS_PROG_VOLT", Voltage,
                new byte[] { (byte)(PinNumber & 0xFF) });
            return J2534Err.STATUS_NOERROR;
        }

        // ═════════════════════════════════════════════════════════════════════
        // Frame Router: single background thread that reads raw CanFrames from
        // SerialBridge and distributes them (with filter + ISO-TP processing)
        // into each open channel's private RxQueue.
        // ═════════════════════════════════════════════════════════════════════
        private static void RouterWorker()
        {
            while (_routerRunning)
            {
                CanFrame frame;
                if (!SerialBridge.RxQueue.TryTake(out frame, 50)) continue;

                List<ChannelState> snapshot;
                lock (_channelLock) { snapshot = new List<ChannelState>(_channels.Values); }

                foreach (var ch in snapshot)
                {
                    if (!PassesFilter(ch, frame.Id)) continue;

                    if (ch.ProtocolId == ProtocolID.ISO15765)
                        RouteIsoTpFrame(ch, frame);
                    else
                        ch.RxQueue.TryAdd(new ReassembledMsg
                        {
                            ProtocolId = ProtocolID.CAN,
                            RxStatus   = ch.Use29BitId ? RxStatusFlags.CAN_29BIT_ID : 0u,
                            CanId      = frame.Id,
                            Data       = frame.Data,
                            DataLen    = frame.Data.Length,
                            Timestamp  = frame.Timestamp
                        });
                }
            }
        }

        private static bool PassesFilter(ChannelState ch, uint canId)
        {
            List<MsgFilter> snap;
            lock (ch.Filters) { snap = new List<MsgFilter>(ch.Filters); }
            if (snap.Count == 0) return true;

            // Block filters take priority
            foreach (var f in snap)
                if (f.FilterType == MsgFilterType.BLOCK_FILTER && f.Matches(canId))
                    return false;

            // Pass/FC filters allow
            foreach (var f in snap)
                if ((f.FilterType == MsgFilterType.PASS_FILTER ||
                     f.FilterType == MsgFilterType.FLOW_CONTROL_FILTER)
                    && f.Matches(canId))
                    return true;

            return false;
        }

        private static void RouteIsoTpFrame(ChannelState ch, CanFrame frame)
        {
            if (frame.Data == null || frame.Data.Length == 0) return;

            byte pci     = frame.Data[0];
            byte pciType = (byte)(pci & 0xF0);

            // Flow Control: signal the waiting transmitter
            if (pciType == ISO_FC)
            {
                if (frame.Data.Length >= 3)
                {
                    ch.IsoTx.FcStatus    = (byte)(pci & 0x0F);
                    ch.IsoTx.FcBlockSize = frame.Data[1];
                    ch.IsoTx.FcStMin     = frame.Data[2];
                    ch.IsoTx.FcReceived  = true;
                    ch.IsoTx.FcReady.Set();
                }
                return;
            }

            lock (ch.IsoRx)
            {
                switch (pciType)
                {
                    case ISO_SF:
                    {
                        int sduLen = pci & 0x0F;
                        if (sduLen == 0 || sduLen > frame.Data.Length - 1) break;
                        byte[] sdu = new byte[sduLen];
                        Array.Copy(frame.Data, 1, sdu, 0, sduLen);
                        EnqueueIsoMsg(ch, frame.Id, sdu, sduLen, frame.Timestamp, 0);
                        ch.IsoRx.Active = false;
                        break;
                    }

                    case ISO_FF:
                    {
                        if (frame.Data.Length < 2) break;
                        int totalLen = ((pci & 0x0F) << 8) | frame.Data[1];
                        if (totalLen < 8) break;

                        ch.IsoRx.Active      = true;
                        ch.IsoRx.TotalLength = totalLen;
                        ch.IsoRx.Buffer      = new byte[totalLen];
                        ch.IsoRx.CanId       = frame.Id;
                        ch.IsoRx.NextSN      = 1;

                        int initBytes = Math.Min(frame.Data.Length - 2, totalLen);
                        Array.Copy(frame.Data, 2, ch.IsoRx.Buffer, 0, initBytes);
                        ch.IsoRx.Received = initBytes;

                        // Deliver start-of-message indication first
                        EnqueueIsoMsg(ch, frame.Id, new byte[0], 0,
                            frame.Timestamp, RxStatusFlags.START_OF_MESSAGE);

                        // Immediately send Flow Control (CTS) to the ECU
                        SendFlowControl(ch, frame.Id);
                        break;
                    }

                    case ISO_CF:
                    {
                        if (!ch.IsoRx.Active) break;
                        byte sn = (byte)(pci & 0x0F);
                        if (sn != (ch.IsoRx.NextSN & 0x0F))
                        {
                            ch.IsoRx.Active = false; // out-of-sequence CF, abort reassembly
                            break;
                        }

                        int rem      = ch.IsoRx.TotalLength - ch.IsoRx.Received;
                        int copyLen  = Math.Min(frame.Data.Length - 1, rem);
                        Array.Copy(frame.Data, 1, ch.IsoRx.Buffer, ch.IsoRx.Received, copyLen);
                        ch.IsoRx.Received += copyLen;
                        ch.IsoRx.NextSN++;

                        if (ch.IsoRx.Received >= ch.IsoRx.TotalLength)
                        {
                            EnqueueIsoMsg(ch, ch.IsoRx.CanId,
                                ch.IsoRx.Buffer, ch.IsoRx.TotalLength,
                                frame.Timestamp, 0);
                            ch.IsoRx.Active = false;
                        }
                        break;
                    }
                }
            }
        }

        private static void SendFlowControl(ChannelState ch, uint incomingCanId)
        {
            MsgFilter fc = null;
            lock (ch.Filters)
            {
                foreach (var f in ch.Filters)
                {
                    if (f.FilterType == MsgFilterType.FLOW_CONTROL_FILTER
                        && f.Matches(incomingCanId))
                    {
                        fc = f; break;
                    }
                }
            }
            if (fc == null) return;

            // Start with a standard CTS frame [0x30][BS][STmin][pad*5]
            byte padByte = (byte)(ch.Iso15765_FramePadVal & 0xFF);
            byte[] frame = new byte[8];
            frame[0] = (byte)(ISO_FC | FC_CTS);
            frame[1] = (byte)(ch.Iso15765_BS    & 0xFF);
            frame[2] = (byte)(ch.Iso15765_STMin & 0xFF);

            // Override with the app's FC payload if it provided specific bytes
            if (fc.FcPayload != null)
                for (int i = 0; i < Math.Min((int)fc.FcDataSize, 3); i++)
                    frame[i] = fc.FcPayload[i];

            for (int i = 3; i < 8; i++) frame[i] = padByte;

            Sniffa.LogTraffic("TX_ISO_FC", fc.FcCanId, frame);
            SerialBridge.SendCanFrame(fc.FcCanId, frame, 8);
        }

        private static void EnqueueIsoMsg(ChannelState ch, uint canId,
            byte[] data, int dataLen, uint ts, uint rxStatus)
        {
            ch.RxQueue.TryAdd(new ReassembledMsg
            {
                ProtocolId = ProtocolID.ISO15765,
                RxStatus   = rxStatus | (ch.Use29BitId ? RxStatusFlags.CAN_29BIT_ID : 0u),
                CanId      = canId,
                Data       = data,
                DataLen    = dataLen,
                Timestamp  = ts
            });
        }

        // ═════════════════════════════════════════════════════════════════════
        // ISO-TP transmit: handles segmentation and waits for flow control
        // ═════════════════════════════════════════════════════════════════════
        private static int SendIsoTp(ChannelState ch, uint canId,
            byte[] sdu, bool padded, int timeoutMs)
        {
            if (timeoutMs <= 0) timeoutMs = 1000;
            byte padByte = (byte)(ch.Iso15765_FramePadVal & 0xFF);

            if (sdu.Length <= 7)
            {
                // Single Frame
                byte[] sf = PaddedFrame(padded, padByte);
                sf[0] = (byte)(ISO_SF | sdu.Length);
                Array.Copy(sdu, 0, sf, 1, sdu.Length);
                Sniffa.LogTraffic("TX_ISO_SF", canId, sf);
                SerialBridge.SendCanFrame(canId, sf, padded ? 8 : 1 + sdu.Length);
            }
            else
            {
                // First Frame: reset FC event BEFORE sending to avoid race where
                // the ECU delivers FC before we call Reset().
                ch.IsoTx.FcReady.Reset();
                ch.IsoTx.FcReceived = false;

                byte[] ff = new byte[8];
                ff[0] = (byte)(ISO_FF | ((sdu.Length >> 8) & 0x0F));
                ff[1] = (byte)(sdu.Length & 0xFF);
                int initBytes = Math.Min(6, sdu.Length);
                Array.Copy(sdu, 0, ff, 2, initBytes);
                Sniffa.LogTraffic("TX_ISO_FF", canId, ff);
                SerialBridge.SendCanFrame(canId, ff, 8);

                // Wait for Flow Control (CTS). Handle FC_WAIT: the ECU may send one
                // or more FC(WAIT) frames before FC(CTS). Loop until CTS or timeout.
                int rc = WaitForFcCts(ch, timeoutMs);
                if (rc != J2534Err.STATUS_NOERROR) return rc;

                // Read FC parameters, then immediately arm the event for the next block
                // BEFORE sending any CFs, eliminating the race at block boundaries.
                int blockSize = ch.IsoTx.FcBlockSize;
                int stMinMs   = StMinToMs(ch.IsoTx.FcStMin);
                ch.IsoTx.FcReady.Reset();
                ch.IsoTx.FcReceived = false;

                // Send Consecutive Frames
                int  offset     = initBytes;
                byte sn         = 1;
                int  blockCount = 0;

                while (offset < sdu.Length)
                {
                    if (blockSize != 0 && blockCount >= blockSize)
                    {
                        // Block exhausted; wait for next FC(CTS). Event was already
                        // reset after the previous FC was processed (see above).
                        rc = WaitForFcCts(ch, timeoutMs);
                        if (rc != J2534Err.STATUS_NOERROR) return rc;
                        blockSize  = ch.IsoTx.FcBlockSize;
                        stMinMs    = StMinToMs(ch.IsoTx.FcStMin);
                        // Arm for the block after this one
                        ch.IsoTx.FcReady.Reset();
                        ch.IsoTx.FcReceived = false;
                        blockCount = 0;
                    }

                    byte[] cf     = PaddedFrame(padded, padByte);
                    cf[0]         = (byte)(ISO_CF | (sn & 0x0F));
                    int cfPayload = Math.Min(7, sdu.Length - offset);
                    Array.Copy(sdu, offset, cf, 1, cfPayload);
                    Sniffa.LogTraffic("TX_ISO_CF", canId, cf);
                    SerialBridge.SendCanFrame(canId, cf, padded ? 8 : 1 + cfPayload);

                    offset += cfPayload;
                    sn      = (byte)((sn + 1) & 0x0F);
                    blockCount++;

                    if (stMinMs > 0) Thread.Sleep(stMinMs);
                }
            }
            return J2534Err.STATUS_NOERROR;
        }

        // Waits for FC(CTS), transparently looping through FC(WAIT) frames.
        // Returns STATUS_NOERROR on CTS, ERR_NO_FLOW_CONTROL on timeout or WftMax exceeded,
        // ERR_BUFFER_OVERFLOW on FC(OVFL).
        private static int WaitForFcCts(ChannelState ch, int timeoutMs)
        {
            int remaining = timeoutMs;
            int wftCount  = 0;
            while (remaining > 0)
            {
                int start = Environment.TickCount;
                if (!ch.IsoTx.FcReady.Wait(remaining))
                {
                    _lastError.Value = "ISO-TP: flow control timeout.";
                    return J2534Err.ERR_NO_FLOW_CONTROL;
                }
                remaining -= (Environment.TickCount - start);

                byte status = ch.IsoTx.FcStatus;
                if (status == FC_CTS)  return J2534Err.STATUS_NOERROR;
                if (status == FC_OVFL) return J2534Err.ERR_BUFFER_OVERFLOW;

                // FC_WAIT: reset and wait again; per ISO 15765-2 the ECU will
                // send another FC before the N_Bs timeout expires.
                wftCount++;
                uint wftMax = ch.Iso15765_WftMax;
                if (wftMax != 0 && (uint)wftCount > wftMax)
                {
                    _lastError.Value = "ISO-TP: exceeded maximum FC_WAIT count.";
                    return J2534Err.ERR_NO_FLOW_CONTROL;
                }
                ch.IsoTx.FcReady.Reset();
                ch.IsoTx.FcReceived = false;
            }
            _lastError.Value = "ISO-TP: flow control timeout (FC_WAIT loop).";
            return J2534Err.ERR_NO_FLOW_CONTROL;
        }

        private static byte[] PaddedFrame(bool padded, byte padByte = ISO_PAD)
        {
            byte[] f = new byte[8];
            if (padded) for (int i = 0; i < 8; i++) f[i] = padByte;
            return f;
        }

        private static int StMinToMs(byte stMin)
        {
            if (stMin <= 0x7F) return stMin;
            if (stMin >= 0xF1 && stMin <= 0xF9) return 1; // 100-900 us, round to 1 ms
            return 0;
        }

        // ═════════════════════════════════════════════════════════════════════
        // GET_CONFIG / SET_CONFIG helpers
        // ═════════════════════════════════════════════════════════════════════
        private static int HandleGetConfig(uint channelId, IntPtr pInput)
        {
            if (pInput == IntPtr.Zero) return J2534Err.STATUS_NOERROR;
            var list = (SCONFIG_LIST)Marshal.PtrToStructure(pInput, typeof(SCONFIG_LIST));
            if (list.ConfigPtr == IntPtr.Zero) return J2534Err.STATUS_NOERROR;

            ChannelState ch;
            lock (_channelLock) { _channels.TryGetValue(channelId, out ch); }

            int sSize = Marshal.SizeOf(typeof(SCONFIG));
            for (int i = 0; i < list.NumOfParams; i++)
            {
                IntPtr ptr = new IntPtr(list.ConfigPtr.ToInt64() + i * sSize);
                var cfg    = (SCONFIG)Marshal.PtrToStructure(ptr, typeof(SCONFIG));
                cfg.Value  = DefaultConfigValue(ch, cfg.Parameter);
                Marshal.StructureToPtr(cfg, ptr, false);
            }
            return J2534Err.STATUS_NOERROR;
        }

        private static int HandleSetConfig(uint channelId, IntPtr pInput)
        {
            if (pInput == IntPtr.Zero) return J2534Err.STATUS_NOERROR;
            var list = (SCONFIG_LIST)Marshal.PtrToStructure(pInput, typeof(SCONFIG_LIST));
            if (list.ConfigPtr == IntPtr.Zero) return J2534Err.STATUS_NOERROR;

            ChannelState ch;
            lock (_channelLock) { _channels.TryGetValue(channelId, out ch); }

            int sSize = Marshal.SizeOf(typeof(SCONFIG));
            for (int i = 0; i < list.NumOfParams; i++)
            {
                IntPtr ptr = new IntPtr(list.ConfigPtr.ToInt64() + i * sSize);
                var cfg    = (SCONFIG)Marshal.PtrToStructure(ptr, typeof(SCONFIG));
                Sniffa.LogTraffic("SYS_SET_CFG", cfg.Parameter, BitConverter.GetBytes(cfg.Value));

                if (ch != null)
                {
                    switch (cfg.Parameter)
                    {
                        case ConfigParam.ISO15765_BS:            ch.Iso15765_BS           = cfg.Value; break;
                        case ConfigParam.ISO15765_STMIN:         ch.Iso15765_STMin         = cfg.Value; break;
                        case ConfigParam.BS_TX:                  ch.Iso15765_BS_Tx         = cfg.Value; break;
                        case ConfigParam.STMIN_TX:               ch.Iso15765_STMin_Tx      = cfg.Value; break;
                        case ConfigParam.ISO15765_FRAME_PAD_VAL: ch.Iso15765_FramePadVal   = cfg.Value & 0xFF; break;
                        case ConfigParam.ISO15765_WFT_MAX:       ch.Iso15765_WftMax        = cfg.Value; break;
                        // ISO15765_ADDR_TYPE: stored implicitly; extended addressing
                        // (type != 0) requires firmware changes not yet available.
                    }
                }
            }
            return J2534Err.STATUS_NOERROR;
        }

        private static uint DefaultConfigValue(ChannelState ch, uint param)
        {
            switch (param)
            {
                case ConfigParam.DATA_RATE:              return ch?.BaudRate ?? (uint)BridgeConfig.CanBaudRate;
                case ConfigParam.LOOPBACK:               return 0;
                case ConfigParam.BIT_SAMPLE_POINT:       return 80;
                case ConfigParam.SYNCH_JUMPWIDTH:        return 80;
                case ConfigParam.SYNC_MODE:              return 0;
                case ConfigParam.J1962_PINS:             return 0x030B; // Pin 6 = CAN_H, Pin 14 = CAN_L
                case ConfigParam.ISO15765_BS:            return ch?.Iso15765_BS           ?? 0;
                case ConfigParam.ISO15765_STMIN:         return ch?.Iso15765_STMin         ?? 0;
                case ConfigParam.ISO15765_FRAME_PAD_VAL: return ch?.Iso15765_FramePadVal   ?? 0xCC;
                case ConfigParam.ISO15765_ADDR_TYPE:     return 0;  // normal addressing only
                case ConfigParam.BS_TX:                  return ch?.Iso15765_BS_Tx         ?? 0;
                case ConfigParam.STMIN_TX:               return ch?.Iso15765_STMin_Tx      ?? 25;
                case ConfigParam.ISO15765_WFT_MAX:       return ch?.Iso15765_WftMax        ?? 0;
                default:                                 return 0;
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // Utilities
        // ═════════════════════════════════════════════════════════════════════
        private static uint ReadCanId(byte[] data)
            => (uint)(data[0] << 24 | data[1] << 16 | data[2] << 8 | data[3]);

        private static void DisposeChannel(ChannelState ch)
        {
            lock (ch.Periodic)
            {
                foreach (var e in ch.Periodic.Values) e.Timer?.Dispose();
                ch.Periodic.Clear();
            }
            while (ch.RxQueue.TryTake(out _)) { }
        }

        private static void CopyString(IntPtr dest, string text)
        {
            if (text.Length > 79) text = text.Substring(0, 79);
            byte[] bytes = Encoding.ASCII.GetBytes(text + "\0");
            Marshal.Copy(bytes, 0, dest, bytes.Length);
        }

        private static bool ShowConfigDialog()
        {
            bool ok = false;
            var t = new Thread(() =>
            {
                Application.EnableVisualStyles();
                using (var dlg = new ConfigDialog())
                    ok = dlg.ShowDialog() == DialogResult.OK;
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
            t.Join();
            return ok;
        }
    }
}
