# Arquitetura

A visão geral está em [PROJETO.md](../PROJETO.md#3-arquitetura).

## Virtual Display Driver (`driver/virtual-display`)

Driver UMDF IddCx derivado da amostra `video/IndirectDisplay` (ver [THIRD_PARTY.md](../driver/virtual-display/THIRD_PARTY.md)).

- 1 monitor por adaptador, EDID próprio (`NXS`, "NexScreen", serial `NXS0001`, 237 × 148 mm).
- Modos: 1728×1080 (preferencial; cabe no decoder do Tab A9+), 1920×1200 e 1600×900, cada um a 60 e 30 Hz. A mesma tabela
  (`s_SampleDefaultModes`) alimenta os modos sem EDID e os modos de destino, e um `static_assert` garante que a
  lista do EDID tem o mesmo tamanho.
- O container ID é fixo (`{01A9D02A-B722-42A3-8DD5-18A033F50727}`), para que o Windows lembre a posição e a escala.
- O device é criado por `nexscreen-vdd-ctl install`: root-enumerated (`Root\NexScreenVDD`) e persistente. Para
  testes rápidos, `create`/`remove` criam um software device (`SwDeviceCreate`, enumerador `NexScreenVDD`).

### Assinatura (teste C1, 23/10/2026)

Na VM, com `testsigning off`, o driver assinado por um certificado não-Microsoft confiável (em Root e
TrustedPublisher) carregou normalmente e passou em `vm-m1-check.ps1 -Cycles 5`. Por ser UMDF, o driver não exige a
assinatura de kernel da Microsoft: basta uma assinatura Authenticode confiável, como a da SignPath Foundation ou de
um certificado OV.

### Pendências encontradas (resolvidas)

- **O monitor sumia após reiniciar.** O software device de `nexscreen-vdd-ctl create` (`SwDeviceCreate`) fica
  "phantom" depois do boot. Agora `nexscreen-vdd-ctl install` cria um device root-enumerated (`Root\NexScreenVDD`,
  via SetupAPI, como o `devcon install`), que persiste. Validado: depois de reiniciar a VM sem test signing, o monitor
  volta e `vm-m1-check.ps1 -Cycles 5` passa. `create`/`remove` ficam só para testes rápidos.
- **O decoder do Tab A9+ vai até 1920×1088.** O 1728×1080 (16:10) virou o modo preferencial e o DTD do EDID
  (CVT-RB, 125,75 MHz). Os modos agora são 1728×1080, 1920×1200 e 1600×900, cada um a 60 e 30 Hz.

### Pontos que a amostra deixa incompletos

| Ponto | Onde | Marco |
|---|---|---|
| Processamento de quadros: hoje `SwapChainProcessor::RunCore` descarta os quadros | `Driver.cpp`, `RunCore` | M2 |
| `IddCxSwapChainReportFrameStatistics` | `Driver.cpp`, `RunCore` | M4 |
| `AdapterCommitModes` ignora paths inativos | `IddSampleAdapterCommitModes` | M3 |
| O monitor é criado em `FinishInit`, não num evento real de conexão; deve passar para a chegada da sessão NexLink | `IddSampleAdapterInitFinished` / `FinishInit` | M3 |
| Hotplug sem reiniciar o adapter | `IndirectDeviceContext` | M3 |
| HDR e gamma | `InitAdapter` (`GammaSupport = NONE`) | fora do MVP |
| Assinatura de produção (atestação no Partner Center) em vez de test signing | `tools/make-test-cert.ps1` | M5 |
