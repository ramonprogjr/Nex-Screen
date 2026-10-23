using System.Runtime.InteropServices;

namespace NexScreen.Companion.Displays;

/// <summary>Minimal QueryDisplayConfig / DisplayConfigGetDeviceInfo interop.</summary>
internal static class DisplayConfigInterop
{
    private const uint QdcOnlyActivePaths = 0x2;
    private const int ErrorSuccess = 0;
    private const int ErrorInsufficientBuffer = 122;

    private const uint DeviceInfoGetSourceName = 1;
    private const uint DeviceInfoGetTargetName = 2;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Luid
    {
        public uint LowPart;
        public int HighPart;

        public readonly long Value => ((long)HighPart << 32) | LowPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rational
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PathSourceInfo
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIdx;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PathTargetInfo
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIdx;
        public uint OutputTechnology;
        public uint Rotation;
        public uint Scaling;
        public Rational RefreshRate;
        public uint ScanLineOrdering;
        public int TargetAvailable;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PathInfo
    {
        public PathSourceInfo SourceInfo;
        public PathTargetInfo TargetInfo;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct ModeInfo
    {
        public uint InfoType;
        public uint Id;
        public Luid AdapterId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfoHeader
    {
        public uint Type;
        public uint Size;
        public Luid AdapterId;
        public uint Id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SourceDeviceName
    {
        public DeviceInfoHeader Header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string ViewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TargetDeviceName
    {
        public DeviceInfoHeader Header;
        public uint Flags;
        public uint OutputTechnology;
        public ushort EdidManufactureId;
        public ushort EdidProductCodeId;
        public uint ConnectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string MonitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string MonitorDevicePath;
    }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(
        uint flags,
        ref uint numPathArrayElements,
        [Out] PathInfo[] pathArray,
        ref uint numModeInfoArrayElements,
        [Out] ModeInfo[] modeInfoArray,
        IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref SourceDeviceName requestPacket);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref TargetDeviceName requestPacket);

    internal sealed record TargetInfo(
        string GdiDeviceName,
        string FriendlyName,
        string? EdidManufacturer,
        double RefreshHz,
        long AdapterLuid,
        uint OutputTechnology);

    /// <summary>Active display paths, keyed by source GDI name (\\.\DISPLAYn).</summary>
    internal static List<TargetInfo> QueryActiveTargets()
    {
        PathInfo[] paths;
        ModeInfo[] modes;
        int rc;
        var attempts = 0;
        do
        {
            rc = GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out var numPaths, out var numModes);
            if (rc != ErrorSuccess)
            {
                throw new InvalidOperationException($"GetDisplayConfigBufferSizes failed: {rc}");
            }

            paths = new PathInfo[numPaths];
            modes = new ModeInfo[numModes];
            rc = QueryDisplayConfig(QdcOnlyActivePaths, ref numPaths, paths, ref numModes, modes, IntPtr.Zero);
            if (rc == ErrorSuccess)
            {
                Array.Resize(ref paths, (int)numPaths);
            }
        }
        while (rc == ErrorInsufficientBuffer && ++attempts < 5);

        if (rc != ErrorSuccess)
        {
            throw new InvalidOperationException($"QueryDisplayConfig failed: {rc}");
        }

        var result = new List<TargetInfo>();
        foreach (var path in paths)
        {
            var source = new SourceDeviceName
            {
                Header = new DeviceInfoHeader
                {
                    Type = DeviceInfoGetSourceName,
                    Size = (uint)Marshal.SizeOf<SourceDeviceName>(),
                    AdapterId = path.SourceInfo.AdapterId,
                    Id = path.SourceInfo.Id,
                },
            };
            if (DisplayConfigGetDeviceInfo(ref source) != ErrorSuccess)
            {
                continue;
            }

            var target = new TargetDeviceName
            {
                Header = new DeviceInfoHeader
                {
                    Type = DeviceInfoGetTargetName,
                    Size = (uint)Marshal.SizeOf<TargetDeviceName>(),
                    AdapterId = path.TargetInfo.AdapterId,
                    Id = path.TargetInfo.Id,
                },
            };
            var friendly = string.Empty;
            string? manufacturer = null;
            if (DisplayConfigGetDeviceInfo(ref target) == ErrorSuccess)
            {
                friendly = target.MonitorFriendlyDeviceName ?? string.Empty;
                const uint EdidIdsValid = 0x4;
                if ((target.Flags & EdidIdsValid) != 0)
                {
                    manufacturer = DecodePnpId(target.EdidManufactureId);
                }
            }

            var rr = path.TargetInfo.RefreshRate;
            var hz = rr.Denominator == 0 ? 0 : rr.Numerator / (double)rr.Denominator;
            result.Add(new TargetInfo(source.ViewGdiDeviceName, friendly, manufacturer, hz,
                path.SourceInfo.AdapterId.Value, path.TargetInfo.OutputTechnology));
        }

        return result;
    }

    /// <summary>Decodes the EDID 3-letter PNP manufacturer id (stored big-endian in the EDID, read little-endian here).</summary>
    internal static string DecodePnpId(ushort raw)
    {
        var v = (ushort)((raw >> 8) | (raw << 8));
        Span<char> c = stackalloc char[3];
        c[0] = (char)('@' + ((v >> 10) & 0x1F));
        c[1] = (char)('@' + ((v >> 5) & 0x1F));
        c[2] = (char)('@' + (v & 0x1F));
        return new string(c);
    }
}
