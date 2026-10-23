# NexScreen

O NexScreen transforma um Galaxy Tab A9+ (Android) num **monitor independente** do Windows 11. É um monitor virtual de verdade, via IddCx, e não uma janela de espelhamento. O usuário estende o desktop, arrasta janelas para o tablet e interage com elas por toque.

> Nome provisório. Todo o código-fonte é próprio, baseado na amostra oficial IddCx da Microsoft (MS-PL).

---

## 1. Escopo

**MVP (obrigatório)**
- Monitor virtual real no Windows 11 e desktop em modo estendido.
- Arrastar janelas entre o notebook e o tablet.
- Vídeo em tempo real no Android, em retrato e em paisagem.
- Mouse, clique, toque, rolagem e arraste.
- Pareamento seguro e reconexão.

**Segunda fase**
- USB (ADB ou canal próprio).
- Qualidade adaptativa.
- Inicialização automática e perfis de desempenho.
- Instalador e assinatura dos componentes.

**Fora do MVP**
- macOS, Linux e iOS.
- HDR, áudio e vários tablets ao mesmo tempo.
- Promessa de 60 FPS constantes sem medir.

## 2. Parâmetros iniciais (metas a validar)

| Parâmetro | Padrão | Alternativa |
|---|---|---|
| Resolução virtual | 1920 × 1200 | 1600 × 900 |
| Frequência | 30 FPS de transmissão (o monitor expõe 30 e 60 Hz) | 60 FPS |
| Codec | H.264 por hardware | AV1, só se justificar |
| Rede | Wi-Fi 5 GHz | Ethernet no notebook |
| Vídeo | UDP com recuperação (NACK/keyframe) | TCP |
| Controle | Canal TCP+TLS separado | — |
| Latência | p95 < 80 ms em Wi-Fi local (medida, não estimada) | — |

## 3. Arquitetura

```
Android (Kotlin · Compose · MediaCodec · toque)
        │  NexLink: sessão autenticada · vídeo · comandos · métricas
Windows Companion (C#/.NET · sessão do usuário · pareamento · rede · logs)
        ├── Capture & Encoder (WGC ou DXGI Desktop Duplication · D3D11 · H.264 MF)
        └── Input (injeção de mouse e toque nas coordenadas do monitor virtual)
Windows Service (apenas tarefas privilegiadas: instalar e gerenciar o driver)
Virtual Display Driver (C++ · WDK · UMDF · IddCx)
```

Regras:
- O driver, a transmissão e a interface ficam separados: uma falha de rede não pode derrubar o monitor.
- A **captura e a entrada ficam no companion, na sessão do usuário**, nunca na Session 0.
- O próprio Windows faz o arraste de janelas entre monitores. O NexScreen só transmite a imagem correta e encaminha a entrada usando coordenadas normalizadas.
- No Android, os quadros vão do MediaCodec direto para uma `Surface`, sem bitmap de CPU. O modo de baixa latência depende do fabricante.

## 4. Estrutura do monorepo

```
nexscreen/
├── PROJETO.md                 ← este arquivo
├── README.md · LICENSE · .gitignore · .editorconfig
├── docs/        architecture · protocol · security · build-windows · build-android · testing
├── android/     app/ · core/{network,video,input}/ · build.gradle.kts
├── windows/     companion/ · service/ · capture/ · encoder/ · input/ · installer/
├── driver/virtual-display/
│   ├── NexScreenVDD.sln
│   ├── src/     Driver.cpp · Driver.h · Trace.h · NexScreenVDD.rc · NexScreenVDD.vcxproj
│   ├── inf/     NexScreenVDD.inf
│   ├── tools/   nexscreen-vdd-ctl/ (cria/remove o device) · gen_edid.py
│   ├── tests/
│   └── THIRD_PARTY.md         (MS-PL + commit da amostra)
├── protocol/    especificação NexLink versionada
└── tools/       scripts PowerShell de build, assinatura, VM e Android
```

