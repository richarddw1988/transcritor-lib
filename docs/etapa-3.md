# Etapa 3 — tempo real e captura

## Protocolo real do WhisperLiveKit (v0.2.27, lido no código)

Corrige o relatório da Etapa 0:
- Tempos chegam como `"H:MM:SS.cc"` (com centésimos), por linha; não há tempo por palavra no `/asr`.
- Sem diarização, as linhas são separadas por **pausas** (linhas de silêncio com `speaker: -2`), não por
  pontuação. A última linha **cresce** enquanto a pessoa fala; o texto já presente numa linha não muda.
- No modo `full` cada mensagem traz o estado completo (`lines` + `buffer_transcription`); o histórico não é
  podado nesse modo.

## Como os eventos são derivados (`DerivadorEventos`)

- **Confirmado**: cada frase fechada por `.`, `?` ou `!` dentro de uma linha, e o resto da linha quando ela
  termina (pausa, nova linha ou fim da sessão). Cada um recebe um `Id` novo.
- **Provisorio**: resto da linha aberta + `buffer_transcription`. Emitido só quando muda; vazio quando não há
  nada pendente.
- **Tradução**: o Confirmado sai na hora; quando a tradução fica pronta, o mesmo trecho é reemitido como
  Confirmado com o mesmo `Id` e `Traducao` preenchida. Contexto = os 2 trechos confirmados anteriores.
- Tempos de frases no meio de uma linha são estimados pela posição do texto na linha.

## Medido na máquina (RTX 3060 12 GB, WhisperLiveKit `medium`, SimulStreaming)

| | |
|---|---|
| Confirmado depois do fim da frase falada | ~2–3 s |
| Tradução depois do Confirmado (modelo carregado) | ~0,5–2,5 s |
| 1ª tradução com o Ollama frio | ~20 s |
| VRAM com WhisperLiveKit + Speaches + translategemma | 8,7 de 12 GB |

## Limites observados

- **Sem frases seguintes como contexto**: no tempo real só há as anteriores. "Hoje vamos falar sobre o Banco
  da Praça." (o modelo `medium` capitalizou) virou "Bank on the Square"; no arquivo, com as frases seguintes,
  virou "park bench".
- **Detecção de idioma "puxa para o inglês"** no começo da fala (o README do WhisperLiveKit avisa): no teste
  do microfone o início saiu como inglês e depois corrigiu para português. Para fala em português, use
  `--idioma pt` / `IniciarAsync(..., idioma: "pt")`.
- Som do sistema: o Windows não entrega áudio enquanto nada toca; o WhisperLiveKit só recebe trechos com som.
- O modelo `medium` erra mais nomes próprios que o `large-v3` do arquivo ("Linus Dvorvalds"); a tradução
  corrigiu para "Torvalds" nesse caso.

## Docker

A imagem do WhisperLiveKit tem 22,3 GB (PyTorch + CUDA). O disco do Docker foi movido para
`F:\Docker\wsl` (o C: tinha enchido durante o build); cópia das configurações anteriores em
`%APPDATA%\Docker\settings-store.json.bak-2026-11-20`.
