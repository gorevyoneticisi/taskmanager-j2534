using System;
using System.Runtime.InteropServices;

namespace TaskmanagerOBD2Reader
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct PASSTHRU_MSG
    {
        public uint ProtocolID;
        public uint RxStatus;
        public uint TxFlags;
        public uint Timestamp;
        public uint DataSize;
        public uint ExtraDataIndex;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4128)]
        public byte[] Data;

        public static PASSTHRU_MSG Create(uint protocolId, uint txFlags, byte[] payload)
        {
            var msg = new PASSTHRU_MSG
            {
                ProtocolID    = protocolId,
                TxFlags       = txFlags,
                DataSize      = (uint)(4 + payload.Length),
                ExtraDataIndex = (uint)(4 + payload.Length),
                Data          = new byte[4128]
            };
            // Bytes 0-3: CAN ID (big-endian), broadcast 0x7DF
            msg.Data[0] = 0x00;
            msg.Data[1] = 0x00;
            msg.Data[2] = 0x07;
            msg.Data[3] = 0xDF;
            Array.Copy(payload, 0, msg.Data, 4, payload.Length);
            return msg;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SCONFIG
    {
        public uint Parameter;
        public uint Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SCONFIG_LIST
    {
        public uint NumOfParams;
        public IntPtr ConfigPtr;
    }

    internal static class J2534Err
    {
        public const int STATUS_NOERROR         = 0x00;
        public const int ERR_BUFFER_EMPTY       = 0x10;
        public const int ERR_TIMEOUT            = 0x0F;
    }

    internal static class Protocol
    {
        public const uint ISO15765 = 6;
    }

    internal static class TxFlags
    {
        public const uint ISO15765_FRAME_PAD = 0x40;
    }

    internal static class FilterType
    {
        public const uint FLOW_CONTROL_FILTER = 3;
    }

    internal static class ConfigParam
    {
        public const uint LOOPBACK             = 0x03;
        public const uint ISO15765_BS          = 0x16;
        public const uint ISO15765_STMIN       = 0x17;
        public const uint ISO15765_FRAME_PAD_VAL = 0x20;
        public const uint DATA_RATE            = 0x01;
    }

    internal static class J2534Api
    {
        private const string DLL = "TaskmanagerBridge.dll";

        [DllImport(DLL, EntryPoint = "PassThruOpen", CallingConvention = CallingConvention.StdCall)]
        public static extern int Open(IntPtr reserved, out uint deviceId);

        [DllImport(DLL, EntryPoint = "PassThruClose", CallingConvention = CallingConvention.StdCall)]
        public static extern int Close(uint deviceId);

        [DllImport(DLL, EntryPoint = "PassThruConnect", CallingConvention = CallingConvention.StdCall)]
        public static extern int Connect(uint deviceId, uint protocolId, uint flags, uint baudRate, out uint channelId);

        [DllImport(DLL, EntryPoint = "PassThruDisconnect", CallingConvention = CallingConvention.StdCall)]
        public static extern int Disconnect(uint channelId);

        [DllImport(DLL, EntryPoint = "PassThruReadMsgs", CallingConvention = CallingConvention.StdCall)]
        public static extern int ReadMsgs(uint channelId, IntPtr msgs, ref uint numMsgs, uint timeout);

        [DllImport(DLL, EntryPoint = "PassThruWriteMsgs", CallingConvention = CallingConvention.StdCall)]
        public static extern int WriteMsgs(uint channelId, IntPtr msgs, ref uint numMsgs, uint timeout);

        [DllImport(DLL, EntryPoint = "PassThruStartMsgFilter", CallingConvention = CallingConvention.StdCall)]
        public static extern int StartMsgFilter(uint channelId, uint filterType,
            IntPtr maskMsg, IntPtr patternMsg, IntPtr flowControlMsg, out uint filterId);

        [DllImport(DLL, EntryPoint = "PassThruStopMsgFilter", CallingConvention = CallingConvention.StdCall)]
        public static extern int StopMsgFilter(uint channelId, uint filterId);

        [DllImport(DLL, EntryPoint = "PassThruIoctl", CallingConvention = CallingConvention.StdCall)]
        public static extern int Ioctl(uint channelId, uint ioctlId, IntPtr input, IntPtr output);

        [DllImport(DLL, EntryPoint = "PassThruGetLastError", CallingConvention = CallingConvention.StdCall)]
        public static extern int GetLastError([MarshalAs(UnmanagedType.LPStr)] System.Text.StringBuilder errorDescription);

        // IOCTL IDs
        public const uint CLEAR_RX_BUFFER  = 0x0A;
        public const uint CLEAR_TX_BUFFER  = 0x0B;
        public const uint SET_CONFIG       = 0x01;

        public static string GetError()
        {
            var sb = new System.Text.StringBuilder(256);
            GetLastError(sb);
            return sb.ToString();
        }
    }
}
