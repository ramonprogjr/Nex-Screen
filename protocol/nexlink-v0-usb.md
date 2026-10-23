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

## Vídeo

- H.264 em Annex-B, perfil High ou Main, sem B-frames, 1 slice por quadro de preferência.
- Codec config (SPS/PPS) separado em `CODEC_CONFIG`. No Android, ele é enfileirado com
  `BUFFER_FLAG_CODEC_CONFIG`.
- O servidor limita a 30 FPS no baseline e pode pular quadros quando a tela não muda (Desktop Duplication sem
  atualização).
