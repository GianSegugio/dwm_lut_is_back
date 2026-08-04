using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace DwmLutGUI
{
    /// <summary>
    /// A display's current advanced-colour mode. Values match Windows'
    /// DISPLAYCONFIG_ADVANCED_COLOR_MODE.
    ///
    /// WCG matters here: Auto Color Management (Windows 11 24H2+) puts ordinary SDR displays into
    /// wide-gamut advanced colour. Those displays set the legacy advancedColorEnabled bit even
    /// though they have no HDR mode, which is why a non-HDR monitor could show up as "HDR".
    /// </summary>
    public enum AdvancedColorMode
    {
        Sdr = 0,
        Wcg = 1,
        Hdr = 2
    }

    // Queries Windows for each display's advanced-color (HDR) state, keyed by monitor device path.
    // Uses QueryDisplayConfig + DisplayConfigGetDeviceInfo directly, since WindowsDisplayAPI does not
    // expose advanced-color info. Fully defensive: any failure yields an empty map (treat as "not HDR").
    internal static class HdrInfo
    {
        private const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;
        private const int ERROR_SUCCESS = 0;
        private const uint DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;
        private const uint DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO = 9;

        // DISPLAYCONFIG_DEVICE_INFO_TYPE is a plain auto-incrementing enum after GET_SDR_WHITE_LEVEL = 11:
        // 12 GET_MONITOR_SPECIALIZATION, 13 SET_MONITOR_SPECIALIZATION, 14 SET_RESERVED1,
        // 15 GET_ADVANCED_COLOR_INFO_2, 16 SET_HDR_STATE, 17 SET_WCG_STATE.
        private const uint DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO_2 = 15;

        [StructLayout(LayoutKind.Sequential)]
        private struct LUID { public uint LowPart; public int HighPart; }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_SOURCE_INFO
        {
            public LUID adapterId;
            public uint id;
            public uint modeInfoIdx;
            public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_RATIONAL { public uint Numerator; public uint Denominator; }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_TARGET_INFO
        {
            public LUID adapterId;
            public uint id;
            public uint modeInfoIdx;
            public uint outputTechnology;
            public uint rotation;
            public uint scaling;
            public DISPLAYCONFIG_RATIONAL refreshRate;
            public uint scanLineOrdering;
            public int targetAvailable;
            public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_INFO
        {
            public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
            public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
            public uint flags;
        }

        // The mode-info union is not read here; Size pins the struct to its native 64-byte footprint.
        [StructLayout(LayoutKind.Sequential, Size = 64)]
        private struct DISPLAYCONFIG_MODE_INFO
        {
            public uint infoType;
            public uint id;
            public LUID adapterId;
            // union (48 bytes) omitted — padded to 64 by Size
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
        {
            public uint type;
            public uint size;
            public LUID adapterId;
            public uint id;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DISPLAYCONFIG_TARGET_DEVICE_NAME
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            public uint flags;
            public uint outputTechnology;
            public ushort edidManufactureId;
            public ushort edidProductCodeId;
            public uint connectorInstance;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string monitorFriendlyDeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string monitorDevicePath;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            public uint value;              // bit0 = advancedColorSupported, bit1 = advancedColorEnabled
            public uint colorEncoding;
            public uint bitsPerColorChannel;
        }

        [DllImport("user32.dll")]
        private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements,
            out uint numModeInfoArrayElements);

        [DllImport("user32.dll")]
        private static extern int QueryDisplayConfig(uint flags, ref uint numPathArrayElements,
            [Out] DISPLAYCONFIG_PATH_INFO[] pathArray, ref uint numModeInfoArrayElements,
            [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray, IntPtr currentTopologyId);

        [DllImport("user32.dll")]
        private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME requestPacket);

        /// <summary>
        /// Windows 11 24H2+ replacement for DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO. The legacy
        /// struct's advancedColorEnabled bit means "advanced color is active", which since Auto
        /// Color Management also covers SDR displays running in wide-gamut (WCG) mode - so it
        /// reports true for displays that have no HDR mode at all. This one carries an explicit
        /// activeColorMode that separates SDR / WCG / HDR.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            public uint value;                  // bitfield: supported / active / HDR- and WCG-enabled...
            public uint colorEncoding;
            public uint bitsPerColorChannel;
            public uint activeColorMode;        // 0 = SDR, 1 = WCG, 2 = HDR
        }

        [DllImport("user32.dll")]
        private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO requestPacket);

        [DllImport("user32.dll")]
        private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2 requestPacket);

        // device path (\\?\DISPLAY#...) -> the display's current advanced-colour mode.
        public static Dictionary<string, AdvancedColorMode> GetColorModes()
        {
            var map = new Dictionary<string, AdvancedColorMode>(StringComparer.OrdinalIgnoreCase);
            try
            {
                uint pathCount, modeCount;
                if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out pathCount, out modeCount) != ERROR_SUCCESS)
                    return map;

                var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
                var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
                if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != ERROR_SUCCESS)
                    return map;

                for (uint i = 0; i < pathCount; i++)
                {
                    var adapterId = paths[i].targetInfo.adapterId;
                    var targetId = paths[i].targetInfo.id;

                    var name = new DISPLAYCONFIG_TARGET_DEVICE_NAME();
                    name.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
                    name.header.size = (uint)Marshal.SizeOf(typeof(DISPLAYCONFIG_TARGET_DEVICE_NAME));
                    name.header.adapterId = adapterId;
                    name.header.id = targetId;
                    if (DisplayConfigGetDeviceInfo(ref name) != ERROR_SUCCESS)
                        continue;
                    if (string.IsNullOrEmpty(name.monitorDevicePath))
                        continue;

                    map[name.monitorDevicePath] = QueryColorMode(adapterId, targetId);
                }
            }
            catch
            {
                // Any P/Invoke or marshalling failure -> report nothing (callers treat as SDR).
            }
            return map;
        }

        /// <summary>
        /// Asks for the precise SDR / WCG / HDR mode, falling back to the legacy call on builds
        /// that predate it. Note the fallback cannot distinguish WCG from HDR - it only knows
        /// "advanced color is on" - so it reports HDR, matching the old behaviour.
        /// </summary>
        private static AdvancedColorMode QueryColorMode(LUID adapterId, uint targetId)
        {
            var info2 = new DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2();
            info2.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO_2;
            info2.header.size = (uint)Marshal.SizeOf(typeof(DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2));
            info2.header.adapterId = adapterId;
            info2.header.id = targetId;

            // On older builds the type is unknown and the call fails, so we simply fall through.
            // The API also validates header.size, so a struct mismatch degrades to the legacy path
            // rather than returning garbage.
            if (DisplayConfigGetDeviceInfo(ref info2) == ERROR_SUCCESS && info2.activeColorMode <= 2)
            {
                return (AdvancedColorMode)info2.activeColorMode;
            }

            var legacy = new DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO();
            legacy.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO;
            legacy.header.size = (uint)Marshal.SizeOf(typeof(DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO));
            legacy.header.adapterId = adapterId;
            legacy.header.id = targetId;

            if (DisplayConfigGetDeviceInfo(ref legacy) == ERROR_SUCCESS && (legacy.value & 0x2) != 0)
            {
                return AdvancedColorMode.Hdr;   // advancedColorEnabled; may actually be WCG
            }

            return AdvancedColorMode.Sdr;
        }
    }
}
