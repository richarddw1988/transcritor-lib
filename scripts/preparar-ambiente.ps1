<#
.SYNOPSIS
    Prepara (ou confere) tudo o que a Transcritor lib precisa para rodar nesta máquina.

.DESCRIPTION
    Idempotente: pode rodar quantas vezes quiser; só faz o que falta.
      1. Confere os pré-requisitos: .NET 10, Docker com GPU NVIDIA, ffmpeg/ffprobe, espaço do Docker e memória do WSL.
      2. Constrói as imagens do Speaches (a oficial 0.8.3 com uma correção) e do WhisperLiveKit (não há imagem
         publicada) e sobe os serviços do docker-compose.yml.
      3. Espera cada serviço responder.
      4. Baixa os modelos que faltam: Whisper no Speaches e os modelos de tradução e da correção no Ollama.
      5. Compila a solução e roda "transcritor servicos" para confirmar tudo.
      6. Baixa o Wiktionary (kaikki.org, ~600 MB) e as listas de frequência (~1,3 MB) e gera o dicionário local em
         dados/ (-SemDicionario pula).
      7. Converte para ONNX, dentro de um contêiner Docker, os dois BERT dos homófonos e da conferência da correção
         (dados/bert, ~1 GB; não precisa de Python nesta máquina; -SemHomofonos pula).

    Primeira vez: baixa ~58 GB (imagens + modelos + dicionário + BERT; ~48 GB com -SemCorrecao -SemDicionario
    -SemHomofonos) e leva de 20 a 60 minutos, conforme a internet.

.PARAMETER SoConferir
    Só confere pré-requisitos e serviços; não constrói, não sobe e não baixa nada.

.PARAMETER SemTempoReal
    Não constrói nem sobe o WhisperLiveKit (economiza ~22 GB). Arquivo e tradução continuam funcionando.

.PARAMETER ModeloTranscricao
    Modelo Whisper do Speaches (mesmo valor de Transcritor:Arquivo:ModeloTranscricao no appsettings.json).

.PARAMETER ModeloFrases
    Modelo do Ollama para as frases de arquivo (Transcritor:Traducao:ModeloFrases).

.PARAMETER ModeloFrasesTempoReal
    Modelo do Ollama para frases no tempo real (Transcritor:Traducao:ModeloFrasesTempoReal).

.PARAMETER ModeloPalavras
    Modelo do Ollama para palavra solta (Transcritor:Traducao:ModeloPalavras).

.PARAMETER ComAnaliseDeSentido
    Também baixa o modelo da análise de sentido (blocos e "o que quis dizer"; ~8 GB).

.PARAMETER ModeloAnalise
    Modelo do Ollama para a análise de sentido (Transcritor:Traducao:ModeloAnalise).

.PARAMETER SemCorrecao
    Não baixa o modelo da correção de palavras ouvidas errado (Transcritor:Traducao:CorrigirTranscricao, ligada por
    padrão; ~8 GB) e confere os serviços com ela desligada. Ponha CorrigirTranscricao = false no appsettings.json.

.PARAMETER ModeloCorrecao
    Modelo do Ollama para a correção da transcrição (Transcritor:Traducao:ModeloCorrecao).

.PARAMETER SemDicionario
    Não baixa os dumps do Wiktionary (kaikki.org, ~600 MB, CC BY-SA) nem as listas de frequência do FrequencyWords
    (en_50k.txt e pt_br_50k.txt, ~0,6 MB cada, CC BY-SA) e não gera dados/dicionario.db (dicionário inglês ↔ português
    local, ~60 MB); só informa se ele existe. Sem o dicionário, os homófonos não rodam e a correção perde as sugestões
    de palavras parecidas.

.PARAMETER SemHomofonos
    Não gera dados/bert (~1 GB: google-bert/bert-base-cased e neuralmind/bert-base-portuguese-cased do huggingface.co,
    convertidos para ONNX num contêiner python:3.12-slim por scripts/exportar-bert.py; baixa ~1,3 GB na 1ª vez, entre
    imagem, PyTorch e modelos); só informa se existem. Sem eles, nem os homófonos (CorrigirHomofonos) nem a correção
    pelo modelo rodam (ela é conferida pelo BERT; Transcritor:Traducao:ConferirCorrecaoComBert).

