# Etapa 4 — unidades de sentido e "o que quis dizer"

Objetivo: gerar os dados para que o app que usa a lib (ex.: um player, em outro projeto) mostre não só a palavra
traduzida, mas a expressão de que ela faz parte e o que a pessoa quis dizer com a frase. A lib não tem interface.

## O que vai no JSON (opcional, com `AnalisarSentido = true` ou `transcritor arquivo --sentido`)

```json
{
  "texto": "Let's call it a day, we are running out of time.",
  "traducao": "Vamos encerrar por hoje, estamos ficando sem tempo.",
  "palavras": [ { "texto": "Let's", "inicio": 0.0, "fim": 0.3 }, "..." ],
  "sentido": "É hora de parar o que estão fazendo, porque o tempo está acabando.",
  "blocos": [
    { "palavraInicial": 0, "palavraFinal": 4, "texto": "Let's call it a day,",
      "traducao": "Vamos encerrar por hoje", "tipo": "expressao",
      "nota": "Expressão idiomática: vamos parar por hoje." },
    { "palavraInicial": 7, "palavraFinal": 10, "texto": "running out of time.",
      "traducao": "ficando sem tempo", "tipo": "phrasal" }
  ]
}
```

- `palavraInicial`/`palavraFinal` são índices em `palavras` (inclusive): o app sabe a que bloco cada palavra
  pertence e, pelo tempo das palavras, quando o bloco é falado.
- `traducao` do bloco é copiada da tradução da frase; quando aparece literalmente nela, o app pode destacar o
  trecho correspondente na legenda traduzida.
- `tipo`: `literal`, `expressao`, `phrasal`, `colocacao`, `nome`. `nota` só aparece quando o literal engana.
- Sem a análise, `sentido` e `blocos` não aparecem no JSON.

## Como funciona

1. A frase já está transcrita e traduzida (etapas 1 e 2).
2. `IAnalisadorSentido` (implementação: `AnalisadorOllama`) manda ao modelo a frase, a tradução, as 2 frases
   anteriores e a seguinte, e pede em JSON Schema: os blocos (texto copiado da frase + trecho copiado da
   tradução + tipo + nota) e o sentido.
3. O modelo copia o texto do bloco em vez de dar índices (contar palavras ele erra). `AlinhadorBlocos` procura
   cada bloco nas palavras, em ordem, ignorando caixa, acento e pontuação; bloco não encontrado é descartado.
4. Resposta inválida deixa só aquela frase sem análise; o resto do arquivo segue.
5. Cache próprio (chave da tradução + modelo + versão do prompt): repetir não chama o Ollama.

## Escolha do modelo (teste com 10 frases do audiolivro + 3 expressões idiomáticas)

| | gemma3:12b | gemma3:4b |
|---|---|---|
| Tempo por frase | 13,4 s (máx. 24 s) | 6,4 s (máx. 14 s) |
| Bloco achado na frase | 72/72 | 65/66 |
| Tradução do bloco achada na tradução da frase | 72/72 | 59/66 |
| Palavras cobertas | 294/294 | 287/294 |
| Expressões ("call it a day", "break a leg", "under his skin") | certas, com nota | certas, quase sem nota |
| Tipos | coerentes | marca frases inteiras como "expressão"; "would" → "conhecesse" |

Padrão: `gemma3:12b` (`ModeloAnalise`). Custa ~13 s por frase (um áudio de 6 min ≈ 14 min de análise, uma vez
só, com cache) e ocupa ~8 GB de VRAM: rode com o WhisperLiveKit parado (`docker stop whisperlivekit`).

## Resultado no audiolivro inteiro (64 frases, 6 min de áudio)

Tradução com `translategemma:12b` + análise com `gemma3:12b` (prompt v2): 971 s no total, uma vez só (depois vem do
cache). 339 blocos, 53 expressões/phrasal/colocações/nomes, 99% das palavras cobertas (1232/1247), 338/339
traduções de bloco achadas literalmente na tradução da frase. O prompt v2 reduziu as notas inúteis, mas 4 de 101
ainda diziam "não há armadilha"; essas são descartadas no código (`AlinhadorBlocos.NotaUtil`).

## Limites

- O sentido às vezes especula além do texto ("nunca mais se encontrarão" para "Or never.").
- Se a tradução da frase estiver errada, a nota do bloco herda o erro.
- No tempo real a análise não roda (é lenta demais); fica para o arquivo.
