using System.Text.RegularExpressions;

namespace NexScreen.Companion.Adb;

public enum AdbDeviceState { Device, Unauthorized, Offline, NoPermissions, Other }

public enum AdbConnection { None, Usb, Wifi }

/// <summary>One line of `adb devices -l`. TransportId changes when the device reconnects.</summary>
public sealed record AdbDevice(string Serial, AdbDeviceState State, string RawState, string? TransportId)
{
    public bool IsReady => State == AdbDeviceState.Device;
}

public static partial class AdbOutput
{
    /// <summary>
    /// Parses `adb devices` / `adb devices -l`. Skips the header, daemon start-up lines ("* daemon ...") and
    /// anything that is not "serial state [...]".
    /// </summary>
    public static List<AdbDevice> ParseDevices(string output)
    {
        var result = new List<AdbDevice>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('*') || line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("adb ", StringComparison.OrdinalIgnoreCase) || line.StartsWith("error", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var split = line.IndexOfAny([' ', '\t']);
            if (split <= 0)
            {
                continue;
            }

            var serial = line[..split];
            var rest = line[split..].TrimStart();
            if (rest.Length == 0)
            {
                continue;
            }

            AdbDeviceState state;
            string rawState;
            if (rest.StartsWith("no permissions", StringComparison.OrdinalIgnoreCase))
            {
                state = AdbDeviceState.NoPermissions;
                rawState = "no permissions";
            }
            else
            {
                var end = rest.IndexOfAny([' ', '\t']);
                rawState = end < 0 ? rest : rest[..end];
                state = rawState switch
                {
                    "device" => AdbDeviceState.Device,
                    "unauthorized" => AdbDeviceState.Unauthorized,
                    "offline" => AdbDeviceState.Offline,
                    _ => AdbDeviceState.Other,
                };
            }

            var tid = TransportIdRegex().Match(rest);
            result.Add(new AdbDevice(serial, state, rawState, tid.Success ? tid.Groups[1].Value : null));
        }

        return result;
    }

    /// <summary>True when `adb reverse --list` shows tcp:port forwarded to tcp:port.</summary>
    public static bool HasReverse(string reverseListOutput, int port)
    {
        var wanted = $"tcp:{port} tcp:{port}";
        return reverseListOutput.Split('\n').Any(l => l.TrimEnd().EndsWith(wanted, StringComparison.Ordinal));
    }

    /// <summary>`am start` prints "Error: ..." (exit code 0) when the activity does not exist.</summary>
    public static bool AmStartFailed(string output) =>
        output.Contains("Error:", StringComparison.Ordinal) || output.Contains("Error type", StringComparison.Ordinal);

    /// <summary>Serial for logs: only the last 4 characters.</summary>
    public static string MaskSerial(string serial) => serial.Length <= 4 ? serial : "..." + serial[^4..];

    public const string TlsConnectService = "_adb-tls-connect._tcp";

    /// <summary>Wireless debugging serials: "ip:port", "[v6]:port" or an mDNS name ("adb-XXXX._adb-tls-connect._tcp").</summary>
    public static AdbConnection ConnectionOf(string serial) =>
        IsMdnsSerial(serial) || NetworkSerialRegex().IsMatch(serial) ? AdbConnection.Wifi : AdbConnection.Usb;

    public static bool IsMdnsSerial(string serial) =>
        serial.Contains("._adb-tls-connect._tcp", StringComparison.OrdinalIgnoreCase) ||
        serial.Contains("._adb._tcp", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Parses `adb mdns services` ("name\ttype\taddress" lines). Only "_adb-tls-connect._tcp" services are returned:
    /// those accept connections from already-paired PCs only. Pairing services are never returned.
    /// </summary>
    public static List<string> ParseTlsConnectServices(string output) => ParseMdnsServices(output, TlsConnectService);

    public const string TlsPairingService = "_adb-tls-pairing._tcp";

    /// <summary>
    /// "_adb-tls-pairing._tcp" services from `adb mdns services`: announced only while the tablet shows "Pair device
    /// with pairing code". Used by the tray pairing dialog, never by the automatic link.
    /// </summary>
    public static List<string> ParseTlsPairingServices(string output) => ParseMdnsServices(output, TlsPairingService);

    private static List<string> ParseMdnsServices(string output, string serviceType)
    {
        var result = new List<string>();
        foreach (var raw in output.Split('\n'))
        {
            var parts = raw.Trim().Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3 || !parts[1].TrimEnd('.').Equals(serviceType, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var address = parts[^1];
            if (IsNetworkAddress(address) && !result.Contains(address))
            {
                result.Add(address);
            }
        }

        return result;
    }

    /// <summary>"ip:port" or "[v6]:port" with a port in 1-65535.</summary>
    public static bool IsNetworkAddress(string address) =>
        NetworkSerialRegex().IsMatch(address) &&
        int.TryParse(address.AsSpan(address.LastIndexOf(':') + 1), System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var port) && port is > 0 and <= 65535;

    /// <summary>`adb pair` prints "Successfully paired to ip:port [guid=...]" on success; its exit code is not reliable.</summary>
    public static bool PairSucceeded(string output) => output.Contains("Successfully paired", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// One transport per physical device (same ro.serialno): USB first, then ip:port, then the mDNS name. Linking
    /// two transports of one tablet would make the second adb reverse fight the first.
    /// </summary>
    public static List<AdbDevice> ChoosePerPhysicalDevice(IEnumerable<(AdbDevice Device, string PhysicalId)> ready) =>
        ready.GroupBy(r => r.PhysicalId, StringComparer.Ordinal)
            .Select(g => g.OrderBy(r => Preference(r.Device.Serial)).ThenBy(r => r.Device.Serial, StringComparer.Ordinal).First().Device)
            .ToList();

    private static int Preference(string serial) =>
        ConnectionOf(serial) == AdbConnection.Usb ? 0 : IsMdnsSerial(serial) ? 2 : 1;

    [GeneratedRegex(@"\btransport_id:(\d+)")]
    private static partial Regex TransportIdRegex();

    [GeneratedRegex(@"^(\d{1,3}(\.\d{1,3}){3}|\[[0-9A-Fa-f:.%a-zA-Z0-9]+\]):\d{1,5}$")]
    private static partial Regex NetworkSerialRegex();
}
