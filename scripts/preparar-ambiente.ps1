<#
.SYNOPSIS
    Prepara (ou confere) tudo o que a Transcritor lib precisa para rodar nesta máquina.

.DESCRIPTION
    Idempotente: pode rodar quantas vezes quiser; só faz o que falta.
      1. Confere os pré-requisitos: .NET 10, Docker com GPU NVIDIA, ffmpeg/ffprobe, espaço do Docker e memória do WSL.
      2. Constrói a imagem do WhisperLiveKit (não há imagem publicada) e sobe os serviços do docker-compose.yml.
      3. Espera cada serviço responder.
      4. Baixa os modelos que faltam: Whisper no Speaches e os modelos de tradução no Ollama.
      5. Compila a solução e roda "transcritor servicos" para confirmar tudo.

    Primeira vez: baixa ~48 GB (imagens + modelos) e leva de 20 a 60 minutos, conforme a internet.

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
            'trava ao trocar de modelo; use memory=12GB e swap=4GB e rode "wsl --shutdown".')
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
    if (-not $SemTempoReal) {
        $imagem = & docker images -q 'transcritor/whisperlivekit:0.2.27'
        if ($imagem) { Escrever-Ok 'Imagem do WhisperLiveKit já existe' }
        else {
            Write-Host '  Construindo a imagem do WhisperLiveKit (~22 GB, pode levar 20+ min na 1ª vez)...'
            & docker compose build whisperlivekit
            if ($LASTEXITCODE -ne 0) { Escrever-Falha 'Falhou o build do WhisperLiveKit (veja o log acima).'; exit 1 }
            Escrever-Ok 'Imagem do WhisperLiveKit construída'
        }
    }

    & docker compose up -d @servicos
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
    foreach ($modelo in $modelosOllama | Select-Object -Unique) {
        $nome = if ($modelo.Contains(':')) { $modelo } else { "${modelo}:latest" }
        if ($instalados -contains $nome) { Escrever-Ok "Ollama: $modelo" }
        elseif ($SoConferir) { Escrever-Falha "Ollama sem o modelo $modelo" }
        else {
            Write-Host "  Baixando $modelo no Ollama..."
            & docker exec ollama ollama pull $modelo
            if ($LASTEXITCODE -eq 0) { Escrever-Ok "Ollama: $modelo baixado" }
            else { Escrever-Falha "Não consegui baixar $modelo no Ollama." }
        }
    }
}

# ---------------------------------------------------------------------------
if (-not $SoConferir -and $problemas.Count -eq 0) {
    Escrever-Etapa '5. Compilação e conferência final'
    & dotnet build "$raiz\Transcritor.slnx" -nologo -v q
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
        & dotnet run --project "$raiz\src\Transcritor.Cli" --no-build -- servicos
        if ($LASTEXITCODE -ne 0) { Escrever-Falha 'transcritor servicos apontou problemas (acima).' }
    }
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
