using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;

namespace NexScreen.Companion.Capture;

/// <summary>D3D11 device on the adapter that owns the captured output, shared with Media Foundation.</summary>
public sealed class GpuDevice : IDisposable
{
    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }
    public IDXGIAdapter1 Adapter { get; }
    public IMFDXGIDeviceManager DeviceManager { get; }
    public long AdapterLuid { get; }
    public string AdapterName { get; }

    private GpuDevice(IDXGIAdapter1 adapter, ID3D11Device device, ID3D11DeviceContext context, IMFDXGIDeviceManager manager)
    {
        Adapter = adapter;
        Device = device;
        Context = context;
        DeviceManager = manager;
        var desc = adapter.Description1;
        AdapterLuid = ((long)desc.Luid.HighPart << 32) | desc.Luid.LowPart;
        AdapterName = desc.Description.TrimEnd('\0');
    }

    public static GpuDevice Create(int adapterIndex)
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        factory.EnumAdapters1((uint)adapterIndex, out var adapter).CheckError();

        FeatureLevel[] levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1];
        var flags = DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport;
        var hr = D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, flags, levels, out ID3D11Device device, out ID3D11DeviceContext context);
        if (hr.Failure || device is null || context is null)
        {
            adapter.Dispose();
            throw new InvalidOperationException($"D3D11CreateDevice failed on adapter {adapterIndex}: {hr}");
        }

        // Media Foundation uses the device from its own threads.
        using (var mt = device.QueryInterface<ID3D11Multithread>())
        {
            mt.SetMultithreadProtected(true);
        }

        var manager = MediaFactory.MFCreateDXGIDeviceManager();
        manager.ResetDevice(device).CheckError();
        return new GpuDevice(adapter, device, context, manager);
    }

    public void Dispose()
    {
        DeviceManager.Dispose();
        Context.ClearState();
        Context.Flush();
        Context.Dispose();
        Device.Dispose();
        Adapter.Dispose();
    }
}
