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

O companion é um app .NET 8 em `windows\companion` (solução `windows\NexScreen.sln`, com o projeto de
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

- Instalação para uso diário: `.\tools\install-companion.ps1` publica uma cópia self-contained (arquivo único,
  win-x64) em `%LOCALAPPDATA%\Programs\NexScreen` e cria o atalho "NexScreen" no menu Iniciar, que roda
  `NexScreen.Companion.exe --tray`. Não precisa de administrador. `-NoLaunch` não abre o programa no fim;
  `-Uninstall` remove a pasta, o atalho e o valor `NexScreen` de `HKCU\...\CurrentVersion\Run`.
- `--tray`: sem janela, com ícone na área de notificação (estado, conexão USB/Wi-Fi, "Estender a área de
  trabalho", "Manter a tela do tablet estendida", "Abrir pasta de logs", "Iniciar com o Windows", "Sair"). Uma
  instância por sessão; abrir de novo só mostra um aviso. "Iniciar com o Windows" grava
  `"<exe>" --tray` em `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` e só é ligado pelo usuário.
- O exe é do subsistema Windows (para o `--tray` não piscar um console). Nos outros modos ele se conecta ao console
  de quem o chamou, mas o shell não espera um programa desse tipo: use o `run-companion.ps1` (que espera) ou, no
  cmd, `start /wait NexScreen.Companion.exe --list`. Saída redirecionada (`> arquivo`, pipe) funciona normalmente.
- Ligação automática com o tablet (em todos os modos; `--no-adb` desliga): a cada ~2 s o companion roda
  `adb devices -l` e, para cada tablet autorizado que aparece ou reconecta (USB ou depuração sem fio), roda
  `adb -s <serial> reverse tcp:<porta> tcp:<porta>` e abre o app (`am start -n app.nexscreen/.MainActivity`). O
  mesmo tablet listado duas vezes (`ip:porta` e o nome mDNS `adb-..._adb-tls-connect._tcp`) é reconhecido pelo
  `ro.serialno` e ligado uma vez só (USB, depois `ip:porta`, depois mDNS). Sem nenhum tablet, ele roda
  `adb mdns services` (a cada 10 s, até 60 s) e faz `adb connect` nos serviços `_adb-tls-connect._tcp`, que só
  aceitam PCs já pareados; o pareamento nunca é automático. O adb é procurado em `--adb <caminho>`, no PATH, em
  `%LOCALAPPDATA%\Android\Sdk\platform-tools` e no pacote WinGet `Google.PlatformTools`. Os comandos rodam sem
  janela, com timeout e com espera crescente quando falham.
- Monitor fora da área de trabalho: sem o monitor virtual o companion continua rodando e verifica a cada 2 s. Se o
  monitor NexScreen/VDD existe mas não está anexado (QueryDisplayConfig com `QDC_ALL_PATHS`) e há um tablet
  conectado, ele roda `DisplaySwitch.exe /extend` (no máximo a cada 30 s, desiste depois de 3 tentativas até o
  monitor voltar ou o tablet reconectar). `--no-auto-extend` desliga. Falhas de captura/encoder são repetidas com
  espera de 1 s até 30 s.
- O script compila a solução e repassa os argumentos (`-NoBuild` pula o build). Sem argumentos, o companion procura
  o monitor com nome de EDID `NexScreen` (fabricante `NXS`); sem o driver instalado, use `--output primary` ou
  `--output \.\DISPLAYn`.
- Opções principais: `--fps` (padrão 30), `--bitrate` (padrão `8M`), `--gop` (padrão fps×10), `--size WxH`,
  `--profile main|high`, `--level 4.2`, `--encoder auto|hardware|software`, `--port`. Veja `--help`.
- O ponteiro do mouse é desenhado no vídeo (a captura o deixa de fora) e movê-lo gera quadros novos; `--no-cursor` desliga.
- Toques no tablet viram mouse no monitor capturado (M3): `POINTER`/`SCROLL` do NexLink são mapeados para o retângulo
  do monitor no desktop (DXGI `DesktopCoordinates`, mesmo seletor `--output` da captura) e injetados com `SendInput`
  (`MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK`, roda e roda horizontal). O processo é *per-monitor DPI aware*
  (PerMonitorV2 no `app.manifest`), então funciona com escala diferente em cada monitor e origens negativas. Botões
  pressionados são soltos quando o tablet desconecta. `--no-input` desliga a injeção. O `SendInput` não alcança a
  tela de UAC/bloqueio nem janelas elevadas (UIPI) enquanto o companion roda sem privilégios.
- O `--dump` grava o stream Annex-B (CODEC_CONFIG + quadros) sem cliente. Para conferir:
  `ffprobe -show_streams test.h264` e `ffmpeg -v error -i test.h264 -f null -` (não deve imprimir nada).
- O log vai para o console e para `%LOCALAPPDATA%\NexScreen\logs\companion-AAAAMMDD.log` (`--no-log-file` desliga).
  Ele registra encoder, resolução, FPS, bitrate, conexões e STATS do cliente, nunca imagens.
- Uma conexão nova derruba a anterior. Com o app do tablet aberto e o `adb reverse` ativo, ele reconecta sozinho;
  para testes locais com outro cliente, use outra porta (`--port 27184`).
