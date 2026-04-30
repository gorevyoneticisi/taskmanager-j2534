using System;
using Microsoft.Win32;

namespace TaskmanagerBridge
{
    /// <summary>
    /// Persists bridge settings to HKCU\Software\TaskmanagerBridge.
    /// All properties are read/written directly to the registry so they
    /// survive process restarts and are visible to all diagnostic apps.
    /// </summary>
    public static class BridgeConfig
    {
        private const string REG_KEY = @"Software\TaskmanagerBridge";

        // ── Defaults ─────────────────────────────────────────────────────────
        public const string DEFAULT_COM_PORT = "COM3";
        public const int DEFAULT_CAN_BAUD = 500000;
        public const bool DEFAULT_EXTENDED_ID = false;

        // ── Properties (lazy registry access) ────────────────────────────────

        public static string ComPort
        {
            get => Read("ComPort", DEFAULT_COM_PORT);
            set => Write("ComPort", value);
        }

        /// <summary>CAN bus speed in bits/sec (e.g. 500000, 250000, 1000000)</summary>
        public static int CanBaudRate
        {
            get => int.TryParse(Read("CanBaudRate", DEFAULT_CAN_BAUD.ToString()), out int v) ? v : DEFAULT_CAN_BAUD;
            set => Write("CanBaudRate", value.ToString());
        }

        /// <summary>True = 29-bit extended CAN IDs, False = 11-bit standard</summary>
        public static bool UseExtendedId
        {
            get => Read("UseExtendedId", "0") == "1";
            set => Write("UseExtendedId", value ? "1" : "0");
        }

        /// <summary>Skip the config dialog if settings are already saved</summary>
        public static bool RememberSettings
        {
            get => Read("RememberSettings", "0") == "1";
            set => Write("RememberSettings", value ? "1" : "0");
        }

        // ── Registry helpers ─────────────────────────────────────────────────

        private static string Read(string name, string defaultValue)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(REG_KEY))
                {
                    return key?.GetValue(name)?.ToString() ?? defaultValue;
                }
            }
            catch { return defaultValue; }
        }

        private static void Write(string name, string value)
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(REG_KEY))
                {
                    key?.SetValue(name, value);
                }
            }
            catch { /* non-fatal */ }
        }

        public static void ClearRemembered()
        {
            Write("RememberSettings", "0");
        }
    }
}