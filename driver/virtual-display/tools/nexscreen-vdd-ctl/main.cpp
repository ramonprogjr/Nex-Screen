// nexscreen-vdd-ctl: creates and removes the NexScreen Virtual Display software device.
//
// Derived from IddSampleApp in microsoft/Windows-driver-samples video/IndirectDisplay (MS-PL). See ../../THIRD_PARTY.md.
//
// Usage (administrator):
//   nexscreen-vdd-ctl run      create the device; it is removed on 'x' or when the process exits
//   nexscreen-vdd-ctl create   create the device and keep it after the process exits
//   nexscreen-vdd-ctl remove   remove a device previously created with 'create'

#include <cstdio>

#include <windows.h>
#include <swdevice.h>
#include <conio.h>

namespace
{
    // These match the PnP ids in NexScreenVDD.inf so the OS loads the driver when the device is created
    constexpr PCWSTR c_enumeratorName = L"NexScreenVDD";
    constexpr PCWSTR c_parentDeviceInstance = L"HTREE\\ROOT\\0";
    constexpr PCWSTR c_instanceId = L"NexScreenVDD";
    constexpr PCWSTR c_hardwareIds = L"NexScreenVDD\0\0";
    constexpr PCWSTR c_compatibleIds = L"NexScreenVDD\0\0";
    constexpr PCWSTR c_description = L"NexScreen Virtual Display";

    constexpr DWORD c_creationTimeoutMs = 10 * 1000;

    struct CreationContext
    {
        HANDLE hEvent;
        HRESULT hrResult;
    };

    VOID WINAPI CreationCallback(
        _In_ HSWDEVICE hSwDevice,
        _In_ HRESULT hrCreateResult,
        _In_opt_ PVOID pContext,
        _In_opt_ PCWSTR pszDeviceInstanceId)
    {
        UNREFERENCED_PARAMETER(hSwDevice);
        UNREFERENCED_PARAMETER(pszDeviceInstanceId);

        auto* context = static_cast<CreationContext*>(pContext);
        context->hrResult = hrCreateResult;
        SetEvent(context->hEvent);
    }

    // Creates the software device, or opens it if it already exists, and waits for the creation callback.
    HRESULT OpenDevice(_Out_ HSWDEVICE* phSwDevice)
    {
        *phSwDevice = nullptr;

        CreationContext context = {};
        context.hEvent = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        context.hrResult = E_PENDING;
        if (context.hEvent == nullptr)
        {
            return HRESULT_FROM_WIN32(GetLastError());
        }

        SW_DEVICE_CREATE_INFO createInfo = {};
        createInfo.cbSize = sizeof(createInfo);
        createInfo.pszzCompatibleIds = c_compatibleIds;
        createInfo.pszInstanceId = c_instanceId;
        createInfo.pszzHardwareIds = c_hardwareIds;
        createInfo.pszDeviceDescription = c_description;
        createInfo.CapabilityFlags = SWDeviceCapabilitiesRemovable |
                                     SWDeviceCapabilitiesSilentInstall |
                                     SWDeviceCapabilitiesDriverRequired;

        HSWDEVICE hSwDevice = nullptr;
        HRESULT hr = SwDeviceCreate(c_enumeratorName,
                                    c_parentDeviceInstance,
                                    &createInfo,
                                    0,
                                    nullptr,
                                    CreationCallback,
                                    &context,
                                    &hSwDevice);
        if (FAILED(hr))
        {
            fprintf(stderr, "SwDeviceCreate failed with 0x%08lx\n", hr);
            CloseHandle(context.hEvent);
            return hr;
        }

        printf("Waiting for device to be created...\n");
        DWORD waitResult = WaitForSingleObject(context.hEvent, c_creationTimeoutMs);
        if (waitResult != WAIT_OBJECT_0)
        {
            fprintf(stderr, "Timed out waiting for device creation\n");
            // Closing the handle cancels the creation, so the callback can no longer reference 'context'
            SwDeviceClose(hSwDevice);
            CloseHandle(context.hEvent);
            return HRESULT_FROM_WIN32(ERROR_TIMEOUT);
        }
        CloseHandle(context.hEvent);

        if (FAILED(context.hrResult))
        {
            fprintf(stderr, "Device creation failed with 0x%08lx\n", context.hrResult);
            SwDeviceClose(hSwDevice);
            return context.hrResult;
        }

        *phSwDevice = hSwDevice;
        return S_OK;
    }

    int Run()
    {
        HSWDEVICE hSwDevice = nullptr;
        if (FAILED(OpenDevice(&hSwDevice)))
        {
            return 1;
        }
        printf("Device created\n\n");

        printf("Press 'x' to exit and destroy the software device\n");
        for (;;)
        {
            int key = _getch();
            if (key == 'x' || key == 'X')
            {
                break;
            }
        }

        // Closing the handle removes the device, which unloads the driver
        SwDeviceClose(hSwDevice);
        return 0;
    }

    int Create()
    {
        HSWDEVICE hSwDevice = nullptr;
        if (FAILED(OpenDevice(&hSwDevice)))
        {
            return 1;
        }

        HRESULT hr = SwDeviceSetLifetime(hSwDevice, SWDeviceLifetimeParentPresent);
        if (FAILED(hr))
        {
            fprintf(stderr, "SwDeviceSetLifetime(ParentPresent) failed with 0x%08lx\n", hr);
            SwDeviceClose(hSwDevice);
            return 1;
        }

        // The device now outlives this handle
        SwDeviceClose(hSwDevice);
        printf("Device created and will persist until 'remove'\n");
        return 0;
    }

    int Remove()
    {
        // SwDeviceCreate with the same ids reopens the existing device
        HSWDEVICE hSwDevice = nullptr;
        if (FAILED(OpenDevice(&hSwDevice)))
        {
            return 1;
        }

        HRESULT hr = SwDeviceSetLifetime(hSwDevice, SWDeviceLifetimeHandle);
        if (FAILED(hr))
        {
            fprintf(stderr, "SwDeviceSetLifetime(Handle) failed with 0x%08lx\n", hr);
            SwDeviceClose(hSwDevice);
            return 1;
        }

        // With a handle lifetime, closing the handle removes the device
        SwDeviceClose(hSwDevice);
        printf("Device removed\n");
        return 0;
    }

    void PrintUsage()
    {
        fprintf(stderr,
            "Usage: nexscreen-vdd-ctl <command>\n"
            "  run      create the device; removed on 'x' or when the process exits\n"
            "  create   create the device and keep it after the process exits\n"
            "  remove   remove a device created with 'create'\n");
    }
}

int __cdecl wmain(int argc, wchar_t* argv[])
{
    if (argc != 2)
    {
        PrintUsage();
        return 2;
    }

    if (_wcsicmp(argv[1], L"run") == 0)
    {
        return Run();
    }
    if (_wcsicmp(argv[1], L"create") == 0)
    {
        return Create();
    }
    if (_wcsicmp(argv[1], L"remove") == 0)
    {
        return Remove();
    }

    PrintUsage();
    return 2;
}
