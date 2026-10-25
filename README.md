# NexScreen

**NexScreen turns an Android tablet into a real extended monitor for Windows 10/11.** A Windows indirect display
driver (IddCx) creates a virtual monitor. The companion app captures that monitor, encodes it with hardware H.264 and
streams it to the Android app over USB. Touch on the tablet is sent back as mouse input.

O NexScreen transforma um tablet Android (testado no Galaxy Tab A9+) num **monitor estendido de verdade** do
Windows: você arrasta janelas para o tablet e usa por toque.

| Component | Path | Language |
|---|---|---|
| Virtual display driver (UMDF/IddCx) | [driver/virtual-display](driver/virtual-display) | C++ (WDK) |
| Windows companion (capture, encode, input) | [windows/companion](windows/companion) | C# / .NET 8 |
| Android receiver app | [android](android) | Kotlin |
| Protocol | [protocol/nexlink-v0-usb.md](protocol/nexlink-v0-usb.md) | — |

- Plano, escopo e marcos: [PROJETO.md](PROJETO.md)
- Arquitetura: [docs/architecture.md](docs/architecture.md)
- Build do Windows (driver e companion): [docs/build-windows.md](docs/build-windows.md)
- Build do Android: [docs/build-android.md](docs/build-android.md)

## Usar

O jeito mais simples é o instalador:

