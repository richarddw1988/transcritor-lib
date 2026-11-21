<#
.SYNOPSIS
    Grava, pelo microfone, cada frase de docs/avaliacao/frases-para-gravar.json, para medir os erros de
    reconhecimento da sua fala (transcritor avaliar-fala).

.DESCRIPTION
    Para cada frase: mostra o texto, espera Enter, grava alguns segundos e salva em dados/gravacoes/<id>.wav
    (a pasta dados/ não vai para o Git). Frases já gravadas são puladas, a não ser com -Regravar.
    Fale com naturalidade, no seu ritmo e no lugar onde vai usar a lib; erros são justamente o que queremos medir.

.PARAMETER Frases
    JSON com as frases (id, idioma, texto).

.PARAMETER Pasta
    Onde salvar os WAV.

.PARAMETER Regravar
    Grava de novo as frases que já têm WAV.

.EXAMPLE
    .\scripts\gravar-frases.ps1
#>
[CmdletBinding()]
param(
    [string] $Frases = 'docs/avaliacao/frases-para-gravar.json',
    [string] $Pasta = 'dados/gravacoes',
    [switch] $Regravar
)

$ErrorActionPreference = 'Stop'
$raiz = Split-Path -Parent $PSScriptRoot
Set-Location $raiz
New-Item -ItemType Directory -Force $Pasta | Out-Null

& dotnet build "$raiz\src\Transcritor.Cli" -nologo -v q | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'O Cli não compilou (dotnet build src/Transcritor.Cli).' }

$lista = Get-Content -Raw -Encoding UTF8 $Frases | ConvertFrom-Json
$n = 0
foreach ($f in $lista) {
    $n++
    $wav = Join-Path $Pasta "$($f.id).wav"
    if ((Test-Path $wav) -and -not $Regravar) { Write-Host "[$n/$($lista.Count)] $($f.id): já gravada (use -Regravar para refazer)"; continue }

    # ~0,45 s por palavra + folga para começar e terminar.
    $segundos = [math]::Ceiling(($f.texto -split '\s+').Count * 0.45) + 3
    Write-Host ''
    Write-Host "[$n/$($lista.Count)] $($f.id) ($($f.idioma)) - $segundos s" -ForegroundColor Cyan
    Write-Host "    $($f.texto)" -ForegroundColor White
    Read-Host '    Enter para gravar (Ctrl+C sai)' | Out-Null
    & dotnet run --project "$raiz\src\Transcritor.Cli" --no-build -- captura --fonte Microfone --segundos $segundos --wav $wav | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "A gravação de $($f.id) falhou." }
    Write-Host "    Salvo: $wav" -ForegroundColor Green
}

Write-Host ''
Write-Host 'Pronto. Para medir:  dotnet run --project src/Transcritor.Cli -- avaliar-fala' -ForegroundColor Green
