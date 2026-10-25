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

## Instalador (NexScreen-Setup)

O instalador único é feito com o [Inno Setup 6](https://jrsoftware.org/isinfo.php), que é gratuito. O script fica em
`windows\installer\NexScreen.iss`.

```powershell
winget install --id JRSoftware.InnoSetup -e     # uma vez
.\tools\build-installer.ps1                     # gera out\installer\NexScreen-Setup-v<versão>.exe
.\tools\build-installer.ps1 -Version v0.3.0     # versão explícita (padrão: git describe --tags)
.\tools\build-installer.ps1 -SkipDriverBuild -CompanionDir out\companion   # reaproveita builds já feitos
```

O `build-installer.ps1`:

1. Baixa as dependências, cada uma numa pasta nova em `out\installer\downloads`, e confere cada uma:
   - o [Virtual Display Driver 25.7.23](https://github.com/VirtualDrivers/Virtual-Display-Driver/releases/tag/25.7.23)
     (zip "Driver.Only", que apesar do "x86" no nome é o build x64): SHA256 fixado no script e assinatura
     Authenticode válida da SignPath Foundation no `MttVDD.dll` e no `mttvdd.cat`;
   - o Android SDK Platform-Tools (versão fixada, hoje r37.0.1): SHA256 fixado e assinatura válida da Google LLC no
     `adb.exe`, `AdbWinApi.dll` e `AdbWinUsbApi.dll`.

   Se um hash ou uma assinatura não bate, o build para. Para atualizar uma versão, troque a URL e o SHA256 no topo
   do script.
2. Publica o companion self-contained (arquivo único, win-x64), com as mesmas opções do `release.yml`.
3. Compila o `nexscreen-vdd-ctl.exe` com o `build-driver.ps1` (precisa do VS 2022 + WDK).
4. Junta tudo em `out\installer\stage` e roda o `ISCC`.

O que o instalador faz (pede administrador uma vez):

- Copia o companion, o `nexscreen-vdd-ctl.exe` e as licenças para `C:\Program Files\NexScreen`, o adb para
  `platform-tools\` e o pacote do VDD para `vdd\`. Também cria o atalho "NexScreen" no menu Iniciar
  (`NexScreen.Companion.exe --tray --adb "<pasta>\platform-tools\adb.exe"`).
- Acrescenta `C:\Program Files\NexScreen\platform-tools` ao PATH do sistema. Assim o companion encontra o adb também
  quando é aberto pela inicialização com o Windows, cujo valor é só `"<exe>" --tray`.
- Grava `C:\VirtualDisplayDriver\vdd_settings.xml` (1728×1080, 1920×1080 e 1920×1200). Se já existe um arquivo
  diferente, ele é guardado como `vdd_settings.xml.nexscreen-backup` e volta na desinstalação.
- Roda `nexscreen-vdd-ctl install <app>\vdd\MttVDD.inf --hwid Root\MttVDD`. Se já existe um device `Root\MttVDD`
  (por exemplo, criado antes com o devcon), ele só atualiza o driver e não cria um segundo monitor.
- Na última tela: "Iniciar o NexScreen agora" (marcado) e "Iniciar com o Windows" (desmarcado). O segundo grava
  `HKCU\...\Run\NexScreen = "<exe>" --tray` para o usuário que abriu o instalador. É o mesmo formato do item da
  bandeja.
- A desinstalação fecha o companion e o adb da pasta e pergunta se deve remover o monitor virtual. Com Sim, roda
  `nexscreen-vdd-ctl uninstall --hwid Root\MttVDD`, que remove o device e apaga o `oemNN.inf` do driver store. Depois
  remove os arquivos, o atalho, o valor Run, `HKCU\Software\NexScreen` e a entrada do PATH.

Opções de linha de comando, além das do Inno (`/SILENT`, `/VERYSILENT`, `/LOG=...`):

- no setup, `/AUTOSTART` grava a inicialização com o Windows numa instalação silenciosa;
- no desinstalador, `/KEEPVDD` mantém o monitor virtual.

O `nexscreen-vdd-ctl` também serve à mão: `install [inf] [--hwid <id>]`, `uninstall [--hwid <id>] [--keep-driver]`
e `status [--hwid <id>]`. O padrão é `Root\NexScreenVDD`, o driver NexScreen. Os códigos de saída são 0 (ok),
1 (falhou), 2 (argumentos inválidos) e 3 (ok, mas precisa reiniciar).

### Testar na VM

Não rode o instalador numa máquina em uso. Na VM VirtualBox (o repositório aparece como `N:`), entre no Windows e
abra um PowerShell **como administrador**:

```powershell
powershell -ExecutionPolicy Bypass -File N:\tools\vm-test-installer.ps1             # instala, confere, reinstala, desinstala, confere
powershell -ExecutionPolicy Bypass -File N:\tools\vm-test-installer.ps1 -Extended   # + vdd_settings.xml existente e /KEEPVDD
```

O script usa o `NexScreen-Setup-v*.exe` mais novo de `N:\out\installer` e grava o resultado e os logs do Inno em
`C:\ProgramData\NexScreen\installer-test`. O código de saída é o número de falhas.

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
  trabalho", "Manter a tela do tablet estendida", "Parear tablet por Wi-Fi…", "Abrir pasta de logs", "Iniciar com o
  Windows", "Sair"). Uma instância por sessão; abrir de novo só mostra um aviso. "Iniciar com o Windows" grava
  `"<exe>" --tray` em `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` e só é ligado pelo usuário.
- Opções lembradas em `HKCU\Software\NexScreen`: `KeepExtended` (DWORD, padrão 1) é "Manter a tela do tablet
  estendida", gravado quando o item é clicado e lido na inicialização (o `--no-auto-extend` desliga só naquela
  execução); `VirtualMonitorRect` (`x,y,largura,altura`) é onde o monitor virtual estava estendido, para voltar ao
  mesmo lugar.
- "Parear tablet por Wi-Fi…": janela com o código de 6 dígitos e um IP:porta opcional. Se o IP:porta ficar vazio,
  ela procura o serviço `_adb-tls-pairing._tcp` em `adb mdns services`. Depois roda `adb pair <ip:porta>` com o
  código pela entrada padrão (nunca na linha de comando nem no log), fora da thread da interface e com timeout. Se
  der certo, o companion roda na hora a reconexão por mDNS (`adb connect`).
- O exe é do subsistema Windows (para o `--tray` não piscar um console). Nos outros modos ele se conecta ao console
  de quem o chamou, mas o shell não espera um programa desse tipo: use o `run-companion.ps1` (que espera) ou, no
  cmd, `start /wait NexScreen.Companion.exe --list`. Saída redirecionada (`> arquivo`, pipe) funciona normalmente.
- Ligação automática com o tablet (em todos os modos; `--no-adb` desliga): a cada ~2 s o companion roda
  `adb devices -l` e, para cada tablet autorizado que aparece ou reconecta (USB ou depuração sem fio), roda
  `adb -s <serial> reverse tcp:<porta> tcp:<porta>` e abre o app (`am start -n app.nexscreen/.MainActivity`). O
  mesmo tablet listado duas vezes (`ip:porta` e o nome mDNS `adb-..._adb-tls-connect._tcp`) é reconhecido pelo
  `ro.serialno` e ligado uma vez só (USB, depois `ip:porta`, depois mDNS). Sem nenhum tablet, ele roda
  `adb mdns services` (a cada 10 s, até 60 s) e faz `adb connect` nos serviços `_adb-tls-connect._tcp`, que só
  aceitam PCs já pareados; o pareamento nunca é automático. O adb é procurado em `--adb <caminho>`, em `platform-tools\` ao lado do exe (onde o instalador o põe), no PATH, em
  `%LOCALAPPDATA%\Android\Sdk\platform-tools` e no pacote WinGet `Google.PlatformTools`. Os comandos rodam sem
  janela, com timeout e com espera crescente quando falham.
- Monitor fora da área de trabalho: sem o monitor virtual o companion continua rodando e verifica a cada 2 s. Se o
  monitor NexScreen/VDD existe mas não está anexado, ou está duplicando outra tela (QueryDisplayConfig com
  `QDC_ALL_PATHS`), e há um tablet conectado, ele estende só esse monitor com `SetDisplayConfig`:
  - mantém os caminhos ativos como estão;
  - tira o caminho do monitor virtual que divide a origem com outro (Duplicar);
  - liga o monitor virtual numa origem livre, no modo preferido dele;
  - posiciona o monitor onde ele estava, se esse lugar continua livre e encostado em outra tela; senão, à direita da
    tela mais à direita, alinhado pelo topo.

  Primeiro valida com `SDC_VALIDATE`, depois aplica com
  `SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_ALLOW_CHANGES | SDC_SAVE_TO_DATABASE`. Se o Windows recusar,
  tenta de novo deixando o modo e a posição por conta dele e, por último, roda `DisplaySwitch.exe /extend`. O log
  diz qual caminho foi usado. Roda no máximo a cada 30 s e desiste depois de 3 tentativas, até o monitor voltar ou
  o tablet reconectar. `--no-auto-extend` desliga. Falhas de captura/encoder são repetidas com espera de 1 s até
  30 s.
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
