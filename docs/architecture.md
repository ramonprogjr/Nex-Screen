# Arquitetura

A visão geral está em [PROJETO.md](../PROJETO.md#3-arquitetura).

## Virtual Display Driver (`driver/virtual-display`)

Driver UMDF IddCx derivado da amostra `video/IndirectDisplay` (ver [THIRD_PARTY.md](../driver/virtual-display/THIRD_PARTY.md)).

- 1 monitor por adaptador, EDID próprio (`NXS`, "NexScreen", serial `NXS0001`, 237 × 148 mm).
- Modos: 1920×1200@60 (preferencial), 1920×1200@30, 1600×900@60 e 1600×900@30. A mesma tabela
  (`s_SampleDefaultModes`) alimenta os modos sem EDID e os modos de destino, e um `static_assert` garante que a
  lista do EDID tem o mesmo tamanho.
- O container ID é fixo (`{01A9D02A-B722-42A3-8DD5-18A033F50727}`), para que o Windows lembre a posição e a escala.
- O device de software é criado por `nexscreen-vdd-ctl` (`SwDeviceCreate`, enumerador `NexScreenVDD`).

### Assinatura (teste C1, 23/10/2026)

Na VM, com `testsigning off`, o driver assinado por um certificado não-Microsoft confiável (em Root e
TrustedPublisher) carregou normalmente e passou em `vm-m1-check.ps1 -Cycles 5`. Por ser UMDF, o driver não exige a
assinatura de kernel da Microsoft: basta uma assinatura Authenticode confiável, como a da SignPath Foundation ou de
um certificado OV.

### Pendências encontradas

- **O monitor some após reiniciar.** O device criado por `nexscreen-vdd-ctl create` (software device via
  `SwDeviceCreate`) fica "phantom" depois do boot. Solução: instalar como device root-enumerated
  (`Root\NexScreenVDD`, que já está no INF), que persiste como o do VDD, ou recriar o device ao iniciar.
- **O decoder do Tab A9+ vai até 1920×1088.** Acrescentar 1728×1080 (16:10) aos modos e ao EDID, como modo
  preferencial, para evitar a reescala no companion.

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