.EXAMPLE
    .\scripts\preparar-ambiente.ps1
.EXAMPLE
    .\scripts\preparar-ambiente.ps1 -SoConferir
.EXAMPLE
    .\scripts\preparar-ambiente.ps1 -SemTempoReal -ModeloPalavras gemma3:12b
#>
[CmdletBinding()]
param(
    [switch] $SoConferir,
    [switch] $SemTempoReal,
    [string] $ModeloTranscricao = 'Systran/faster-whisper-large-v3',
    [string] $ModeloFrases = 'translategemma:12b',
    [string] $ModeloFrasesTempoReal = 'translategemma:4b',
    [string] $ModeloPalavras = 'gemma3:4b',
    [switch] $ComAnaliseDeSentido,
    [string] $ModeloAnalise = 'gemma3:12b',
    [switch] $SemCorrecao,
    [string] $ModeloCorrecao = 'gemma3:12b',
    [switch] $SemDicionario,
    [switch] $SemHomofonos,
    [string] $SpeachesUrl = 'http://localhost:8000',
    [string] $OllamaUrl = 'http://localhost:11434',
    [string] $WhisperLiveKitUrl = 'http://localhost:8001'
)

$ErrorActionPreference = 'Stop'
$raiz = Split-Path -Parent $PSScriptRoot
Set-Location $raiz
$problemas = New-Object System.Collections.Generic.List[string]
$avisos = New-Object System.Collections.Generic.List[string]

function Escrever-Etapa([string] $texto) { Write-Host ''; Write-Host "== $texto" -ForegroundColor Cyan }
function Escrever-Ok([string] $texto) { Write-Host "  [ OK ] $texto" -ForegroundColor Green }
function Escrever-Aviso([string] $texto) { Write-Host "  [AVISO] $texto" -ForegroundColor Yellow; $script:avisos.Add($texto) }
function Escrever-Falha([string] $texto) { Write-Host "  [FALHA] $texto" -ForegroundColor Red; $script:problemas.Add($texto) }

function Testar-Comando([string] $nome) { [bool](Get-Command $nome -ErrorAction SilentlyContinue) }

# Roda um programa que escreve progresso ou avisos no stderr (docker, dotnet, curl). No Windows PowerShell 5.1, com a
# saída do script redirecionada (ex.: *> preparo.log), cada linha do stderr vira erro, e com 'Stop' o script pararia no
# primeiro aviso. Aqui o erro só é mostrado; quem chama confere o resultado em $LASTEXITCODE.
function Rodar-Programa([scriptblock] $blocoPrograma) {
    $ErrorActionPreference = 'Continue'
    & $blocoPrograma
}

function Obter-Json([string] $url) {
    try { Invoke-RestMethod -Uri $url -TimeoutSec 5 -ErrorAction Stop } catch { $null }
}

function Aguardar-Servico([string] $nome, [string] $url, [double] $minutos) {
    Write-Host "  $nome em $url..." -NoNewline
    $limite = (Get-Date).AddMinutes($minutos)
    do {
        if ($null -ne (Obter-Json $url)) { Write-Host ' respondendo.' -ForegroundColor Green; return $true }
        if ((Get-Date) -ge $limite) { break }
        Write-Host '.' -NoNewline
        Start-Sleep -Seconds 5
    } while ($true)
    Write-Host ''
    Escrever-Falha "$nome não respondeu em $url. Veja: docker compose logs $($nome.ToLowerInvariant())"
    return $false
}

# ---------------------------------------------------------------------------
Escrever-Etapa '1. Pré-requisitos'

# .NET 10
if (Testar-Comando 'dotnet') {
    $sdks = & dotnet --list-sdks
    if ($sdks -match '^10\.') { Escrever-Ok ".NET SDK 10 ($((($sdks -match '^10\.') | Select-Object -Last 1).Split(' ')[0]))" }
    else { Escrever-Falha '.NET SDK 10 não encontrado. Instale: winget install Microsoft.DotNet.SDK.10' }
} else { Escrever-Falha 'dotnet não encontrado. Instale: winget install Microsoft.DotNet.SDK.10' }

