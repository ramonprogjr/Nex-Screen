// nexscreen-vdd-ctl: creates and removes the virtual display device (NexScreen's driver or, with --hwid, another
// root-enumerated indirect display driver such as the Virtual Display Driver, Root\MttVDD). Used by the installer
// instead of devcon, which is not redistributable.
//
// Derived from IddSampleApp in microsoft/Windows-driver-samples video/IndirectDisplay (MS-PL). See ../../THIRD_PARTY.md.
//
// Usage (install/uninstall need an administrator):
//   nexscreen-vdd-ctl install [inf] [--hwid <id>]       install the driver on a root-enumerated device (persists
//                                                        across reboots); an existing device is reused
//   nexscreen-vdd-ctl uninstall [--hwid <id>] [--keep-driver]
//                                                        remove the root-enumerated device(s) and their driver package
//   nexscreen-vdd-ctl status [--hwid <id>]              list the devices; exit code 0 if one works
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
#include <devpkey.h>
#include <cfgmgr32.h>
#include <setupapi.h>
#include <newdev.h>

namespace
{
    // Default hardware ID of the root-enumerated device; listed in NexScreenVDD.inf. --hwid selects another one,
    // e.g. Root\MttVDD for the Virtual Display Driver.
    constexpr PCWSTR c_rootHardwareId = L"Root\\NexScreenVDD";

    // These match the PnP ids in NexScreenVDD.inf so the OS loads the driver when the device is created
    constexpr PCWSTR c_enumeratorName = L"NexScreenVDD";
    constexpr PCWSTR c_parentDeviceInstance = L"HTREE\\ROOT\\0";
    constexpr PCWSTR c_instanceId = L"NexScreenVDD";
    constexpr PCWSTR c_hardwareIds = L"NexScreenVDD\0\0";
    constexpr PCWSTR c_compatibleIds = L"NexScreenVDD\0\0";
    constexpr PCWSTR c_description = L"NexScreen Virtual Display";

    constexpr DWORD c_creationTimeoutMs = 10 * 1000;

    // install/uninstall succeeded but Windows asks for a reboot to finish
    constexpr int c_exitRebootRequired = 3;

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

