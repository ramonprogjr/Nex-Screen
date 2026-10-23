namespace NexScreen.Companion.Util;

internal static class HResults
{
    public const int DxgiErrorAccessLost = unchecked((int)0x887A0026);
    public const int DxgiErrorWaitTimeout = unchecked((int)0x887A0027);
    public const int DxgiErrorInvalidCall = unchecked((int)0x887A0001);
    public const int DxgiErrorUnsupported = unchecked((int)0x887A0004);
    public const int DxgiErrorDeviceRemoved = unchecked((int)0x887A0005);
    public const int DxgiErrorDeviceReset = unchecked((int)0x887A0007);
    public const int DxgiErrorNotCurrentlyAvailable = unchecked((int)0x887A0022);
    public const int DxgiErrorSessionDisconnected = unchecked((int)0x887A0028);
    public const int EAccessDenied = unchecked((int)0x80070005);

    public const int MfETransformNeedMoreInput = unchecked((int)0xC00D6D72);
    public const int MfETransformStreamChange = unchecked((int)0xC00D6D61);
    public const int MfENotAccepting = unchecked((int)0xC00D36B5);
    public const int MfEShutdown = unchecked((int)0xC00D3E85);
    public const int MfENoEventsAvailable = unchecked((int)0xC00D3E80);

    public static string Format(int hr) => $"0x{hr:X8}";
}
