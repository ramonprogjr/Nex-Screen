# Build e instalação do app Android

O app recebe o vídeo do companion pelo cabo USB (NexLink v0-usb, ver
[protocol/nexlink-v0-usb.md](../protocol/nexlink-v0-usb.md)) e mostra a imagem em tela cheia, decodificada pelo
`MediaCodec` direto numa `Surface`.

## Pré-requisitos

Instalar uma vez na máquina de build:

```powershell
winget install --id Microsoft.OpenJDK.17 -e        # JDK 17 para o Gradle
winget install --id Google.PlatformTools -e        # adb (opcional se usar o do SDK)
```

Android SDK sem o Android Studio:

1. Baixe o **Command line tools only** (Windows) em <https://developer.android.com/studio#command-tools>.
2. Extraia em `%LOCALAPPDATA%\Android\Sdk\cmdline-tools\latest` (a pasta `bin` fica dentro de `latest`).
3. Com o `JAVA_HOME` apontando para o JDK 17:

```powershell
$sdk = "$env:LOCALAPPDATA\Android\Sdk"
& "$sdk\cmdline-tools\latest\bin\sdkmanager.bat" --licenses
& "$sdk\cmdline-tools\latest\bin\sdkmanager.bat" "platform-tools" "platforms;android-35" "build-tools;35.0.0"
```

O Gradle vem pelo wrapper (`android\gradlew.bat`, Gradle 8.11.1). Versões: AGP 8.7.3, Kotlin 2.0.21, compileSdk
e targetSdk 35, minSdk 26 (ver `android\gradle\libs.versions.toml`). O `android\local.properties` (caminho do
SDK) é criado pelo script e não vai para o git.

## Preparar o Tab A9+

1. **Configurações → Sobre o tablet → Informações do software** e toque 7× em **Número de compilação**. Confirme
   com o PIN. Aparece "O modo de desenvolvedor foi ativado".
2. **Configurações → Opções do desenvolvedor** e ligue **Depuração USB**.
3. Conecte o cabo USB. No tablet, aceite **Permitir depuração USB?** (marque "Sempre permitir neste computador").
4. `adb devices` deve listar o aparelho como `device`. Se aparecer `unauthorized`, desbloqueie o tablet e aceite
   o aviso.

## Build e instalação

```powershell
.\tools\android-install.ps1              # gradlew assembleDebug + adb install -r + adb reverse tcp:27183 tcp:27183
.\tools\android-install.ps1 -Launch      # idem e já abre o app
.\tools\android-install.ps1 -SkipBuild   # só reinstala e refaz o adb reverse
```

O APK fica em `android\app\build\outputs\apk\debug\app-debug.apk`. Para compilar e testar sem o tablet:

```powershell
cd android
.\gradlew.bat assembleDebug test lintDebug
```

Os testes de `:core:network` rodam na JVM: cabeçalho, HELLO/STATS, rejeição de versão, tipo e tamanho inválidos,
leituras parciais e uma sessão completa contra um servidor TCP local.

## Uso

1. Rode o `android-install.ps1` (ou só `adb reverse tcp:27183 tcp:27183`) com o tablet conectado.
2. Abra o **NexScreen** no tablet. Ele mostra "Aguardando o NexScreen no notebook…" e tenta conectar sozinho.
3. Inicie o companion no Windows. A imagem aparece em alguns instantes.

Toque na tela para mostrar ou esconder o painel com resolução, FPS exibidos, latência de decodificação e quadros
descartados. O app pode girar livremente: o vídeo mantém a proporção, com faixas pretas quando necessário.

## Limite do decodificador do Tab A9+

O decodificador H.264 por hardware do Tab A9+ (`c2.qti.avc.decoder` e `c2.qti.avc.decoder.low_latency`, perfil
de mídia `blair`) aceita no máximo **1920×1088** (8160 macroblocos). Um stream **1920×1200 não inicia**: o
`MediaCodec.start()` falha e o app mostra que a resolução não é suportada. Use no companion uma resolução que
caiba nesse limite. **1728×1080** mantém o 16:10 e ocupa a tela inteira.

## Problemas comuns

| Sintoma | Causa e solução |
|---|---|
| `No Android device connected` | Cabo só de carga, depuração USB desligada ou driver USB ausente. Troque o cabo e confira `adb devices`. |
| `unauthorized` | O aviso de depuração não foi aceito. Desbloqueie o tablet e aceite, ou revogue as autorizações em Opções do desenvolvedor e reconecte. |
| Fica em "O notebook não respondeu" | O companion não está rodando ou o `adb reverse` se perdeu (cabo desconectado, `adb kill-server`). Rode `.\tools\android-install.ps1 -SkipBuild`. |
| "O decodificador de vídeo do tablet não aceita…" | Resolução acima do limite do hardware (ver acima). |
| `JDK 17 not found` | Instale o JDK 17 ou aponte o `JAVA_HOME` para ele. |
| `INSTALL_FAILED_UPDATE_INCOMPATIBLE` | Há uma versão assinada com outra chave. `adb uninstall app.nexscreen` e instale de novo. |

Logs do app: `adb logcat -s NexScreen NexScreenDecoder`.
