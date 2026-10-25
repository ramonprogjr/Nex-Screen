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

Antes, uma vez: o app no tablet (`.\tools\android-install.ps1`), a depuração USB autorizada e um monitor virtual.
Até o driver NexScreen ser assinado, a ponte é o
[Virtual Display Driver](https://github.com/VirtualDrivers/Virtual-Display-Driver), que é assinado.

1. Instale o companion uma vez: `.\tools\install-companion.ps1` (sem administrador; vai para
   `%LOCALAPPDATA%\Programs\NexScreen` e cria o atalho "NexScreen" no menu Iniciar).
2. Abra **NexScreen** no menu Iniciar. Ele fica no ícone perto do relógio, sem janela.
3. Ligue o tablet no cabo USB. O NexScreen faz o `adb reverse`, abre o app no tablet e começa a transmitir.

O ícone mostra o estado ("aguardando o tablet", "transmitindo para o tablet", "Tablet não autorizado — aceite a
depuração USB no tablet"...). No menu do ícone:

- **Manter a tela do tablet estendida** (ligado): se o monitor virtual sair da área de trabalho (por exemplo depois
  de reiniciar, ou com Win+P em "Somente na tela do PC") e houver um tablet conectado, o NexScreen volta a estender.
- **Iniciar com o Windows** (desligado): marque para o NexScreen abrir sozinho depois de reiniciar.
- **Estender a área de trabalho**, **Abrir pasta de logs** e **Sair**.

Sem cabo, a **depuração sem fio** do Android também serve: pareie o tablet uma vez com `adb pair <ip>:<porta>`
(o código aparece no tablet) e depois `adb connect <ip>:<porta>`. Daí em diante o NexScreen reconecta sozinho
quando a porta muda, e o menu mostra "Tablet conectado por Wi-Fi". O pareamento é sempre feito por você.

Para desenvolvimento, `.\tools\start-nexscreen.ps1` roda o companion neste console (Ctrl+C encerra).

No tablet: toque = clique, arrastar = arrastar, segurar = botão direito, dois dedos = rolagem e três dedos = estatísticas.

## Desinstalar / Uninstall

- **Android app:** desinstale "NexScreen" pelo tablet, ou rode `adb uninstall app.nexscreen`.
- **Companion:** `.\tools\install-companion.ps1 -Uninstall` fecha o programa e remove a pasta
  `%LOCALAPPDATA%\Programs\NexScreen`, o atalho do menu Iniciar e a inicialização com o Windows. Se você usou o zip
  de uma release, feche o programa e apague a pasta. Os logs ficam em `%LOCALAPPDATA%\NexScreen\logs`.
- **Driver NexScreen:** `tools\vm-uninstall-driver.ps1` (como administrador) remove o monitor e o pacote do driver.
- **Ponte VDD (terceiros):** `devcon remove Root\MttVDD` como administrador, depois apague `C:\VirtualDisplayDriver`.

## Estado

- **M1:** monitor virtual aprovado numa VM (enumeração, modo estendido, 4 modos, 10 ciclos de create/remove).
- **M2:** imagem no tablet via USB, 1728×1080 a ~30 FPS com encoder por hardware.
- **M3:** desktop estendido com cursor e toque como mouse.
- **Bandeja:** ícone perto do relógio, reconexão automática por USB e Wi-Fi e retorno automático ao modo Estender.
- **Próximos:** instalador único, rotação do tablet e assinatura própria (quando houver certificado).

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