**Estado atual desta pasta:** as adaptações das seções 6.1 a 6.5 já foram aplicadas a `driver/virtual-display/` e a `tools/`, **mas ainda não foram compiladas nem testadas**. Ainda falta o `LICENSE` da raiz.
- Commit da amostra: `microsoft/Windows-driver-samples@bb68ed5e770bce04dc5395ec9258dafab034184d`, pasta `video/IndirectDisplay`.

## 5. Preparar a máquina de desenvolvimento

O build pode ser feito em qualquer Windows 11 x64. A instalação do driver de teste deve acontecer numa **VM Hyper-V** ou numa máquina descartável, nunca no notebook de uso diário.

Instalar via winget, com o terminal como administrador:

```powershell
winget install --id Microsoft.VisualStudio.2022.Community -e --override "--wait --passive --norestart --add Microsoft.VisualStudio.Workload.NativeDesktop --add Microsoft.VisualStudio.Component.VC.Runtimes.x86.x64.Spectre --add Microsoft.VisualStudio.Component.VC.ATL.Spectre --includeRecommended"
winget install --id Microsoft.WindowsSDK.10.0.26100 -e
winget install --id Microsoft.WindowsWDK.10.0.26100 -e
winget install --id Microsoft.DotNet.SDK.8 -e
winget install --id Google.PlatformTools -e      # adb, para o M2
```

- O SDK e o WDK precisam ter a **mesma versão** (10.0.26100).
- Depois, no Visual Installer → *Individual components*, confirme que o componente **Windows Driver Kit** (extensão do VS) está marcado.
- O Android Studio fica para o M2.
- Para a VM: `Enable-WindowsOptionalFeature -Online -FeatureName Microsoft-Hyper-V-All`, que exige reiniciar. Depois crie uma VM **Gen 2** com **Secure Boot desligado** a partir de uma ISO do Windows 11.
- **O Windows 11 Home não tem Hyper-V.** Nele, use VirtualBox ou VMware Workstation (EFI, Secure Boot desligado) ou uma máquina descartável.

> Na tentativa nesta máquina, o instalador do VS saiu com o código **1602**, que significa cancelado pelo usuário/UAC. Na outra máquina, aceite o UAC quando ele aparecer.

## 6. M1: prova do monitor virtual (primeiro marco)

**Objetivo:** o Windows deve enumerar um monitor "NexScreen" e o modo "Estender estes monitores" deve funcionar.

### 6.1 Adaptações no driver (`src/Driver.cpp`, `Driver.h`, `Trace.h`)

Adaptações mínimas, sem reescrever a lógica da amostra:

| Item | Amostra | NexScreen |
|---|---|---|
| Monitores por adaptador | `IDD_SAMPLE_MONITOR_COUNT = 3` | **1** |
| EDID | Dell / Lenovo | EDID próprio: fabricante `NXS`, nome `NexScreen`, serial `NXS0001`, 237 × 148 mm, DTD 1920×1200@60 (CVT-RB, 154 MHz). Gerado por `tools/gen_edid.py`, que calcula o checksum. |
| Lista de modos (`szModeList`) | 3 modos | **4**: 1920×1200@60 (preferencial), 1920×1200@30, 1600×900@60, 1600×900@30 |
| `s_SampleDefaultModes` e `IddSampleMonitorQueryModes` | 1080p, 1440p, 4K… | os mesmos 4 modos acima |
| `MonitorType` | `HDMI` | `DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INDIRECT_WIRED` |
| Container ID | `CoCreateGuid` aleatório | GUID **fixo**, para o Windows lembrar a disposição e a escala |
| Strings de telemetria | "IddSample…/Microsoft" | "NexScreen Virtual Display" / "NexScreen" |
| Trace GUID (`Trace.h`) | `b254994f-…` | `202d3a1e-92cd-4190-9b01-85a4e9bb8f1d` |
| `MYDRIVER_TRACING_ID` | `…\IddSampleDriver v1.0` | `NexScreen\\NexScreenVDD v0.1` |