# ffmpeg / ffprobe (o módulo de arquivo chama os executáveis locais)
foreach ($ferramenta in 'ffmpeg', 'ffprobe') {
    if (Testar-Comando $ferramenta) { Escrever-Ok "$ferramenta no PATH" }
    else { Escrever-Falha "$ferramenta não encontrado. Instale: winget install Gyan.FFmpeg (e abra um terminal novo)" }
}

# GPU NVIDIA
if (Testar-Comando 'nvidia-smi') {
    $gpu = (& nvidia-smi --query-gpu=name,memory.total --format=csv,noheader | Select-Object -First 1)
    Escrever-Ok "GPU: $gpu"
    # A imagem do WhisperLiveKit usa CUDA 12.9, que pede driver 576 ou mais novo no Windows.
    $driver = (& nvidia-smi --query-gpu=driver_version --format=csv,noheader | Select-Object -First 1).Trim()
    if ([int]($driver.Split('.')[0]) -ge 576) { Escrever-Ok "Driver NVIDIA $driver" }
    else { Escrever-Aviso "Driver NVIDIA ${driver}: a imagem do WhisperLiveKit (CUDA 12.9) pede 576 ou mais novo. Atualize pelo NVIDIA App ou nvidia.com." }
} else {
    Escrever-Aviso 'nvidia-smi não encontrado: sem GPU NVIDIA os modelos rodam muito mais devagar (e o compose pede GPU).'
}

# Docker
$dockerOk = $false
if (-not (Testar-Comando 'docker')) {
    Escrever-Falha 'Docker não encontrado. Instale o Docker Desktop (backend WSL2).'
} else {
    & docker info --format '{{.ServerVersion}}' 2>$null | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Escrever-Falha 'O Docker não está rodando. Abra o Docker Desktop e rode o script de novo.'
    } else {
        $dockerOk = $true
        Escrever-Ok "Docker $(& docker info --format '{{.ServerVersion}}')"
    }
}

# Espaço do disco do Docker e memória do WSL
$config = Join-Path $env:APPDATA 'Docker\settings-store.json'
$pastaDocker = Join-Path $env:LOCALAPPDATA 'Docker\wsl'
if (Test-Path $config) {
    $dir = (Get-Content $config -Raw | ConvertFrom-Json).CustomWslDistroDir
    if ($dir) { $pastaDocker = $dir }
}
$unidade = (Split-Path -Qualifier $pastaDocker).TrimEnd(':')
$livre = [math]::Round((Get-PSDrive $unidade).Free / 1GB, 1)
$precisa = if ($SemTempoReal) { 20 } else { 45 }
if ($livre -ge $precisa) { Escrever-Ok "Disco do Docker em $pastaDocker com $livre GB livres" }
else {
    Escrever-Aviso ("Disco do Docker em $pastaDocker tem só $livre GB livres (a 1ª preparação usa ~$precisa GB). " +
        'Mova em Docker Desktop > Settings > Resources > Advanced > Disk image location.')
}

$wslconfig = Join-Path $env:USERPROFILE '.wslconfig'
if (Test-Path $wslconfig) {
    $linhaMemoria = Select-String -Path $wslconfig -Pattern '^\s*memory\s*=\s*(\d+)\s*GB' | Select-Object -First 1
    if ($linhaMemoria -and [int]$linhaMemoria.Matches[0].Groups[1].Value -lt 10) {
        Escrever-Aviso ("$wslconfig limita a VM do Docker a $($linhaMemoria.Matches[0].Groups[1].Value) GB. Com os três serviços, o Ollama " +
            'trava ao trocar de modelo; use memory=16GB (12GB com menos de 32 GB de RAM) e swap=4GB e rode "wsl --shutdown".')
    } else { Escrever-Ok 'Memória da VM do WSL' }
} else { Escrever-Ok 'Memória da VM do WSL (padrão: metade da RAM)' }

