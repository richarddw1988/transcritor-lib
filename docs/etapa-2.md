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
