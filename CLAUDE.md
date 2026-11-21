# Instruções do projeto transcritor-lib

## Idioma
Fale com o usuário em português do Brasil. Código, nomes e documentação também em português.

## Documentação sempre atualizada (obrigatório)
Toda mudança que afete o que está abaixo atualiza os documentos **no mesmo commit**:

| Se mudar... | Atualize |
|---|---|
| Um modelo de IA (padrão, troca, novo teste ou medição) | `docs/modelos.md` (tabelas "em uso" e "testados", data no topo), README (tabela de serviços), `appsettings.json` do Cli, padrões em `Opcoes*.cs`, `scripts/preparar-ambiente.ps1` |
| Imagem Docker, porta ou comando de serviço | `docker-compose.yml`, `docs/modelos.md`, `docs/manual.md` (seções 3, 6 e 9), README |
| Pré-requisito ou passo de instalação | `docs/manual.md` (seções 2 e 3), `scripts/preparar-ambiente.ps1` (checagem correspondente), README (começo rápido) |
| Comando ou opção do Cli | `docs/manual.md` (seção 4) — confira com `transcritor <comando> --help` |
| API pública da lib | `docs/manual.md` (seção 5) e `tests/Transcritor.Tests/ExemplosDoManualTests.cs` (os exemplos precisam compilar) |
| Opção de configuração | `docs/manual.md` (seção 6) e `appsettings.json` do Cli |
| Requisito de hardware/VRAM medido | `docs/modelos.md` (requisitos e VRAM) |

Antes de dizer que o ambiente está pronto para outra pessoa, teste num clone limpo:
`git clone` numa pasta temporária, `dotnet build`, `dotnet test` e `.\scripts\preparar-ambiente.ps1 -SoConferir`.

## Como trabalhar
- Antes de trocar um modelo padrão, meça (qualidade e tempo) e registre os números em `docs/modelos.md`;
  não escreva número que não foi medido — diga "não medido".
- A solução compila sem avisos (`TreatWarningsAsErrors`) e os testes passam antes de cada commit.
- Commits com mensagem descritiva em português; push para `origin/main` quando o usuário pedir ou já tiver
  combinado.
- Mídias de teste e `*.transcricao.json` não vão para o Git (`.gitignore`).
