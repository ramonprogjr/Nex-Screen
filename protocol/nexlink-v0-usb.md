# NexLink v0-usb (transporte provisório do M2)

Protocolo mínimo para provar a imagem no tablet pelo cabo USB. **Não é o NexLink final** da seção 8 do
PROJETO.md: ele não tem TLS, pareamento, UDP nem recuperação por NACK. Esses entram no M3/M4. Ele só é seguro
porque a porta escuta apenas em `127.0.0.1` e o tablet chega até ela pelo `adb reverse`, que exige depuração USB
autorizada no aparelho.

## Conexão

- O companion (Windows) é o **servidor TCP** em `127.0.0.1:27183`.
- O app (Android) é o **cliente** e conecta em `127.0.0.1:27183` no próprio tablet.
- `adb reverse tcp:27183 tcp:27183` liga a porta do tablet à do notebook.
- Uma sessão por vez. Uma conexão nova derruba a anterior.
- `TCP_NODELAY` dos dois lados.

## Formato

Todos os inteiros são **big-endian**. Toda mensagem, nos dois sentidos, tem um cabeçalho fixo de 16 bytes:

| Offset | Tamanho | Campo | Descrição |
|---|---|---|---|
| 0 | 1 | `version` | Versão do protocolo. Sempre `0` aqui. |
| 1 | 1 | `type` | Tipo da mensagem (tabela abaixo). |
| 2 | 2 | `flags` | Bits por tipo. |
| 4 | 4 | `length` | Tamanho do payload em bytes (máximo 16 MiB). |
| 8 | 8 | `timestamp_us` | Relógio monotônico de quem envia, em µs. Nos quadros, é a hora da captura. |

Quem recebe `version` diferente de 0, `type` desconhecido ou `length` acima do limite fecha a conexão.

## Mensagens

### Cliente → servidor

