# Build e teste do driver (Windows)

Pré-requisitos: [PROJETO.md, seção 5](../PROJETO.md#5-preparar-a-máquina-de-desenvolvimento).

## Na máquina de build

```powershell
.\tools\build-driver.ps1        # gera out\driver\ (.dll, .inf com o DriverVer preenchido, nexscreen-vdd-ctl.exe)
.\tools\make-test-cert.ps1      # cria o "CN=NexScreen Test", assina o .dll, roda o inf2cat e assina o .cat
```

Se o `inf2cat` reclamar do sistema operacional, passe outro alvo: `.\tools\make-test-cert.ps1 -Os 10_GE_X64`.

## Na VM de teste (administrador)

Copie `out\driver\` e `tools\vm-*.ps1`. Com Hyper-V, use `.\tools\new-test-vm.ps1 -CopyPackage`.

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\vm-install-driver.ps1   # 1ª vez: liga o test signing; reinicie
powershell -ExecutionPolicy Bypass -File .\tools\vm-install-driver.ps1   # 2ª vez: instala e cria o monitor
pnputil /enum-devices /class Display
```

Para remover: `vm-uninstall-driver.ps1` (use `-DisableTestSigning` para desligar o modo de teste também).

## Notas

- O Hyper-V só existe no Windows 11 Pro, Enterprise e Education. No **Home**, use o VirtualBox:
  `.\tools\new-test-vm-vbox.ps1 -IsoPath <iso>` cria a VM (EFI, TPM 2.0, Secure Boot desligado) e compartilha o
  repositório como `N:`, que aparece depois que as Guest Additions são instaladas no convidado. Os scripts `vm-*.ps1`
  funcionam em qualquer VM.
- O `tools/gen_edid.py` gera o EDID usado em `Driver.cpp`. Depois de alterar um parâmetro, rode
  `python driver\virtual-display\tools\gen_edid.py` e cole a saída no array.
