# Etapa 2 — tradução

## Regra da `Saida`

O `texto` de cada frase fica sempre no idioma falado (é o que bate com as palavras e os tempos).
A `Saida` só decide se `traducao` é preenchida e para qual idioma (`RegraSaida.IdiomaDaTraducao`):

| Falado | `Portugues` | `Ingles` | `Ambos` |
|---|---|---|---|
| pt | sem tradução | traduz para en | traduz para en |
| en | traduz para pt | sem tradução | traduz para pt |
| outro | traduz para pt | traduz para en | erro claro |

## Cache

- Transcrição: chave `hash.motor:modelo.idioma` (como na Etapa 1).
- Tradução do arquivo: chave da transcrição + `.trad-<destino>.<tradutor>`; o identificador do tradutor
  inclui modelo, versão do prompt e tamanho da janela de contexto.
- Trocar só a `Saida` reaproveita a transcrição; repetir o mesmo pedido não chama nenhum serviço.
- Palavras: `palavras.json` na pasta de cache, chave = SHA-256 de modelos + versão do prompt + idiomas +
  palavra (minúsculas) + frase + tradução da frase.

## Prompts (testados com os modelos reais antes de codar)

Casos de teste: "banco" (praça → *bench*; contas → *bank*), "bank" (rio → *margem*), "call it a day".

| Estratégia | Resultado |
|---|---|
| translategemma, só a frase | "banco" da praça virou *bank* |
| translategemma, vizinhas como histórico de conversa | ignorou o contexto (*bank*) |
| translategemma, vizinhas no texto sem marcação | acertou *bench*, mas traduziu o trecho inteiro |
| gemma3:4b com JSON, prompt simples | misturou as vizinhas e inventou frase |
| **translategemma, uma frase por linha, alvo entre `<t>…</t>`** | **acertou os 4 casos; recorta só o alvo** |
| gemma3:4b com JSON, prompt reforçado | acertou os 4 casos (fica como estratégia para modelos não-TranslateGemma) |

Detalhes que viraram código:
- Frases na mesma linha faziam o modelo às vezes envolver o trecho todo na tag quando o alvo era a primeira
  frase; uma frase por linha resolveu. Mesmo assim o recorte é validado (sem quebra de linha e no máximo
  2× o tamanho do original + 30); se falhar, a frase é traduzida de novo sem contexto.
- "Portuguese (pt)" gerava português de Portugal ("Ficámos"); o prompt usa "Brazilian Portuguese (pt-BR)".
- `num_predict` limitado pelo tamanho do texto, para uma repetição do modelo não prender a chamada.
- Palavra: passar a tradução da frase como pista resolveu *banco → bench/bank* e *bank → margem*. Se a
  tradução da frase não for informada, o tradutor traduz a frase antes.

Limite conhecido: expressões idiomáticas na palavra solta. Com o gemma3:4b, "call" em "Let's call it a day"
vira "ligar"/"chamar" e "running" em "running out of time" vira "correndo", mesmo pedindo o sentido da
expressão. Um modelo maior (ex.: gemma3:12b) deve melhorar; é só trocar `ModeloPalavras`.

## Memória da VM do Docker

O `%USERPROFILE%\.wslconfig` atual limita a VM do WSL2 a 4 GB sem swap. Com Speaches e os dois modelos do
Ollama carregados, o Ollama registra "disabling mmap … due to host memory pressure" e chamadas que trocam
de modelo travam por minutos (a lib devolve `ServicoIndisponivelException` por tempo esgotado).
Chamadas com o modelo já carregado levam ~0,5–3 s.

Resolvido em 2026-11-20 subindo o `.wslconfig` para `memory=12GB` e `swap=4GB` (cópia do anterior em
`.wslconfig.bak-2026-11-20`). Depois disso os dois modelos ficam juntos na GPU e alternar entre eles leva
~3 s. A primeira chamada após reiniciar a VM é lenta porque cada modelo é lido do disco: ~60–90 s para o
primeiro arquivo (Speaches large-v3 + translategemma) e ~25 s para o gemma3.

## Modelo das frases: arquivo × tempo real (comparação em 2026-11-21)

As 64 frases do audiolivro de teste (Frankenstein, carta 1) traduzidas com o mesmo prompt:

| | translategemma:4b | translategemma:12b |
|---|---|---|
| Tempo por frase | ~2–3 s | 6,7 s (máx. 14,7 s) |
| Tags `<t>` perdidas (cai na tradução sem contexto) | 0/64 | 5/64 |
| Exemplos | "Eu tento em vão convencê-me"; "Seus neves e gelos desapareceram"; "eu mesmo me contratei … barco de baleias do Groenlândia" | "Tento, em vão, me convencer"; "A neve e o gelo foram deixados para trás"; "trabalhei como ajudante em um navio baleeiro na Groenlândia" |

Decisão: `ModeloFrases` = `translategemma:12b` (arquivo, processado antes de assistir, com cache) e
`ModeloFrasesTempoReal` = `translategemma:4b` (tempo real, comando `traduzir` e frase de apoio da palavra).
O 12b ainda erra trechos difíceis ("the channel of their earlier bent") e às vezes troca o tratamento ("vocês").