Detalhes do DTD 1920×1200@60 (CVT-RB):
- Horizontal: ativo 1920, blank 160, front porch 48, sync 32, polaridade H+.
- Vertical: ativo 1200, blank 35, front porch 3, sync 6, polaridade V−.

O monitor expõe 60 Hz, mas o companion limita a transmissão a 30 FPS no baseline.

### 6.2 INF (`inf/NexScreenVDD.inf`)

- `CatalogFile=NexScreenVDD.cat`, com `DriverVer` preenchido pelo stampinf.
- Hardware IDs: `Root\NexScreenVDD` e `NexScreenVDD`.
- `DeviceGroupId = "NexScreenVDDGroup"`: o driver roda num processo UMDF dedicado, o que aumenta a confiabilidade.
- `UmdfService=NexScreenVDD`, `ServiceBinary=%12%\UMDF\NexScreenVDD.dll`, `UmdfExtensions = IddCx0102`.
- Strings: Manufacturer `NexScreen`, DeviceName `NexScreen Virtual Display`.

### 6.3 Projeto e solução

- `NexScreenVDD.vcxproj`:
  - `RootNamespace`/`TargetName` passam a ser `NexScreenVDD`;
  - o item `<Inf Include="..\inf\NexScreenVDD.inf" />`;
  - um novo `ProjectGuid`, `{37B23982-DCFA-4876-BCBA-22BEFF17F10D}`;
  - Spectre ligado. Mantém IddCx 1.4 como mínimo e compila com 1.6, como na amostra.
- `nexscreen-vdd-ctl.vcxproj`: o `ProjectGuid` passa a ser `{385AEF2B-B273-4CC3-9B2B-1AD321B0A7D8}`.
- Atualize as referências e os GUIDs no `NexScreenVDD.sln`.
- Só x64; o ARM64 pode ser removido.

### 6.4 Ferramenta `nexscreen-vdd-ctl` (adaptação do IddSampleApp)

O device de software é criado com `SwDeviceCreate(L"NexScreenVDD", L"HTREE\\ROOT\\0", …)`, com instanceId, hardwareIds e compatibleIds iguais a `NexScreenVDD`.

| Comando | Comportamento |
|---|---|
| `run` | Igual à amostra: cria o device, que é removido ao apertar `x` ou ao fechar o processo. |
| `create` | Cria o device e chama `SwDeviceSetLifetime(h, SWDeviceLifetimeParentPresent)`, para que o monitor continue existindo depois que o processo termina. |
| `remove` | Chama `SwDeviceCreate` de novo com o mesmo ID, o que reabre o device existente, depois `SwDeviceSetLifetime(h, SWDeviceLifetimeHandle)` e `SwDeviceClose(h)`. O monitor é removido. |

Retorne um código de saída diferente de zero em caso de falha e verifique `hrCreateResult` no callback. A amostra ignora esse valor.

### 6.5 Scripts em `tools/`

- `build-driver.ps1`: localiza o msbuild com `vswhere` e roda `msbuild NexScreenVDD.sln /p:Configuration=Release /p:Platform=x64`. A saída vai para `out/driver/` (.dll, .inf, .cat e o ctl .exe).
- `make-test-cert.ps1`:
  1. `New-SelfSignedCertificate -Type CodeSigningCert -Subject "CN=NexScreen Test"`;
  2. exporta o `.cer`;
  3. `inf2cat /driver:out\driver /os:10_x64`;
  4. `signtool sign /fd sha256` no .dll e no .cat.
- `vm-install-driver.ps1` (roda **dentro da VM**, como administrador):
  1. importa o .cer em `LocalMachine\Root` e `TrustedPublisher`;
  2. aplica `bcdedit /set testsigning on` e reinicia;
  3. `pnputil /add-driver NexScreenVDD.inf /install`;
  4. `nexscreen-vdd-ctl create`.
