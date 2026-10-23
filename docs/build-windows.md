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

Para validar o M1: `powershell -ExecutionPolicy Bypass -File N:\tools\vm-m1-check.ps1 -Cycles 10`. Ele confere o
device e o monitor, os 4 modos e os ciclos de remove/create, e grava o log em `C:\ProgramData\NexScreen\m1-check.log`.

Para remover: `vm-uninstall-driver.ps1` (use `-DisableTestSigning` para desligar o modo de teste também).

## Notas

- O Hyper-V só existe no Windows 11 Pro, Enterprise e Education. No **Home**, use o VirtualBox:
  `.\tools\new-test-vm-vbox.ps1 -IsoPath <iso>` cria a VM (EFI, TPM 2.0, Secure Boot desligado) e compartilha o
  repositório como `N:`, que aparece depois que as Guest Additions são instaladas no convidado. Os scripts `vm-*.ps1`
  funcionam em qualquer VM.
- O `tools/gen_edid.py` gera o EDID usado em `Driver.cpp`. Depois de alterar um parâmetro, rode
  `python driver\virtual-display\tools\gen_edid.py` e cole a saída no array.

## Companion (M2)

O companion é um app de console .NET 8 em `windows\companion` (solução `windows\NexScreen.sln`, com o projeto de
testes). Ele localiza o monitor, captura com DXGI Desktop Duplication, converte BGRA→NV12 na GPU, codifica H.264
pelo Media Foundation (MFT de hardware; se falhar, o encoder de software da Microsoft) e serve o
[NexLink v0-usb](../protocol/nexlink-v0-usb.md) em `127.0.0.1:27183`.

```powershell
cd windows
dotnet build -c Release
dotnet test -c Release

.\tools\run-companion.ps1 --list                                   # monitores e encoders H.264 disponíveis
.\tools\run-companion.ps1                                          # monitor "NexScreen" (EDID), servidor TCP
.\tools\run-companion.ps1 --output primary --size 1920x1200        # teste sem o driver: captura a tela principal
.\tools\run-companion.ps1 --output primary --size 1920x1200 --dump $env:TEMP\test.h264 --seconds 5
adb reverse tcp:27183 tcp:27183                                    # liga a porta do tablet à do notebook
```

- O script compila a solução e repassa os argumentos (`-NoBuild` pula o build). Sem argumentos, o companion procura
  o monitor com nome de EDID `NexScreen` (fabricante `NXS`); sem o driver instalado, use `--output primary` ou
  `--output \.\DISPLAYn`.
- Opções principais: `--fps` (padrão 30), `--bitrate` (padrão `8M`), `--gop` (padrão fps×10), `--size WxH`,
  `--profile main|high`, `--level 4.2`, `--encoder auto|hardware|software`, `--port`. Veja `--help`.
- O `--dump` grava o stream Annex-B (CODEC_CONFIG + quadros) sem cliente. Para conferir:
  `ffprobe -show_streams test.h264` e `ffmpeg -v error -i test.h264 -f null -` (não deve imprimir nada).
- O log vai para o console e para `%LOCALAPPDATA%\NexScreen\logs\companion-AAAAMMDD.log` (`--no-log-file` desliga).
  Ele registra encoder, resolução, FPS, bitrate, conexões e STATS do cliente, nunca imagens.
- Uma conexão nova derruba a anterior. Com o app do tablet aberto e o `adb reverse` ativo, ele reconecta sozinho;
  para testes locais com outro cliente, use outra porta (`--port 27184`).
