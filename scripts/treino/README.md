# Treino do Whisper com legendas de filmes (experimental)

Ferramentas para ajustar o Whisper large-v3 (LoRA) com filmes e episódios que têm legenda em inglês, para tentar
fechar as lacunas da transcrição. **Nada disto é usado pela lib**; o resultado só seria adotado como outro modelo no
Speaches (`ModeloTranscricao`) depois de medido. Números e conclusões do teste: [docs/modelos.md](../../docs/modelos.md)
(tabela "Transcrição" em "Modelos testados").

## Arquivos

| Arquivo | O que faz |
|---|---|
| `Dockerfile` | Imagem `transcritor/treino:whisper-lora`: a do separador (PyTorch 2.5.1, CUDA 12.4) + transformers, peft, ctranslate2 |
| `filme_wer.py` | Erro da transcrição da lib contra a legenda SDH, trecho a trecho pelo tempo |
| `montar_pares.py` | Legenda + áudio + transcrição da lib → trechos `NNNNN.wav` + `pares.json` (texto da legenda, erro da lib) |
| `treinar.py` | Treino LoRA na 1ª metade dos pares (pela ordem de tempo); `--teste N` só mede memória e tempo |
| `avaliar.py` | Erro do modelo original ou com o adaptador; `--metade2` mede só a metade que o treino não viu |

## Passo a passo (um filme)

```powershell
# 1. Imagem de treino (uma vez; ~9,5 GB, quase tudo em comum com a do separador)
docker build -t transcritor/treino:whisper-lora scripts/treino

# 2. Pares: legenda .srt em inglês (SDH de preferência, sincronizada com o vídeo), a voz separada pelo separador
#    convertida para WAV 16 kHz mono, e a transcrição da lib (.transcricao.json) do mesmo filme
python scripts/treino/montar_pares.py Filme.en.srt voz16k.wav Filme.transcricao.json C:\treino\pares --gravar

# 3. Treino (o 1º uso baixa openai/whisper-large-v3, ~3 GB, para o volume transcritor-treino-hf)
docker run --rm --gpus all -v transcritor-treino-hf:/root/.cache/huggingface -v C:\treino:/treino -v ${PWD}\scripts\treino:/s `
  transcritor/treino:whisper-lora python /s/treinar.py /treino/pares /treino/lora 1

# 4. Medição na metade não vista, sem e com o adaptador
docker run --rm --gpus all -v transcritor-treino-hf:/root/.cache/huggingface -v C:\treino:/treino -v ${PWD}\scripts\treino:/s `
  transcritor/treino:whisper-lora python /s/avaliar.py /treino/pares aval-original.json --metade2
docker run --rm --gpus all -v transcritor-treino-hf:/root/.cache/huggingface -v C:\treino:/treino -v ${PWD}\scripts\treino:/s `
  transcritor/treino:whisper-lora python /s/avaliar.py /treino/pares aval-lora.json /treino/lora --metade2
```

Os dois scripts limitam a GPU a 11 GB: no Windows, passar disso transborda para a memória comum e fica 10x mais lento
sem dar erro. Treino: pico de 4,05 GB e ~1,2 s por trecho na RTX 3060 (medido).

## Falta para virar algo útil

- **Vários filmes:** com um só, o modelo decora as falas (3 épocas inventou uma fala da 1ª metade em trechos curtos).
  O plano é juntar 10–20 títulos, deixar um filme inteiro fora do treino para medir e treinar com o resto.
- **Usar no Speaches:** juntar o adaptador ao modelo (`merge_and_unload`), converter com `ct2-transformers-converter`
  (vai na imagem) e apontar `ModeloTranscricao` para ele — não testado.
- Medir também no português e nas gravações do usuário antes de adotar.
