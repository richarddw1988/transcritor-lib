# transcritor-lib

Lib .NET 10 de **transcrição e tradução de fala, tudo local e de graça**, na sua GPU NVIDIA.
Os modelos de IA rodam em contêineres Docker (Speaches, WhisperLiveKit, Ollama); a lib só faz as chamadas.

- **Arquivo**: transcreve áudio ou vídeo e gera um JSON com frases e palavras (início e fim de cada uma), tradução
  português ↔ inglês pelo sentido, correção de palavras ouvidas errado ("cela" → "sela") e, opcionalmente, as expressões e "o que a pessoa quis dizer".
- **Microfone e som do sistema em tempo real**: eventos `Provisorio` e `Confirmado`, com tradução.
- **Dicionário inglês ↔ português local**: sentidos reais de palavras e expressões, gerado do Wiktionary
  em SQLite (~60 MB, consulta em menos de 1 ms), com listas de frequência de palavras. Com ele, a tradução de palavra
  escolhe entre os sentidos das expressões ("gave up"; acerto de 80% → 82% em 192 casos sem a tradução da frase).
  Experimental, desligado por padrão: dar à tradução das frases os sentidos das palavras raras.
- Saída em português, inglês ou os dois.

**Escopo:** a lib faz só o trabalho pesado (transcrever, traduzir, analisar e gerar os dados). Ela não tem
interface: player, legenda na tela e clique na palavra para traduzir ficam em outro projeto, que usa a lib
ou lê o JSON gerado.

## Documentação

| Documento | Conteúdo |
|---|---|
| [docs/manual.md](docs/manual.md) | **Manual passo a passo**: pré-requisitos, preparar o ambiente, usar pelo console e pela lib, configuração, problemas comuns |
| [docs/modelos.md](docs/modelos.md) | **Especificação dos modelos de IA** usados e testados, números medidos, requisitos de hardware e software |
| [docs/pesquisa-asr.md](docs/pesquisa-asr.md) | Pesquisa e medições de reconhecimento de fala (pt-BR e inglês) |
| [docs/etapa-0.md](docs/etapa-0.md) … [docs/etapa-4.md](docs/etapa-4.md) | Histórico técnico de cada etapa: protocolos dos serviços, decisões e limites |

## Começo rápido

Pré-requisitos (detalhes no [manual](docs/manual.md#2-instalar-os-pré-requisitos)): Windows 10/11, GPU NVIDIA
(12 GB recomendado, driver 576+), Docker Desktop (WSL2), .NET SDK 10, ffmpeg e Git.

```powershell
git clone https://github.com/richarddw1988/transcritor-lib
```
```powershell
cd transcritor-lib
```
```powershell
.\scripts\preparar-ambiente.ps1
```
O script confere tudo, constrói a imagem do WhisperLiveKit, sobe os serviços, baixa os modelos e termina com
**Ambiente pronto** (1ª vez: ~58 GB e 20–60 min). Depois:

```powershell
dotnet run --project src/Transcritor.Cli -- arquivo C:\videos\aula.mp4 --saida Ambos
```
```powershell
dotnet run --project src/Transcritor.Cli -- microfone --saida Ambos --idioma pt
```

## Serviços e modelos

| Serviço | Porta | Função | Modelos padrão |
|---|---|---|---|
| Speaches | 8000 | Transcrição de arquivo com tempo por palavra | `Systran/faster-whisper-large-v3` |
| WhisperLiveKit | 8001 | Transcrição em tempo real (WebSocket) | `large-v3-turbo` |
| Ollama | 11434 | Tradução, análise e correção | `translategemma:12b` (arquivo), `translategemma:4b` (tempo real), `gemma3:4b` (palavra), `gemma3:12b` (correção da transcrição, ligada por padrão; e análise, opcional) |

O Speaches roda numa imagem própria, a 0.8.3 oficial com uma linha corrigida ([docker/speaches](docker/speaches/Dockerfile)),
para a lib poder tirar o Whisper da GPU entre as etapas; o `preparar-ambiente.ps1` constrói essa imagem. No
WhisperLiveKit, um arquivo corrigido ([docker/whisperlivekit](docker/whisperlivekit/align_att_base.py)), montado pelo
`docker-compose.yml`, faz a detecção de idioma escolher só entre português e inglês.

Por que esses modelos, o que mais foi testado e quanto cada um custa: [docs/modelos.md](docs/modelos.md).

## Projetos

| Projeto | Papel |
|---|---|
| `Transcritor.Core` | Contratos (`ITranscritorArquivo`, `ITradutor`, `ITranscritorTempoReal`, ...), modelos do JSON e erros. Sem dependências. |
| `Transcritor.Arquivo` | ffprobe/ffmpeg → WAV 16 kHz mono → Speaches → frases e palavras → tradução → análise → JSON, com cache. |
| `Transcritor.Traducao` | Tradução pt ↔ en pelo sentido via Ollama (frase com as vizinhas como contexto, palavra com a frase), análise de sentido e correção da transcrição (homófonos por BERT em ONNX, na CPU). |
| `Transcritor.TempoReal` | Cliente WebSocket do WhisperLiveKit: eventos `Provisorio`/`Confirmado` como `IAsyncEnumerable`, tradução dos trechos. |
| `Transcritor.Captura` | Windows (WASAPI/NAudio): microfone e som do sistema em PCM 16 kHz mono. |
| `Transcritor.Cli` | Console para testar tudo: `arquivo`, `traduzir`, `palavra`, `microfone`, `sistema`, `simular`, `captura`, `servicos`, `liberar-gpu`, `dicionario`, `avaliar-fala`. |
| `Transcritor.Tests` | Testes unitários com fakes de HTTP, WebSocket e processo (não precisam dos serviços). |

Core, Arquivo, Traducao e TempoReal são `net10.0`; Captura, Cli e Tests são `net10.0-windows`.

```powershell
dotnet test
```