if ($problemas.Count -gt 0 -and -not $SoConferir) {
    Write-Host ''
    Write-Host 'Corrija os itens [FALHA] acima e rode o script de novo.' -ForegroundColor Red
    exit 1
}

# ---------------------------------------------------------------------------
$servicos = @('speaches', 'ollama')
if (-not $SemTempoReal) { $servicos += 'whisperlivekit' }

if (-not $SoConferir -and $dockerOk) {
    Escrever-Etapa '2. Imagens e contêineres'
    # Speaches 0.8.3 com a correção do descarregar (docker/speaches/Dockerfile): build rápido sobre a imagem oficial.
    $imagemSpeaches = & docker images -q 'transcritor/speaches:0.8.3-descarregar'
    if ($imagemSpeaches) { Escrever-Ok 'Imagem do Speaches já existe' }
    else {
        Write-Host '  Construindo a imagem do Speaches (baixa a oficial, ~8 GB, na 1ª vez)...'
        Rodar-Programa { docker compose build speaches }
        if ($LASTEXITCODE -ne 0) { Escrever-Falha 'Falhou o build do Speaches (veja o log acima).'; exit 1 }
        Escrever-Ok 'Imagem do Speaches construída'
    }
    if (-not $SemTempoReal) {
        $imagem = & docker images -q 'transcritor/whisperlivekit:0.2.27'
        if ($imagem) { Escrever-Ok 'Imagem do WhisperLiveKit já existe' }
        else {
            Write-Host '  Construindo a imagem do WhisperLiveKit (~22 GB, pode levar 20+ min na 1ª vez)...'
            Rodar-Programa { docker compose build whisperlivekit }
            if ($LASTEXITCODE -ne 0) { Escrever-Falha 'Falhou o build do WhisperLiveKit (veja o log acima).'; exit 1 }
            Escrever-Ok 'Imagem do WhisperLiveKit construída'
        }
    }

    Rodar-Programa { docker compose up -d @servicos }
    if ($LASTEXITCODE -ne 0) { Escrever-Falha 'docker compose up falhou (veja o log acima).'; exit 1 }
    Escrever-Ok "Contêineres no ar: $($servicos -join ', ')"
}

# ---------------------------------------------------------------------------
Escrever-Etapa '3. Serviços respondendo'
$espera = if ($SoConferir) { 0 } else { 10 }
$speachesNoAr = Aguardar-Servico 'Speaches' "$SpeachesUrl/health" $espera
$ollamaNoAr = Aguardar-Servico 'Ollama' "$OllamaUrl/api/version" $espera
if (-not $SemTempoReal) {
    # Na 1ª subida o WhisperLiveKit baixa o modelo (~1,5 GB) e aquece antes de responder.
    $null = Aguardar-Servico 'WhisperLiveKit' "$WhisperLiveKitUrl/health" ($(if ($SoConferir) { 0 } else { 15 }))
}

# A imagem do projeto (docker/speaches/Dockerfile) marca o /health com este cabeçalho; sem ele, a lib não consegue
# tirar o Whisper da GPU entre as etapas e espera o tempo ocioso do Speaches.
if ($speachesNoAr) {
    $cabecalho = $null
    try { $cabecalho = (Invoke-WebRequest "$SpeachesUrl/health" -UseBasicParsing -TimeoutSec 5).Headers['X-Transcritor-Descarregar'] } catch { }
    if ($cabecalho) { Escrever-Ok 'Speaches com a correção do descarregar (imagem do projeto)' }
    else {
        Escrever-Aviso ('O Speaches no ar não é a imagem do projeto: a lib não tira o Whisper da GPU entre as etapas. ' +
            'Rode: docker compose up -d --build speaches')
    }
}