| `type` | Nome | Payload |
|---|---|---|
| `0x01` | `HELLO` | `magic` 4 bytes `"NXS0"`, `u16 screen_width`, `u16 screen_height`, `u16 density_dpi`, `u16 rotation` (0/90/180/270) |
| `0x10` | `REQUEST_KEYFRAME` | vazio |
| `0x11` | `STATS` | `u32 frames_decoded`, `u32 frames_dropped`, `u32 decode_latency_us_p50`, `u32 decode_latency_us_p95` |
| `0x20` | `POINTER` | `u8 action`, `u8 pointer_id`, `u16 buttons`, `u16 x_norm`, `u16 y_norm` (8 bytes, ver [Entrada](#entrada-m3)) |
| `0x21` | `SCROLL` | `i16 dx`, `i16 dy` (4 bytes, ver [Entrada](#entrada-m3)) |

### Servidor → cliente

| `type` | Nome | Payload |
|---|---|---|
| `0x81` | `HELLO_ACK` | `magic` `"NXS0"`, `u16 video_width`, `u16 video_height`, `u16 fps`, `u8 codec` (1 = H.264), `u8 reserved` |
| `0x82` | `CODEC_CONFIG` | SPS + PPS em Annex-B (com start codes `00 00 00 01`) |
| `0x83` | `FRAME` | Uma access unit H.264 em Annex-B. `flags` bit 0 = keyframe (IDR). |

## Sequência

1. O cliente conecta e envia `HELLO`.
2. O servidor responde `HELLO_ACK`, depois `CODEC_CONFIG` e então os `FRAME`s, começando por um keyframe.
3. Sempre que o encoder mudar (resolução ou modo), o servidor envia de novo `HELLO_ACK` e `CODEC_CONFIG` antes do
   próximo keyframe. O cliente reconfigura o decoder.
4. O cliente envia `REQUEST_KEYFRAME` quando o decoder falhar ou depois de descartar quadros.
5. O cliente envia `STATS` a cada ~1 s. O servidor só registra no log.
6. Depois do `HELLO_ACK`, o cliente envia `POINTER` e `SCROLL` quando o usuário toca no vídeo (M3).

## Entrada (M3)

Tipos aditivos da versão 0, só do cliente para o servidor. O `timestamp_us` do cabeçalho é o relógio monotônico
do cliente em µs; o servidor só usa em log.

### `POINTER` (`0x20`, 8 bytes)

| Offset | Tamanho | Campo | Descrição |
|---|---|---|---|
| 0 | 1 | `action` | `0` down, `1` move, `2` up, `3` cancel. |
| 1 | 1 | `pointer_id` | Identificador do dedo no cliente (só informativo). |
| 2 | 2 | `buttons` | Estado dos botões **depois** deste evento: bit 0 esquerdo, bit 1 direito, bit 2 do meio. |
| 4 | 2 | `x_norm` | 0..65535 na largura do vídeo exibido (sem as faixas pretas). 0 = borda esquerda. |
| 6 | 2 | `y_norm` | 0..65535 na altura do vídeo exibido. 0 = borda de cima. |

- As coordenadas valem para o vídeo inteiro, que sempre mostra o monitor capturado inteiro. O servidor mapeia
  0 para o primeiro pixel do monitor e 65535 para o último.
- O servidor move o ponteiro para o ponto e aplica a diferença entre os botões que ele mantém pressionados e
  `buttons` (por exemplo `down` com `buttons=1` aperta o esquerdo, `up` com `buttons=0` solta).
- `cancel` solta todos os botões sem mover o ponteiro.

### `SCROLL` (`0x21`, 4 bytes)

| Offset | Tamanho | Campo | Descrição |
|---|---|---|---|
| 0 | 2 | `dx` | Roda horizontal, com sinal. Positivo rola para a direita (como `MOUSEEVENTF_HWHEEL`). |
| 2 | 2 | `dy` | Roda vertical, com sinal. Positivo rola para cima, como no Windows (`MOUSEEVENTF_WHEEL`). |

As unidades são as do Windows: 120 (`WHEEL_DELTA`) é um degrau da roda. Valores menores são rolagem fina.

### Regras do servidor

- Tamanho do payload exato (8 e 4 bytes), `action` até 3 e nenhum bit de botão desconhecido. Qualquer outra coisa
  é erro de protocolo e fecha a conexão.
- Entrada que chega antes de o servidor enviar `HELLO_ACK` + `CODEC_CONFIG` à sessão é descartada, assim como a
  entrada de uma sessão que já foi substituída.
- Quando a sessão termina (queda, substituição ou `cancel`), o servidor solta os botões que ainda estiverem
  pressionados.
- O companion com `--no-input` aceita as mensagens e as ignora.

### Gestos no tablet

O app decide o gesto e só manda o resultado. Nada é enviado quando o dedo encosta.

| Gesto | Mensagens |
|---|---|
| Toque rápido | `down` esquerdo + `up` no ponto do toque (clique esquerdo). |
| Arrastar um dedo (mais que o *touch slop*) | `down` esquerdo no ponto inicial, `move` com o esquerdo pressionado a no máximo 120 Hz, `up` no ponto final. |
| Segurar ~500 ms sem mover | `down` direito + `up` no ponto (clique direito). Depois disso o gesto termina. |
| Dois dedos | `move` sem botões para o meio dos dedos (o Windows rola a janela sob o ponteiro) e `SCROLL` a no máximo 120 Hz. O conteúdo acompanha os dedos, como numa tela de toque. |
| Três dedos, toque rápido | Nada é enviado. Mostra ou esconde o painel de estatísticas do app. |
| Toque que começa nas faixas pretas | Ignorado até todos os dedos saírem. |

## Vídeo

- H.264 em Annex-B, perfil High ou Main, sem B-frames, 1 slice por quadro de preferência.
- Codec config (SPS/PPS) separado em `CODEC_CONFIG`. No Android, ele é enfileirado com
  `BUFFER_FLAG_CODEC_CONFIG`.
- O servidor limita a 30 FPS no baseline e pode pular quadros quando a tela não muda (Desktop Duplication sem
  atualização).
