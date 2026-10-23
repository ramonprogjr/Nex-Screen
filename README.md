# NexScreen

Transforma um tablet Android (Galaxy Tab A9+) num monitor independente do Windows 11, usando um monitor virtual
IddCx de verdade.

- Plano, escopo e marcos: [PROJETO.md](PROJETO.md)
- Arquitetura: [docs/architecture.md](docs/architecture.md)
- Build e teste do driver: [docs/build-windows.md](docs/build-windows.md)

## Usar

1. Tablet no cabo USB, com a depuração USB autorizada e o app instalado (`.\tools\android-install.ps1`).
2. Um monitor virtual no modo **Estender** (Win+P). Até o driver NexScreen ser assinado, a ponte é o
   [Virtual Display Driver](https://github.com/VirtualDrivers/Virtual-Display-Driver), que é assinado.
3. `.\tools\start-nexscreen.ps1`: liga o USB, abre o app e transmite o monitor virtual. Ctrl+C encerra.

No tablet: toque = clique, arrastar = arrastar, segurar = botão direito, dois dedos = rolagem e três dedos = estatísticas.

**Estado:** M1 (prova do monitor virtual) aprovado numa VM. O Windows enumera o monitor "NexScreen", estende o
desktop, troca entre os 4 modos e sobrevive a 10 ciclos de create/remove. Próximo marco: M2 (transmissão para o
tablet).
