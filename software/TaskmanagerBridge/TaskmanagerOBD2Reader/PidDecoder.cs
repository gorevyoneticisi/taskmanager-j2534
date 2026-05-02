using System;
using System.Collections.Generic;

namespace TaskmanagerOBD2Reader
{
    internal class PidInfo
    {
        public byte   Pid    { get; }
        public string Name   { get; }
        public string Unit   { get; }
        public Func<byte, byte, double> Decode { get; }
        public Func<double, string>     Format { get; }

        public PidInfo(byte pid, string name, string unit,
            Func<byte, byte, double> decode,
            Func<double, string> format = null)
        {
            Pid    = pid;
            Name   = name;
            Unit   = unit;
            Decode = decode;
            Format = format ?? (v => v.ToString("F1"));
        }
    }

    internal static class PidDecoder
    {
        public static readonly PidInfo[] Pids =
        {
            new PidInfo(0x04, "Engine Load",      "%",     (a, b) => a / 2.55),
            new PidInfo(0x05, "Coolant Temp",      "degC",  (a, b) => a - 40.0),
            new PidInfo(0x0B, "Intake Pressure",   "kPa",   (a, b) => a),
            new PidInfo(0x0C, "Engine RPM",        "RPM",   (a, b) => ((a * 256.0 + b) / 4.0), v => v.ToString("F0")),
            new PidInfo(0x0D, "Vehicle Speed",     "km/h",  (a, b) => a,                        v => v.ToString("F0")),
            new PidInfo(0x0E, "Timing Advance",    "deg",   (a, b) => a / 2.0 - 64.0),
            new PidInfo(0x0F, "Intake Air Temp",   "degC",  (a, b) => a - 40.0),
            new PidInfo(0x10, "MAF Air Flow",      "g/s",   (a, b) => (a * 256.0 + b) / 100.0),
            new PidInfo(0x11, "Throttle Position", "%",     (a, b) => a / 2.55),
            new PidInfo(0x2F, "Fuel Level",        "%",     (a, b) => a / 2.55),
            new PidInfo(0x46, "Ambient Temp",      "degC",  (a, b) => a - 40.0),
            new PidInfo(0x5C, "Oil Temperature",   "degC",  (a, b) => a - 40.0),
        };

        // Decode OBD2 Mode 03 DTC bytes into "P0123" style strings.
        public static List<string> DecodeDtcs(byte[] data, int offset, int count)
        {
            var list = new List<string>();
            for (int i = offset; i + 1 < offset + count && i + 1 < data.Length; i += 2)
            {
                byte hi = data[i];
                byte lo = data[i + 1];
                if (hi == 0 && lo == 0) continue;

                char type = ((hi >> 6) & 0x03) switch
                {
                    0 => 'P',
                    1 => 'C',
                    2 => 'B',
                    _ => 'U'
                };
                int num = ((hi & 0x3F) << 8) | lo;
                list.Add($"{type}{num:X04}");
            }
            return list;
        }

        private static readonly Dictionary<string, string> _dtcDesc = new Dictionary<string, string>
        {
            { "P0100", "Mass Air Flow Circuit Malfunction" },
            { "P0101", "MAF Circuit Range/Performance" },
            { "P0110", "Intake Air Temperature Circuit Malfunction" },
            { "P0115", "Engine Coolant Temperature Circuit Malfunction" },
            { "P0120", "Throttle Position Sensor Circuit Malfunction" },
            { "P0130", "O2 Sensor Circuit Malfunction (Bank 1, Sensor 1)" },
            { "P0171", "System Too Lean (Bank 1)" },
            { "P0172", "System Too Rich (Bank 1)" },
            { "P0300", "Random/Multiple Cylinder Misfire Detected" },
            { "P0301", "Cylinder 1 Misfire Detected" },
            { "P0302", "Cylinder 2 Misfire Detected" },
            { "P0303", "Cylinder 3 Misfire Detected" },
            { "P0304", "Cylinder 4 Misfire Detected" },
            { "P0335", "Crankshaft Position Sensor Circuit Malfunction" },
            { "P0340", "Camshaft Position Sensor Circuit Malfunction" },
            { "P0400", "Exhaust Gas Recirculation Flow Malfunction" },
            { "P0420", "Catalyst System Efficiency Below Threshold (Bank 1)" },
            { "P0442", "Evaporative Emission Control System Leak Detected" },
            { "P0500", "Vehicle Speed Sensor Malfunction" },
            { "P0505", "Idle Control System Malfunction" },
            { "P0600", "Serial Communication Link Malfunction" },
            { "P0700", "Transmission Control System Malfunction" },
        };

        public static string DescribeDtc(string code)
        {
            return _dtcDesc.TryGetValue(code, out string desc) ? desc : "Unknown fault code";
        }
    }
}
