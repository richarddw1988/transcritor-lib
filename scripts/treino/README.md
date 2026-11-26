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
| `preparar_episodios.sh` | Vídeos `.mkv` com legenda em inglês embutida → `en-sdh.srt` + voz separada (`voz16k.wav`) de cada um |
| `treinar.py` | Treino LoRA com os pares de uma ou mais pastas (`pasta:metade1` usa só a 1ª metade dela); `--teste N` só mede memória e tempo |
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
  transcritor/treino:whisper-lora python /s/treinar.py /treino/lora 1 /treino/pares:metade1

# 4. Medição na metade não vista, sem e com o adaptador
docker run --rm --gpus all -v transcritor-treino-hf:/root/.cache/huggingface -v C:\treino:/treino -v ${PWD}\scripts\treino:/s `
  transcritor/treino:whisper-lora python /s/avaliar.py /treino/pares aval-original.json --metade2
docker run --rm --gpus all -v transcritor-treino-hf:/root/.cache/huggingface -v C:\treino:/treino -v ${PWD}\scripts\treino:/s `
  transcritor/treino:whisper-lora python /s/avaliar.py /treino/pares aval-lora.json /treino/lora --metade2
```

Os dois scripts limitam a GPU a 11 GB: no Windows, passar disso transborda para a memória comum e fica 10x mais lento
sem dar erro. Treino: pico de 4,05 GB e ~1,2 s por trecho na RTX 3060 (medido).

Vários vídeos: `bash scripts/treino/preparar_episodios.sh C:\treino\eps ep1.mkv ep2.mkv ...`, transcrever os mesmos
vídeos com a lib (`transcritor arquivo ... --idioma en --saida Ingles`), `montar_pares.py` em cada um e
`treinar.py /treino/lora 2 /treino/eps/ep1/pares /treino/eps/ep2/pares ...`, deixando um vídeo de fora para medir.

## Falta para virar algo útil

- **Mais títulos:** com um filme só o modelo decora as falas; com o filme e 3 episódios de uma série melhorou no
  episódio de fora sem piorar as falas curtas, mas ainda é uma série só.
- **Usar no Speaches:** juntar o adaptador ao modelo (`merge_and_unload`), converter com `ct2-transformers-converter`
  (vai na imagem) e apontar `ModeloTranscricao` para ele — não testado.
- Medir também no português e nas gravações do usuário antes de adotar.
