# Third-party code

## Windows-driver-samples: Indirect Display (IddCx)

- Source: https://github.com/microsoft/Windows-driver-samples/tree/bb68ed5e770bce04dc5395ec9258dafab034184d/video/IndirectDisplay
- Commit: `bb68ed5e770bce04dc5395ec9258dafab034184d`
- License: Microsoft Public License (MS-PL), reproduced below.

| Upstream file | NexScreen file | Changes |
|---|---|---|
| `IddSampleDriver/Driver.cpp` | `src/Driver.cpp` | 1 monitor, NexScreen EDID, 4 modes, `INDIRECT_WIRED`, fixed container ID, telemetry strings |
| `IddSampleDriver/Driver.h` | `src/Driver.h` | `szModeList = 4` |
| `IddSampleDriver/Trace.h` | `src/Trace.h` | Trace GUID and tracing ID |
| `IddSampleDriver/IddSampleDriver.inf` | `inf/NexScreenVDD.inf` | Names, hardware IDs, device group |
| `IddSampleDriver/IddSampleDriver.vcxproj` | `src/NexScreenVDD.vcxproj` | Names, new GUID, x64 only, own `.rc`, WDK signing off |
| `IddSampleDriver/IddSampleDriver.rc` | `src/NexScreenVDD.rc` | Rewritten with NexScreen version info |
| `IddSampleApp/main.cpp` | `tools/nexscreen-vdd-ctl/main.cpp` | `run`/`create`/`remove`, checks `hrCreateResult`, exit codes |
| `IddSampleApp/IddSampleApp.vcxproj` | `tools/nexscreen-vdd-ctl/nexscreen-vdd-ctl.vcxproj` | Names, new GUID, x64 only |
| `IddSampleDriver.sln` | `NexScreenVDD.sln` | New projects and GUIDs |

Under MS-PL 3(D), any distribution of these files in source form must stay under MS-PL and include the license
text below.

```
The Microsoft Public License (MS-PL)
Copyright (c) 2015 Microsoft

This license governs use of the accompanying software. If you use the software, you
 accept this license. If you do not accept the license, do not use the software.

1. Definitions
 The terms "reproduce," "reproduction," "derivative works," and "distribution" have the
 same meaning here as under U.S. copyright law.
 A "contribution" is the original software, or any additions or changes to the software.
 A "contributor" is any person that distributes its contribution under this license.
 "Licensed patents" are a contributor's patent claims that read directly on its contribution.

2. Grant of Rights
 (A) Copyright Grant- Subject to the terms of this license, including the license conditions and limitations in section 3, each contributor grants you a non-exclusive, worldwide, royalty-free copyright license to reproduce its contribution, prepare derivative works of its contribution, and distribute its contribution or any derivative works that you create.
 (B) Patent Grant- Subject to the terms of this license, including the license conditions and limitations in section 3, each contributor grants you a non-exclusive, worldwide, royalty-free license under its licensed patents to make, have made, use, sell, offer for sale, import, and/or otherwise dispose of its contribution in the software or derivative works of the contribution in the software.

3. Conditions and Limitations
 (A) No Trademark License- This license does not grant you rights to use any contributors' name, logo, or trademarks.
 (B) If you bring a patent claim against any contributor over patents that you claim are infringed by the software, your patent license from such contributor to the software ends automatically.
 (C) If you distribute any portion of the software, you must retain all copyright, patent, trademark, and attribution notices that are present in the software.
 (D) If you distribute any portion of the software in source code form, you may do so only under this license by including a complete copy of this license with your distribution. If you distribute any portion of the software in compiled or object code form, you may only do so under a license that complies with this license.
 (E) The software is licensed "as-is." You bear the risk of using it. The contributors give no express warranties, guarantees or conditions. You may have additional consumer rights under your local laws which this license cannot change. To the extent permitted under your local laws, the contributors exclude the implied warranties of merchantability, fitness for a particular purpose and non-infringement.
```