    bool IsElevated()
    {
        HANDLE token = nullptr;
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token))
        {
            return false;
        }
        TOKEN_ELEVATION elevation = {};
        DWORD size = 0;
        bool elevated = GetTokenInformation(token, TokenElevation, &elevation, sizeof(elevation), &size) &&
                        elevation.TokenIsElevated != 0;
        CloseHandle(token);
        return elevated;
    }

    // True if the REG_MULTI_SZ hardware ID list of the device contains hardwareId
    bool HasHardwareId(HDEVINFO set, SP_DEVINFO_DATA* data, const std::wstring& hardwareId)
    {
        wchar_t buffer[1024] = {};
        if (!SetupDiGetDeviceRegistryPropertyW(set, data, SPDRP_HARDWAREID, nullptr,
                                               reinterpret_cast<BYTE*>(buffer), sizeof(buffer) - sizeof(wchar_t) * 2, nullptr))
        {
            return false;
        }

        for (const wchar_t* id = buffer; *id; id += wcslen(id) + 1)
        {
            if (_wcsicmp(id, hardwareId.c_str()) == 0)
            {
                return true;
            }
        }
        return false;
    }

    std::wstring GetStringProperty(HDEVINFO set, SP_DEVINFO_DATA* data, const DEVPROPKEY& key)
    {
        wchar_t buffer[512] = {};
        DEVPROPTYPE type = 0;
        if (!SetupDiGetDevicePropertyW(set, data, &key, &type, reinterpret_cast<BYTE*>(buffer),
                                       sizeof(buffer) - sizeof(wchar_t), nullptr, 0) ||
            type != DEVPROP_TYPE_STRING)
        {
            return std::wstring();
        }
        return std::wstring(buffer);
    }

    std::wstring GetInstanceId(HDEVINFO set, SP_DEVINFO_DATA* data)
    {
        wchar_t buffer[MAX_DEVICE_ID_LEN] = {};
        if (!SetupDiGetDeviceInstanceIdW(set, data, buffer, MAX_DEVICE_ID_LEN, nullptr))
        {
            return std::wstring();
        }
        return std::wstring(buffer);
    }

    // Number of root-enumerated devices with this hardware ID, present or not
    int CountRootDevices(const std::wstring& hardwareId)
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
            if (HasHardwareId(set, &data, hardwareId))
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

    int Install(const std::wstring& infArg, const std::wstring& hardwareId)
    {
        wchar_t inf[MAX_PATH] = {};
        if (!GetFullPathNameW(infArg.c_str(), MAX_PATH, inf, nullptr) ||
            GetFileAttributesW(inf) == INVALID_FILE_ATTRIBUTES)
        {
            fwprintf(stderr, L"INF not found: %s\n", infArg.c_str());
            return 1;
        }

        // Create the device node once (it may already exist, e.g. created earlier with devcon); afterwards only
        // (re)install the driver on it
        int existing = CountRootDevices(hardwareId);
        if (existing == 0)
        {
            HDEVINFO set = SetupDiCreateDeviceInfoList(&GUID_DEVCLASS_DISPLAY, nullptr);
            if (set == INVALID_HANDLE_VALUE)
            {
                fprintf(stderr, "SetupDiCreateDeviceInfoList failed: %lu\n", GetLastError());
                return 1;
            }

            // REG_MULTI_SZ: the ID, its terminator and the list terminator
            std::wstring hardwareIds = hardwareId;
            hardwareIds.push_back(L'\0');
            hardwareIds.push_back(L'\0');

            // The INF's device description replaces this once the driver is installed
            PCWSTR description = _wcsicmp(hardwareId.c_str(), c_rootHardwareId) == 0 ? c_description : nullptr;

            SP_DEVINFO_DATA data = { sizeof(data) };
            bool ok = SetupDiCreateDeviceInfoW(set, L"Display", &GUID_DEVCLASS_DISPLAY, description, nullptr,
                                               DICD_GENERATE_ID, &data) &&
                      SetupDiSetDeviceRegistryPropertyW(set, &data, SPDRP_HARDWAREID,
                                                        reinterpret_cast<const BYTE*>(hardwareIds.c_str()),
                                                        static_cast<DWORD>(hardwareIds.size() * sizeof(wchar_t))) &&
                      SetupDiCallClassInstaller(DIF_REGISTERDEVICE, set, &data);
            DWORD error = GetLastError();
            SetupDiDestroyDeviceInfoList(set);
            if (!ok)
            {
                fprintf(stderr, "Creating the root device failed: %lu\n", error);
                return 1;
            }
            wprintf(L"Root device created (%s)\n", hardwareId.c_str());
        }
        else
        {
            wprintf(L"%d device(s) with hardware ID %s already exist; updating the driver only\n", existing,
                    hardwareId.c_str());
        }

        BOOL reboot = FALSE;
        if (!UpdateDriverForPlugAndPlayDevicesW(nullptr, hardwareId.c_str(), inf, INSTALLFLAG_FORCE, &reboot))
        {
            fprintf(stderr, "UpdateDriverForPlugAndPlayDevices failed: %lu\n", GetLastError());
            return 1;
        }

        printf("Driver installed on the root device%s\n", reboot ? " (reboot required)" : "");
        return reboot ? c_exitRebootRequired : 0;
    }

    // True for a published third-party driver package name such as oem12.inf (never an inbox INF)
    bool IsOemInfName(const std::wstring& name)
    {
        if (name.size() < 8 || name.find_first_of(L"\\/:") != std::wstring::npos)
        {
            return false;
        }
        return _wcsnicmp(name.c_str(), L"oem", 3) == 0 && _wcsicmp(name.c_str() + name.size() - 4, L".inf") == 0;
    }

    int Uninstall(const std::wstring& hardwareId, bool keepDriverPackage)
    {
        HDEVINFO set = SetupDiGetClassDevsW(&GUID_DEVCLASS_DISPLAY, nullptr, nullptr, 0);
        if (set == INVALID_HANDLE_VALUE)
        {
            fprintf(stderr, "SetupDiGetClassDevs failed: %lu\n", GetLastError());
            return 1;
        }

        int removed = 0;
        int failed = 0;
        bool needReboot = false;
        std::vector<std::wstring> packages;
        SP_DEVINFO_DATA data = { sizeof(data) };
        for (DWORD i = 0; SetupDiEnumDeviceInfo(set, i, &data); i++)
        {
            if (!HasHardwareId(set, &data, hardwareId))
            {
                continue;
            }

            // Read the published INF (oemNN.inf) before the device and its driver key go away
            std::wstring infName = GetStringProperty(set, &data, DEVPKEY_Device_DriverInfPath);
            if (IsOemInfName(infName))
            {
                bool known = false;
                for (const auto& p : packages)
                {
                    known = known || _wcsicmp(p.c_str(), infName.c_str()) == 0;
                }
                if (!known)
                {
                    packages.push_back(infName);
                }
            }

            BOOL reboot = FALSE;
            if (DiUninstallDevice(nullptr, set, &data, 0, &reboot))
            {
                wprintf(L"Removed device %s%s\n", GetInstanceId(set, &data).c_str(), reboot ? L" (reboot required)" : L"");
                needReboot = needReboot || reboot;
                removed++;
            }
            else
            {
                fprintf(stderr, "DiUninstallDevice failed: %lu\n", GetLastError());
                failed++;
            }
        }
        SetupDiDestroyDeviceInfoList(set);
        wprintf(L"Removed %d device(s) with hardware ID %s\n", removed, hardwareId.c_str());

        if (keepDriverPackage || failed > 0)
        {
            return failed > 0 ? 1 : needReboot ? c_exitRebootRequired : 0;
        }

        // Without SUOI_FORCEDELETE this fails if another device still uses the package, which is what we want
        for (const auto& inf : packages)
        {
            if (SetupUninstallOEMInfW(inf.c_str(), 0, nullptr))
            {
                wprintf(L"Deleted driver package %s from the driver store\n", inf.c_str());
            }
            else
            {
                fwprintf(stderr, L"SetupUninstallOEMInf(%s) failed: %lu\n", inf.c_str(), GetLastError());
                failed++;
            }
        }
        return failed > 0 ? 1 : needReboot ? c_exitRebootRequired : 0;
    }

    // Lists the devices with this hardware ID. Exit code 0 if at least one is present without a problem code.
    int Status(const std::wstring& hardwareId)
    {
        HDEVINFO set = SetupDiGetClassDevsW(&GUID_DEVCLASS_DISPLAY, nullptr, nullptr, 0);
        if (set == INVALID_HANDLE_VALUE)
        {
            fprintf(stderr, "SetupDiGetClassDevs failed: %lu\n", GetLastError());
            return 1;
        }

        int found = 0;
        int working = 0;
        SP_DEVINFO_DATA data = { sizeof(data) };
        for (DWORD i = 0; SetupDiEnumDeviceInfo(set, i, &data); i++)
        {
            if (!HasHardwareId(set, &data, hardwareId))
            {
                continue;
            }
            found++;

            DEVPROPTYPE type = 0;
            DEVPROP_BOOLEAN present = DEVPROP_FALSE;
            SetupDiGetDevicePropertyW(set, &data, &DEVPKEY_Device_IsPresent, &type,
                                      reinterpret_cast<BYTE*>(&present), sizeof(present), nullptr, 0);
            ULONG problem = 0;
            bool hasProblem = SetupDiGetDevicePropertyW(set, &data, &DEVPKEY_Device_ProblemCode, &type,
                                                        reinterpret_cast<BYTE*>(&problem), sizeof(problem), nullptr, 0) &&
                              problem != 0;
            if (present == DEVPROP_TRUE && !hasProblem)
            {
                working++;
            }

            std::wstring inf = GetStringProperty(set, &data, DEVPKEY_Device_DriverInfPath);
            wprintf(L"%s  present=%s  problem=%lu  inf=%s\n", GetInstanceId(set, &data).c_str(),
                    present == DEVPROP_TRUE ? L"yes" : L"no", hasProblem ? problem : 0UL,
                    inf.empty() ? L"-" : inf.c_str());
        }
        SetupDiDestroyDeviceInfoList(set);

        wprintf(L"%d device(s) with hardware ID %s, %d working\n", found, hardwareId.c_str(), working);
        return working > 0 ? 0 : 1;
    }

    void PrintUsage(FILE* stream)
    {
        fprintf(stream,
            "Usage: nexscreen-vdd-ctl <command> [options]\n"
            "  install [inf] [--hwid <id>]\n"
            "      install the driver on a root-enumerated device that persists across reboots; the device is\n"
            "      created only if none with that hardware ID exists (default inf: NexScreenVDD.inf next to this\n"
            "      program)\n"
            "  uninstall [--hwid <id>] [--keep-driver]\n"
            "      remove the root-enumerated device(s) and delete their driver package (oemNN.inf) from the\n"
            "      driver store; --keep-driver keeps the package\n"
            "  status [--hwid <id>]\n"
            "      list the devices; exit code 0 if one is present and working\n"
            "  run       software device; removed on 'x' or when the process exits\n"
            "  create    software device kept after the process exits (until reboot or 'remove')\n"
            "  remove    remove a software device created with 'create'\n"
            "\n"
            "  --hwid <id>  hardware ID of the root device (default Root\\NexScreenVDD; Root\\MttVDD for the\n"
            "               Virtual Display Driver)\n"
            "install and uninstall need an administrator.\n"
            "Exit codes: 0 ok, 1 failed, 2 invalid arguments, 3 ok but a reboot is required.\n");
    }

    bool IsHelp(const wchar_t* arg)
    {
        return _wcsicmp(arg, L"--help") == 0 || _wcsicmp(arg, L"-h") == 0 || _wcsicmp(arg, L"/?") == 0 ||
               _wcsicmp(arg, L"-?") == 0 || _wcsicmp(arg, L"help") == 0;
    }

    // A hardware ID such as Root\MttVDD: printable, no spaces or commas, not an option
    bool IsValidHardwareId(const std::wstring& id)
    {
        if (id.empty() || id.size() > 200 || id[0] == L'-')
        {
            return false;
        }
        for (wchar_t c : id)
        {
            if (c <= L' ' || c == L',' || c > 0x7e)
            {
                return false;
            }
        }
        return true;
    }

    int UsageError(const wchar_t* message, const wchar_t* detail)
    {
        fwprintf(stderr, L"%s%s\n\n", message, detail ? detail : L"");
        PrintUsage(stderr);
        return 2;
    }
}