# A GPU precisa aparecer dentro dos contêineres; sem isso os modelos rodariam em CPU (ou nem subiriam).
foreach ($servico in $servicos) {
    # Captura tudo antes de olhar o código de saída (cortar o pipeline no meio dá código de erro falso).
    $nomeGpu = $null
    try { $nomeGpu = @(& docker exec $servico nvidia-smi --query-gpu=name --format=csv,noheader 2>$null) } catch { $nomeGpu = $null }
    if ($LASTEXITCODE -eq 0 -and $nomeGpu -and $nomeGpu[0]) { Escrever-Ok "GPU visível no contêiner $servico ($($nomeGpu[0]))" }
    else {
        Escrever-Falha ("O contêiner $servico não enxerga a GPU. Confira: driver NVIDIA atualizado, Docker Desktop com backend WSL2 " +
            "e 'docker run --rm --gpus all ubuntu nvidia-smi' funcionando.")
    }
}

# ---------------------------------------------------------------------------
Escrever-Etapa '4. Modelos'
if ($speachesNoAr) {
    $instalados = (Obter-Json "$SpeachesUrl/v1/models").data.id
    if ($instalados -contains $ModeloTranscricao) { Escrever-Ok "Speaches: $ModeloTranscricao" }
    elseif ($SoConferir) { Escrever-Falha "Speaches sem o modelo $ModeloTranscricao" }
    else {
        Write-Host "  Baixando $ModeloTranscricao no Speaches (~3 GB)..."
        try {
            Invoke-RestMethod -Method Post -Uri "$SpeachesUrl/v1/models/$ModeloTranscricao" -TimeoutSec 3600 | Out-Null
            Escrever-Ok "Speaches: $ModeloTranscricao baixado"
        } catch { Escrever-Falha "Não consegui baixar $ModeloTranscricao no Speaches: $($_.Exception.Message)" }
    }
}

if ($ollamaNoAr) {
    $instalados = (Obter-Json "$OllamaUrl/api/tags").models.name
    $modelosOllama = @($ModeloFrases, $ModeloFrasesTempoReal, $ModeloPalavras)
    if ($ComAnaliseDeSentido) { $modelosOllama += $ModeloAnalise }
    if (-not $SemCorrecao) { $modelosOllama += $ModeloCorrecao }
    foreach ($modelo in $modelosOllama | Select-Object -Unique) {
        $nome = if ($modelo.Contains(':')) { $modelo } else { "${modelo}:latest" }
        if ($instalados -contains $nome) { Escrever-Ok "Ollama: $modelo" }
        elseif ($SoConferir) { Escrever-Falha "Ollama sem o modelo $modelo" }
        else {
            Write-Host "  Baixando $modelo no Ollama..."
            Rodar-Programa { docker exec ollama ollama pull $modelo }
            if ($LASTEXITCODE -eq 0) { Escrever-Ok "Ollama: $modelo baixado" }
            else { Escrever-Falha "Não consegui baixar $modelo no Ollama." }
        }
    }
}

# ---------------------------------------------------------------------------
if (-not $SoConferir -and $problemas.Count -eq 0) {
    Escrever-Etapa '5. Compilação e conferência final'
    Rodar-Programa { dotnet build "$raiz\Transcritor.slnx" -nologo -v q }
    if ($LASTEXITCODE -ne 0) { Escrever-Falha 'A solução não compilou.' }
    else {
        Escrever-Ok 'Solução compilada'
        $env:TRANSCRITOR_Transcritor__Arquivo__ModeloTranscricao = $ModeloTranscricao
        $env:TRANSCRITOR_Transcritor__Traducao__ModeloFrases = $ModeloFrases
        $env:TRANSCRITOR_Transcritor__Traducao__ModeloFrasesTempoReal = $ModeloFrasesTempoReal
        $env:TRANSCRITOR_Transcritor__Traducao__ModeloPalavras = $ModeloPalavras
        if ($ComAnaliseDeSentido) {
            $env:TRANSCRITOR_Transcritor__Traducao__AnalisarSentido = 'true'
            $env:TRANSCRITOR_Transcritor__Traducao__ModeloAnalise = $ModeloAnalise
        }
        if ($SemCorrecao) {
            $env:TRANSCRITOR_Transcritor__Traducao__CorrigirTranscricao = 'false'
        } else {
            $env:TRANSCRITOR_Transcritor__Traducao__CorrigirTranscricao = 'true'
            $env:TRANSCRITOR_Transcritor__Traducao__ModeloCorrecao = $ModeloCorrecao
        }
        Rodar-Programa { dotnet run --project "$raiz\src\Transcritor.Cli" --no-build -- servicos }
        if ($LASTEXITCODE -ne 0) { Escrever-Falha 'transcritor servicos apontou problemas (acima).' }
    }
}