- `vm-uninstall-driver.ps1`: `nexscreen-vdd-ctl remove`, depois `pnputil /delete-driver oemXX.inf /uninstall` (descobre o nome com `pnputil /enum-drivers`), e opcionalmente `bcdedit /set testsigning off`.
- `new-test-vm.ps1`: `New-VM -Generation 2`, `Set-VMFirmware -EnableSecureBoot Off` e a ISO informada. Copia o `out/` com `Copy-VMFile` ou por uma pasta compartilhada.

### 6.6 Checklist de aceite do M1

1. [ ] `build-driver.ps1` compila sem erros.
2. [ ] Na VM, `pnputil /enum-devices /class Display` lista o **NexScreen Virtual Display** sem erro (Problem code 0).
3. [ ] **Configurações → Sistema → Tela** mostra um segundo monitor "NexScreen".
4. [ ] "Estender estes monitores" funciona e uma janela arrastada para o monitor 2 sai da tela principal (teste também com `Win+Shift+→`).
5. [ ] Trocar para 1600×900 e para 30/60 Hz aplica o modo sem erro.
6. [ ] `nexscreen-vdd-ctl remove`: o monitor some, as janelas voltam para a tela principal e o desktop continua utilizável.
7. [ ] 10 ciclos de `create`/`remove` sem travar o dwm nem gerar erro no Event Viewer (Applications and Services → Microsoft → Windows → DriverFrameworks-UserMode).

> A VM prova a enumeração e o ciclo de vida, **não** o desempenho nem a GPU real. Só depois do M1 aprovado vale decidir se o driver vai para o notebook real, com test signing.

### 6.7 Pontos que a amostra deixa incompletos (registrar em `docs/architecture.md`)

| Ponto | Marco |
|---|---|
| Processamento de quadros em `SwapChainProcessor::RunCore` (hoje descarta os quadros) | M2 |
| `IddCxSwapChainReportFrameStatistics` | M4 |
| `AdapterCommitModes` ignorando paths inativos | M3 |
| Monitor criado em `FinishInit` em vez de num evento de conexão real: deve ir para a chegada da sessão NexLink | M3 |
| Hotplug sem reiniciar o adapter | M3 |
| HDR e gamma | fora do MVP |
| Assinatura de produção (atestação no Partner Center) em vez de test signing | M5 |

## 7. Marcos

| Marco | Estimativa | Entrega |
|---|---|---|
| **M1** Monitor virtual | 1–3 dias | Monitor visível em Configurações → Tela |
| **M2** Transmissão básica | 3–6 dias | Imagem do monitor virtual exibida no Tab A9+ |
| **M3** Extensão e interação | 3–6 dias | Mover uma janela para o tablet e interagir com ela |
| **M4** Qualidade e estabilidade | 3–5 dias | MVP Wi-Fi para uso diário |
| **M5** USB e empacotamento | depois | Versão candidata a distribuição |

São estimativas de um desenvolvedor, não prazos. O maior risco é o driver, por isso o M1 vem primeiro.

### M2 em resumo: captura, encoder e app

