using System.Runtime.InteropServices;
using SharpGen.Runtime;

namespace NexScreen.Companion.Encoding;

/// <summary>Raw ICodecAPI access (not wrapped by Vortice). Only SetValue is used.</summary>
internal sealed unsafe class CodecApi : IDisposable
{
    private static readonly Guid IidICodecApi = new("901db4c7-31ce-41a2-85dc-8fa0bf41b8da");

    public static readonly Guid AVLowLatencyMode = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    public static readonly Guid AVEncCommonRateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    public static readonly Guid AVEncCommonMeanBitRate = new("f7222374-2144-4815-b550-a37f8e12ee52");
    public static readonly Guid AVEncCommonMaxBitRate = new("9651eae4-39b9-4ebf-85ef-d7f444ec7465");
    public static readonly Guid AVEncCommonQualityVsSpeed = new("98332df8-03cd-476b-89fa-3f9e442dec9f");
    public static readonly Guid AVEncMPVGOPSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");
    public static readonly Guid AVEncMPVDefaultBPictureCount = new("8d390aac-dc5c-4200-b57f-814d04babab2");
    public static readonly Guid AVEncVideoForceKeyFrame = new("398c1b98-8353-475a-9ef2-8f265d260345");

    public const uint RateControlCbr = 0;
    public const uint RateControlPeakConstrainedVbr = 1;

    private const ushort VtBool = 11;
    private const ushort VtUI4 = 19;

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct Variant
    {
        [FieldOffset(0)] public ushort Vt;
        [FieldOffset(8)] public uint UI4;
        [FieldOffset(8)] public short Bool;
    }

    private IntPtr _ptr;

    private CodecApi(IntPtr ptr) => _ptr = ptr;

    public static CodecApi? TryGet(ComObject transform)
    {
        var hr = transform.QueryInterface(IidICodecApi, out var ptr);
        return hr.Success && ptr != IntPtr.Zero ? new CodecApi(ptr) : null;
    }

    public int SetUInt32(Guid api, uint value)
    {
        var v = new Variant { Vt = VtUI4, UI4 = value };
        return SetValue(api, &v);
    }

    public int SetBool(Guid api, bool value)
    {
        var v = new Variant { Vt = VtBool, Bool = value ? (short)-1 : (short)0 };
        return SetValue(api, &v);
    }

    private int SetValue(Guid api, Variant* value)
    {
        if (_ptr == IntPtr.Zero)
        {
            throw new ObjectDisposedException(nameof(CodecApi));
        }

        // ICodecAPI vtable: IUnknown (0-2), IsSupported, IsModifiable, GetParameterRange, GetParameterValues,
        // GetDefaultValue, GetValue, SetValue (9).
        var vtbl = *(IntPtr**)_ptr;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, Variant*, int>)vtbl[9];
        return fn(_ptr, &api, value);
    }

    public void Dispose()
    {
        if (_ptr != IntPtr.Zero)
        {
            Marshal.Release(_ptr);
            _ptr = IntPtr.Zero;
        }
    }
}