# ---------------------------------------------------------------------------
# Dicionário local: usado pela correção e pelos homófonos; -SemDicionario pula.
Escrever-Etapa '6. Dicionário inglês-português'
$pastaDados = Join-Path $raiz 'dados'
$banco = Join-Path $pastaDados 'dicionario.db'
$arquivosDicionario = @(
    @{ Nome = 'kaikki-en.jsonl.gz'; Url = 'https://kaikki.org/dictionary/English/kaikki.org-dictionary-English.jsonl.gz' },
    @{ Nome = 'kaikki-pt.jsonl.gz'; Url = 'https://kaikki.org/dictionary/Portuguese/kaikki.org-dictionary-Portuguese.jsonl.gz' },
    # Listas de frequência (OpenSubtitles 2018, CC BY-SA 4.0): escolhem as palavras raras que ganham dica na tradução.
    @{ Nome = 'en_50k.txt'; Url = 'https://raw.githubusercontent.com/hermitdave/FrequencyWords/master/content/2018/en/en_50k.txt' },
    @{ Nome = 'pt_br_50k.txt'; Url = 'https://raw.githubusercontent.com/hermitdave/FrequencyWords/master/content/2018/pt_br/pt_br_50k.txt' }
)
$faltamListas = @('en_50k.txt', 'pt_br_50k.txt' | Where-Object { -not (Test-Path (Join-Path $pastaDados $_)) })

# Banco gerado por versão anterior da lib (ex.: sem frequência) é recusado e a lib segue sem dicionário, em silêncio.
$bancoValido = $false
$temHomofonos = $false
if (Test-Path $banco) {
    # Via cmd: no Windows PowerShell, a saída de erro de um programa vira erro do script.
    & cmd.exe /c "dotnet run --project `"$raiz\src\Transcritor.Cli`" -- dicionario procurar house --de en --para pt >nul 2>&1"
    $bancoValido = $LASTEXITCODE -eq 0
    # Gerado antes de 2026-11-23: funciona, mas sem a lista de homófonos (a correção de homófonos fica parada).
    & cmd.exe /c "dotnet run --project `"$raiz\src\Transcritor.Cli`" --no-build -- dicionario homofonos cela --idioma pt >nul 2>&1"
    $temHomofonos = $LASTEXITCODE -eq 0
}

if ($bancoValido -and $faltamListas.Count -eq 0 -and $temHomofonos) {
    Escrever-Ok "Dicionário: $banco ($([math]::Round((Get-Item $banco).Length / 1MB)) MB), com listas de frequência e homófonos"
}
elseif ($SemDicionario) {
    if ($bancoValido -and $faltamListas.Count -eq 0) { Escrever-Aviso "Dicionário sem a lista de homófonos (gerado antes de 2026-11-23). Para completar, rode o script sem -SemDicionario" }
    elseif ($bancoValido) { Escrever-Aviso "Dicionário sem listas de frequência ($($faltamListas -join ', ')). Para completar, rode o script sem -SemDicionario" }
    elseif (Test-Path $banco) { Escrever-Aviso "Dicionário de versão anterior em $banco (a lib não usa). Para gerar de novo, rode o script sem -SemDicionario" }
    else { Write-Host '  Não gerado (-SemDicionario): sem ele, os homófonos não rodam e a correção fica sem as palavras parecidas.' }
}
elseif ($SoConferir) {
    if (-not $bancoValido) { Escrever-Falha "Dicionário ausente ou de versão anterior em $banco" }
    elseif (-not $temHomofonos) { Escrever-Falha "Dicionário sem a lista de homófonos em $banco" }
    if ($faltamListas.Count -gt 0) { Escrever-Falha "Listas de frequência ausentes em ${pastaDados}: $($faltamListas -join ', ')" }
}
else {
    New-Item -ItemType Directory -Force $pastaDados | Out-Null
    foreach ($d in $arquivosDicionario) {
        $destino = Join-Path $pastaDados $d.Nome
        if (Test-Path $destino) { Escrever-Ok "Já baixado: $($d.Nome)"; continue }
        Write-Host "  Baixando $($d.Url)..."
        Rodar-Programa { curl.exe -sSfL -o "$destino.tmp" $d.Url }
        if ($LASTEXITCODE -eq 0) { Move-Item -Force "$destino.tmp" $destino; Escrever-Ok "Baixado: $($d.Nome)" }
        else { Escrever-Falha "Não consegui baixar $($d.Url)" }
    }
    if (@($arquivosDicionario | Where-Object { -not (Test-Path (Join-Path $pastaDados $_.Nome)) }).Count -eq 0) {
        Rodar-Programa { dotnet run --project "$raiz\src\Transcritor.Cli" -- dicionario importar }
        if ($LASTEXITCODE -eq 0) { Escrever-Ok "Dicionário gerado: $banco" }
        else { Escrever-Falha 'transcritor dicionario importar falhou (acima).' }
    }
}

