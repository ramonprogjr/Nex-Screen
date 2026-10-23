// nexscreen-vdd-ctl: creates and removes the NexScreen Virtual Display software device.
//
// Derived from IddSampleApp in microsoft/Windows-driver-samples video/IndirectDisplay (MS-PL). See ../../THIRD_PARTY.md.
//
// Usage (administrator):
//   nexscreen-vdd-ctl install [inf]  install the driver on a root-enumerated device (persists across reboots)
//   nexscreen-vdd-ctl uninstall      remove the root-enumerated device(s)
//   nexscreen-vdd-ctl run            software device; removed on 'x' or when the process exits
//   nexscreen-vdd-ctl create         software device kept after the process exits (until reboot or 'remove')
//   nexscreen-vdd-ctl remove         remove the software device created with 'create'

#include <cstdio>
#include <string>
#include <vector>

#include <windows.h>
#include <swdevice.h>
#include <conio.h>
#include <initguid.h>
#include <devguid.h>
#include <setupapi.h>
#include <newdev.h>

namespace
{
    // Hardware ID of the root-enumerated device; listed in NexScreenVDD.inf
    constexpr wchar_t c_rootHardwareIds[] = L"Root\\NexScreenVDD\0";
    constexpr PCWSTR c_rootHardwareId = L"Root\\NexScreenVDD";

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

    // True if the REG_MULTI_SZ hardware ID list of the device contains c_rootHardwareId
    bool HasRootHardwareId(HDEVINFO set, SP_DEVINFO_DATA* data)
    {
        wchar_t buffer[1024] = {};
        if (!SetupDiGetDeviceRegistryPropertyW(set, data, SPDRP_HARDWAREID, nullptr,
                                               reinterpret_cast<BYTE*>(buffer), sizeof(buffer) - sizeof(wchar_t) * 2, nullptr))
        {
            return false;
        }

        for (const wchar_t* id = buffer; *id; id += wcslen(id) + 1)
        {
            if (_wcsicmp(id, c_rootHardwareId) == 0)
            {
                return true;
            }
        }
        return false;
    }

    // Number of root-enumerated NexScreen devices, present or not
    int CountRootDevices()
    {
        HDEVINFO set = SetupDiGetClassDevsW(&GUID_DEVCLASS_DISPLAY, nullptr, nullptr, 0);
        if (set == INVALID_HANDLE_VALUE)
        {
            return 0;
        }

        int count = 0;
        SP_DEVINFO_DATA data = { sizeof(data) };
        for (DWORD i = 0; SetupDiEnumDeviceInfo(set, i, &data); i++)
        {
            if (HasRootHardwareId(set, &data))
            {
                count++;
            }
        }
        SetupDiDestroyDeviceInfoList(set);
        return count;
    }

    std::wstring DefaultInfPath()
    {
        wchar_t exe[MAX_PATH] = {};
        GetModuleFileNameW(nullptr, exe, MAX_PATH);
        std::wstring path(exe);
        path.resize(path.find_last_of(L'\\') + 1);
        return path + L"NexScreenVDD.inf";
    }

    int Install(const std::wstring& infArg)
    {
        wchar_t inf[MAX_PATH] = {};
        if (!GetFullPathNameW(infArg.c_str(), MAX_PATH, inf, nullptr) ||
            GetFileAttributesW(inf) == INVALID_FILE_ATTRIBUTES)
        {
            fwprintf(stderr, L"INF not found: %s\n", infArg.c_str());
            return 1;
        }

        // Create the device node once; afterwards only (re)install the driver on it
        if (CountRootDevices() == 0)
        {
            HDEVINFO set = SetupDiCreateDeviceInfoList(&GUID_DEVCLASS_DISPLAY, nullptr);
            if (set == INVALID_HANDLE_VALUE)
            {
                fprintf(stderr, "SetupDiCreateDeviceInfoList failed: %lu\n", GetLastError());
                return 1;
            }

            SP_DEVINFO_DATA data = { sizeof(data) };
            bool ok = SetupDiCreateDeviceInfoW(set, L"Display", &GUID_DEVCLASS_DISPLAY, c_description, nullptr,
                                               DICD_GENERATE_ID, &data) &&
                      SetupDiSetDeviceRegistryPropertyW(set, &data, SPDRP_HARDWAREID,
                                                        reinterpret_cast<const BYTE*>(c_rootHardwareIds),
                                                        sizeof(c_rootHardwareIds)) &&
                      SetupDiCallClassInstaller(DIF_REGISTERDEVICE, set, &data);
            DWORD error = GetLastError();
            SetupDiDestroyDeviceInfoList(set);
            if (!ok)
            {
                fprintf(stderr, "Creating the root device failed: %lu\n", error);
                return 1;
            }
            printf("Root device created\n");
        }

        BOOL reboot = FALSE;
        if (!UpdateDriverForPlugAndPlayDevicesW(nullptr, c_rootHardwareId, inf, INSTALLFLAG_FORCE, &reboot))
        {
            fprintf(stderr, "UpdateDriverForPlugAndPlayDevices failed: %lu\n", GetLastError());
            return 1;
        }

        printf("Driver installed on the root device%s\n", reboot ? " (reboot required)" : "");
        return 0;
    }

    int Uninstall()
    {
        HDEVINFO set = SetupDiGetClassDevsW(&GUID_DEVCLASS_DISPLAY, nullptr, nullptr, 0);
        if (set == INVALID_HANDLE_VALUE)
        {
            fprintf(stderr, "SetupDiGetClassDevs failed: %lu\n", GetLastError());
            return 1;
        }

        int removed = 0;
        int failed = 0;
        SP_DEVINFO_DATA data = { sizeof(data) };
        for (DWORD i = 0; SetupDiEnumDeviceInfo(set, i, &data); i++)
        {
            if (!HasRootHardwareId(set, &data))
            {
                continue;
            }

            BOOL reboot = FALSE;
            if (DiUninstallDevice(nullptr, set, &data, 0, &reboot))
            {
                removed++;
            }
            else
            {
                fprintf(stderr, "DiUninstallDevice failed: %lu\n", GetLastError());
                failed++;
            }
        }
        SetupDiDestroyDeviceInfoList(set);

        printf("Removed %d root device(s)\n", removed);
        return failed == 0 ? 0 : 1;
    }

    void PrintUsage()
    {
        fprintf(stderr,
            "Usage: nexscreen-vdd-ctl <command>\n"
            "  install [inf]  install the driver on a root-enumerated device that persists across reboots\n"
            "                 (default inf: NexScreenVDD.inf next to this program)\n"
            "  uninstall      remove the root-enumerated device(s)\n"
            "  run            software device; removed on 'x' or when the process exits\n"
            "  create         software device kept after the process exits (until reboot or 'remove')\n"
            "  remove         remove a software device created with 'create'\n");
    }
}

int __cdecl wmain(int argc, wchar_t* argv[])
{
    if (argc >= 2 && _wcsicmp(argv[1], L"install") == 0 && argc <= 3)
    {
        return Install(argc == 3 ? argv[2] : DefaultInfPath());
    }

    if (argc != 2)
    {
        PrintUsage();
        return 2;
    }

    if (_wcsicmp(argv[1], L"uninstall") == 0)
    {
        return Uninstall();
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