1. Baixe o `NexScreen-Setup-vX.Y.Z.exe` da [última release](https://github.com/ramonprogjr/Nex-Screen/releases).
2. Abra o arquivo. Como o instalador ainda não é assinado, o Windows pode mostrar a tela azul "O Windows protegeu o
   computador" (SmartScreen). Clique em **Mais informações** e depois em **Executar assim mesmo**.
3. Avance até o fim e aceite o pedido de administrador. É a única vez que ele é necessário. O instalador coloca:
   - o NexScreen em `C:\Program Files\NexScreen`, com o atalho "NexScreen" no menu Iniciar;
   - o adb (Android SDK Platform-Tools);
   - o monitor virtual. Até o driver NexScreen ser assinado, o monitor virtual é o
     [Virtual Display Driver](https://github.com/VirtualDrivers/Virtual-Display-Driver), que é assinado e já vem
     configurado para 1728×1080.

   Na última tela, "Iniciar o NexScreen agora" já vem marcado. Marque "Iniciar com o Windows" se quiser que ele
   abra sozinho.
4. No tablet, instale o app NexScreen (o `.apk` da mesma release) e ative a depuração USB nas opções do
   desenvolvedor.
5. Ligue o tablet no cabo USB e aceite a depuração no tablet. O NexScreen faz o `adb reverse`, abre o app no tablet
   e começa a transmitir. O ícone fica perto do relógio, sem janela.

Sem o instalador (desenvolvimento): o app no tablet com `.\tools\android-install.ps1`, um monitor virtual instalado
à parte e o companion com `.\tools\install-companion.ps1` (sem administrador; vai para
`%LOCALAPPDATA%\Programs\NexScreen` e cria o atalho "NexScreen" no menu Iniciar).

O ícone mostra o estado ("aguardando o tablet", "transmitindo para o tablet", "Tablet não autorizado — aceite a
depuração USB no tablet"...). No menu do ícone:

- **Manter a tela do tablet estendida** (ligado): se o monitor virtual sair da área de trabalho ou ficar duplicando
  outra tela (por exemplo depois de reiniciar, ou com Win+P em "Somente na tela do PC" ou "Duplicar") e houver um
  tablet conectado, o NexScreen volta a estender só a tela do tablet, no lugar onde ela estava (ou à direita das
  outras telas). Os outros monitores não mudam. A escolha fica gravada e continua valendo depois de reiniciar.
- **Parear tablet por Wi-Fi…**: pareia o tablet sem terminal. No tablet, abra Configurações → Opções do
  desenvolvedor → Depuração por Wi-Fi → Parear dispositivo com código de pareamento e digite na janela o código de
  6 dígitos. O IP:porta é opcional; se ficar vazio, o NexScreen encontra o tablet na rede.
- **Iniciar com o Windows** (desligado): marque para o NexScreen abrir sozinho depois de reiniciar.
- **Estender a área de trabalho**, **Abrir pasta de logs** e **Sair**.

Sem cabo, a **depuração sem fio** do Android também serve: pareie o tablet uma vez em **Parear tablet por Wi-Fi…**
(ou no terminal, com `adb pair <ip>:<porta>`). Daí em diante o NexScreen conecta e reconecta sozinho quando a porta
muda, e o menu mostra "Tablet conectado por Wi-Fi". O pareamento é sempre feito por você.

Para desenvolvimento, `.\tools\start-nexscreen.ps1` roda o companion neste console (Ctrl+C encerra).

No tablet: toque = clique, arrastar = arrastar, segurar = botão direito, dois dedos = rolagem e três dedos = estatísticas.

## Desinstalar / Uninstall

- **Instalado pelo NexScreen-Setup:** em Configurações > Aplicativos (ou "Adicionar ou remover programas"),
  desinstale **NexScreen**. O desinstalador fecha o programa e remove a pasta `C:\Program Files\NexScreen` (com o
  adb), o atalho, a inicialização com o Windows, as opções em `HKCU\Software\NexScreen` e o adb do PATH. Ele
  pergunta "Remover também o monitor virtual?" (padrão: Sim). Com Sim, remove o monitor virtual, o pacote do driver
  e o `C:\VirtualDisplayDriver\vdd_settings.xml`; se havia um arquivo seu antes, ele é restaurado. Responda Não se
  você usa o Virtual Display Driver com outro programa. Os logs ficam em `%LOCALAPPDATA%\NexScreen\logs`.
  Sem perguntas: `"C:\Program Files\NexScreen\unins000.exe" /VERYSILENT` (acrescente `/KEEPVDD` para manter o
  monitor virtual).
- **Android app:** desinstale "NexScreen" pelo tablet, ou rode `adb uninstall app.nexscreen`.
- **Companion instalado pelo script:** `.\tools\install-companion.ps1 -Uninstall` fecha o programa e remove a pasta
  `%LOCALAPPDATA%\Programs\NexScreen`, o atalho do menu Iniciar e a inicialização com o Windows. Se você usou o zip
  de uma release, feche o programa e apague a pasta. Os logs ficam em `%LOCALAPPDATA%\NexScreen\logs`.
- **Driver NexScreen:** `tools\vm-uninstall-driver.ps1` (como administrador) remove o monitor e o pacote do driver.
- **VDD instalado à mão (sem o NexScreen-Setup):** `devcon remove Root\MttVDD` como administrador, depois apague
  `C:\VirtualDisplayDriver`.

## Estado

- **M1:** monitor virtual aprovado numa VM (enumeração, modo estendido, 4 modos, 10 ciclos de create/remove).
- **M2:** imagem no tablet via USB, 1728×1080 a ~30 FPS com encoder por hardware.
- **M3:** desktop estendido com cursor e toque como mouse.
- **Bandeja:** ícone perto do relógio, reconexão automática por USB e Wi-Fi e retorno automático ao modo Estender.
- **Instalador:** `NexScreen-Setup` instala o companion, o adb e o monitor virtual de uma vez (ainda não assinado).
- **Próximos:** rotação do tablet e assinatura própria (quando houver certificado).

## Code signing

All release binaries are built from this repository by GitHub Actions. **They are not code-signed yet**, so Windows
SmartScreen may warn before running them ("More info" > "Run anyway").

The virtual monitor used by NexScreen is the [Virtual Display Driver](https://github.com/VirtualDrivers/Virtual-Display-Driver)
(MIT), which is signed by its own project, so it installs with Secure Boot on and without test mode. NexScreen's own
driver in [driver/virtual-display](driver/virtual-display) is not signed and is only used for development and testing.

Os binários dos releases são compilados pelo GitHub Actions a partir deste repositório e **ainda não são assinados**:
o Windows pode mostrar o aviso do SmartScreen ("Mais informações" > "Executar assim mesmo"). O monitor virtual usado é
o Virtual Display Driver, que é assinado pelo próprio projeto.

Maintainer: [Ramon Pereira (@ramonprogjr)](https://github.com/ramonprogjr). Changes from anyone else arrive as pull
requests and are reviewed before merging.

## Privacy

This program will not transfer any information to other networked systems unless specifically requested by the
user or the person installing or operating it.

The companion only listens on `127.0.0.1` and talks to the NexScreen app on the user's own tablet through `adb reverse`,
over a USB cable or Android wireless debugging on the local network (paired by the user). It sends no telemetry and
contacts no internet service. Logs stay on the local machine and never contain screen images.

Este programa não envia nenhuma informação a outros sistemas em rede, a menos que o usuário peça. Não há telemetria,
e a imagem da tela vai apenas para o tablet do próprio usuário, pelo cabo USB ou pela depuração sem fio do Android na
rede local, pareada por você.

## License

[MIT](LICENSE). Files under `driver/virtual-display/` derive from the Microsoft IndirectDisplay sample and are under
MS-PL (see [THIRD_PARTY.md](driver/virtual-display/THIRD_PARTY.md)).

The installer also ships, unmodified, the [Virtual Display Driver](https://github.com/VirtualDrivers/Virtual-Display-Driver)
25.7.23 (MIT) and the Android SDK Platform-Tools (adb, Apache 2.0, with its NOTICE). See
[THIRD_PARTY_NOTICES.txt](windows/installer/THIRD_PARTY_NOTICES.txt).