- **Companion (C#/.NET 8):** localiza o monitor NexScreen (pelo nome do EDID, via `EnumDisplayDevices`/`DisplayConfig`).
- **Captura:** WGC ou DXGI Desktop Duplication daquela saída, com textura D3D11 e sem cópia para a CPU.
- **Encoder:** H.264 por hardware no Media Foundation (NVENC, QSV ou AMF por trás do MFT), em low latency, 30 FPS, GOP longo e keyframe sob demanda.
- **Android:** `MediaCodec` (`video/avc`) direto na `Surface` de um `SurfaceView`, com `KEY_LOW_LATENCY` quando o aparelho suportar.
- **Instalação no Tab A9+ por depuração USB:** ative as Opções do desenvolvedor (tocar 7× em *Número de compilação*) e a Depuração USB. Depois rode `tools/android-install.ps1`, que executa `gradlew assembleDebug` e `adb install -r`.
- A mesma conexão USB permite testar cedo o transporte com `adb reverse tcp:<porta> tcp:<porta>`.

## 8. Protocolo NexLink v0

| Canal | Tecnologia | Dados |
|---|---|---|
| Descoberta | mDNS ou QR Code | Encontrar o notebook |
| Pareamento | Código temporário + chave de sessão | Autenticação mútua |
| Controle | TCP + TLS, mensagens versionadas | Conexão, resolução, orientação, estado, entrada, métricas |
| Vídeo | UDP: cabeçalho com sessão, seq, frame id, timestamp e flags; payload H.264 fragmentado | Quadros |
| Recuperação | Feedback de perda e pedido de keyframe | Retomar após falhas |

- O receptor descarta quadros atrasados.
- Toda mensagem leva `protocol_version`.
- **Sem criptografia própria:** TLS no controle e AEAD (por exemplo AES-GCM) no vídeo, com chaves derivadas da sessão TLS.
- Também entram rotação de chaves, expiração do pareamento e contadores contra repetição de comandos.

Abstração de transporte, para que o Android não saiba se o canal é Wi-Fi ou USB:

```kotlin
interface DisplayTransport {
    suspend fun connect()
    suspend fun sendControl(message: ByteArray)
    suspend fun receiveVideo(): VideoPacket?
    suspend fun disconnect()
}
```

O equivalente em C# fica no companion.

## 9. Segurança e distribuição

- O driver precisa de assinatura de produção. Não dependa de deixar o Windows em modo de teste.
- Instalador com verificação de pré-requisitos, rollback e desinstalação limpa.
- Pareamento explícito, com a possibilidade de revogar dispositivos.
- Nada é transmitido para a internet por padrão.
- Os logs não guardam senhas, tokens nem imagens do desktop.
- Tratar conteúdo protegido, bloqueio de tela e perda de sessão.

## 10. Critérios de aceite do produto (MVP)

| Teste | Critério |
|---|---|
| Detecção | O Windows enumera o tablet como monitor independente |
| Estendido | "Estender" funciona sem duplicação |
| Arraste | Uma janela atravessa a borda e aparece no tablet |
| Interação | Mouse, clique direito, rolagem e arraste funcionam nas coordenadas certas |
| Resolução | Trocar o modo não distorce nem congela a imagem |
| Orientação | Girar o tablet reorganiza a exibição sem reinstalar nada |
| Latência | p95 < 80 ms em Wi-Fi controlado (timestamps de captura, envio, decodificação e render, mais câmera de alta velocidade) |
| Fluidez | 30 FPS sustentados; 60 FPS como meta |
| Reconexão | A sessão volta após uma queda breve sem reiniciar o Windows |
| Segurança | Um dispositivo não pareado não recebe a tela nem envia comandos |
| Estabilidade | 2 h contínuas sem crash, sem vazamento crescente e sem congelamento |
| Recuperação | Desconectar o tablet não inutiliza o desktop principal |

Testar também: sinal Wi-Fi variável, troca de rede, suspensão, bloqueio e desbloqueio, e mudança de escala do Windows.

## 11. Referências

- Indirect Display Driver Model: https://learn.microsoft.com/windows-hardware/drivers/display/indirect-display-driver-model-overview
- Amostra IddCx: https://github.com/microsoft/Windows-driver-samples/tree/main/video/IndirectDisplay
- DXGI Desktop Duplication: https://learn.microsoft.com/windows/win32/direct3ddxgi/desktop-dup-api
- Windows Graphics Capture: https://learn.microsoft.com/windows/uwp/audio-video-camera/screen-capture
- Software devices (`SwDeviceCreate`): https://learn.microsoft.com/windows/win32/api/swdevice/
- Android MediaCodec: https://developer.android.com/reference/android/media/MediaCodec

## 12. Próximo passo

Na máquina nova:
1. Copie esta pasta.
2. Rode os `winget` da seção 5.
3. Aplique as adaptações da seção 6.1 a 6.4.
4. Compile.
5. Valide o checklist 6.6 na VM.

Não comece pelo app nem pelo USB: primeiro prove o monitor virtual.