# ---------------------------------------------------------------------------
# Modelos BERT dos homófonos e da conferência da correção: convertidos para ONNX num contêiner, sem Python aqui.
Escrever-Etapa '7. BERT dos homófonos e da correção'
$pastaBert = Join-Path $pastaDados 'bert'
$faltamBert = @('en', 'pt' | Where-Object {
        -not (Test-Path (Join-Path $pastaBert "$_\model.onnx")) -or -not (Test-Path (Join-Path $pastaBert "$_\vocab.txt")) })
if ($faltamBert.Count -eq 0) {
    Escrever-Ok "Modelos BERT em $pastaBert (en, pt)"
}
elseif ($SemHomofonos) {
    Write-Host '  Não gerados (-SemHomofonos): sem eles, nem os homófonos nem a correção pelo modelo rodam.'
}
elseif ($SoConferir) {
    Escrever-Falha "Modelos BERT ausentes em ${pastaBert}: $($faltamBert -join ', ')"
}
else {
    New-Item -ItemType Directory -Force $pastaBert | Out-Null
    Write-Host '  Convertendo os BERT para ONNX num contêiner python:3.12-slim (1ª vez: baixa ~1,3 GB, leva alguns minutos)...'
    # O volume transcritor-hf-bert guarda os modelos baixados: rodar de novo não baixa outra vez.
    $comando = 'pip install --no-cache-dir -q --root-user-action=ignore torch --index-url https://download.pytorch.org/whl/cpu' +
        ' && pip install --no-cache-dir -q --root-user-action=ignore transformers onnx onnxscript onnxruntime' +
        ' && python /scripts/exportar-bert.py /saida'
    Rodar-Programa {
        docker run --rm -v "${pastaBert}:/saida" -v "$raiz\scripts:/scripts:ro" -v 'transcritor-hf-bert:/root/.cache/huggingface' `
            python:3.12-slim sh -c $comando
    }
    if ($LASTEXITCODE -eq 0) { Escrever-Ok "Modelos BERT gerados em $pastaBert" }
    else { Escrever-Falha 'A conversão dos BERT falhou (veja o log acima).' }
}

# ---------------------------------------------------------------------------
Write-Host ''
if ($problemas.Count -eq 0) {
    Write-Host 'Ambiente pronto.' -ForegroundColor Green
    if ($ModeloTranscricao -ne 'Systran/faster-whisper-large-v3' -or $ModeloFrases -ne 'translategemma:12b' -or $ModeloFrasesTempoReal -ne 'translategemma:4b' -or $ModeloPalavras -ne 'gemma3:4b') {
        Write-Host 'Lembre de pôr os mesmos modelos em src/Transcritor.Cli/appsettings.json (ou na configuração do seu app).' -ForegroundColor Yellow
    }
    Write-Host 'Teste:  dotnet run --project src/Transcritor.Cli -- arquivo C:\caminho\video.mp4 --saida Ambos'
    exit 0
}

Write-Host "Faltam $($problemas.Count) item(ns):" -ForegroundColor Red
$problemas | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
exit 1