int __cdecl wmain(int argc, wchar_t* argv[])
{
    if (argc < 2)
    {
        PrintUsage(stderr);
        return 2;
    }
    if (IsHelp(argv[1]))
    {
        PrintUsage(stdout);
        return 0;
    }

    const std::wstring command = argv[1];
    const bool isInstall = _wcsicmp(command.c_str(), L"install") == 0;
    const bool isUninstall = _wcsicmp(command.c_str(), L"uninstall") == 0;
    const bool isStatus = _wcsicmp(command.c_str(), L"status") == 0;
    const bool isSwDevice = _wcsicmp(command.c_str(), L"run") == 0 || _wcsicmp(command.c_str(), L"create") == 0 ||
                            _wcsicmp(command.c_str(), L"remove") == 0;
    if (!isInstall && !isUninstall && !isStatus && !isSwDevice)
    {
        return UsageError(L"Unknown command: ", command.c_str());
    }

    std::wstring inf;
    std::wstring hardwareId = c_rootHardwareId;
    bool keepDriver = false;
    for (int i = 2; i < argc; i++)
    {
        const wchar_t* arg = argv[i];
        if (IsHelp(arg))
        {
            PrintUsage(stdout);
            return 0;
        }
        if (_wcsicmp(arg, L"--hwid") == 0 && !isSwDevice)
        {
            if (i + 1 >= argc || !IsValidHardwareId(argv[i + 1]))
            {
                return UsageError(L"--hwid needs a hardware ID such as Root\\MttVDD", nullptr);
            }
            hardwareId = argv[++i];
        }
        else if (_wcsicmp(arg, L"--keep-driver") == 0 && isUninstall)
        {
            keepDriver = true;
        }
        else if (isInstall && arg[0] != L'-' && inf.empty())
        {
            inf = arg;
        }
        else
        {
            return UsageError(L"Unexpected argument: ", arg);
        }
    }

    if ((isInstall || isUninstall) && !IsElevated())
    {
        fprintf(stderr, "%ls needs an elevated (administrator) prompt\n", command.c_str());
        return 1;
    }

    if (isInstall)
    {
        return Install(inf.empty() ? DefaultInfPath() : inf, hardwareId);
    }
    if (isUninstall)
    {
        return Uninstall(hardwareId, keepDriver);
    }
    if (isStatus)
    {
        return Status(hardwareId);
    }
    if (_wcsicmp(command.c_str(), L"run") == 0)
    {
        return Run();
    }
    if (_wcsicmp(command.c_str(), L"create") == 0)
    {
        return Create();
    }
    return Remove();
}
