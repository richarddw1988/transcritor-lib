<#
.SYNOPSIS
    Grava, pelo microfone, cada frase de docs/avaliacao/frases-para-gravar.json, para medir os erros de
    reconhecimento da sua fala (transcritor avaliar-fala).

.DESCRIPTION
    Para cada frase: mostra o texto, espera Enter, avisa quando começou a gravar, grava até você apertar Enter de novo
    (no máximo 2 s por palavra + 10 s) e salva em dados/gravacoes/<id>.wav (a pasta dados/ não vai para o Git).
    Frases já gravadas são puladas, a não ser com -Regravar ou -Somente.
    Fale com naturalidade, no seu ritmo e no lugar onde vai usar a lib; erros são justamente o que queremos medir.

.PARAMETER Frases
    JSON com as frases (id, idioma, texto).

.PARAMETER Pasta
    Onde salvar os WAV.

.PARAMETER Regravar
    Grava de novo as frases que já têm WAV.

.PARAMETER Somente
    Grava (ou regrava) só estas frases, pelo id: -Somente en31,en32.

.EXAMPLE
    .\scripts\gravar-frases.ps1

.EXAMPLE
    .\scripts\gravar-frases.ps1 -Somente en31,en32
#>
[CmdletBinding()]
param(
    [string] $Frases = 'docs/avaliacao/frases-para-gravar.json',
    [string] $Pasta = 'dados/gravacoes',
    [switch] $Regravar,
    [string[]] $Somente = @()
)

$ErrorActionPreference = 'Stop'
$raiz = Split-Path -Parent $PSScriptRoot
Set-Location $raiz
New-Item -ItemType Directory -Force $Pasta | Out-Null

& dotnet build "$raiz\src\Transcritor.Cli" -nologo -v q | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'O Cli não compilou (dotnet build src/Transcritor.Cli).' }

# No Windows PowerShell 5.1, o ConvertFrom-Json devolve o array como um item só; o ForEach-Object o desfaz.
$lista = @(Get-Content -Raw -Encoding UTF8 $Frases | ConvertFrom-Json | ForEach-Object { $_ })
if ($Somente.Count -gt 0) {
    $desconhecidos = @($Somente | Where-Object { $_ -notin $lista.id })
    if ($desconhecidos.Count -gt 0) { throw "Ids que não estão em ${Frases}: $($desconhecidos -join ', ')" }
    $lista = @($lista | Where-Object { $_.id -in $Somente })
}
$n = 0
foreach ($f in $lista) {
    $n++
    $wav = Join-Path $Pasta "$($f.id).wav"
    if ((Test-Path $wav) -and -not $Regravar -and $Somente.Count -eq 0) { Write-Host "[$n/$($lista.Count)] $($f.id): já gravada (use -Regravar para refazer)"; continue }

    # Grava até o Enter; o máximo (2 s por palavra + 10 s) só evita gravar para sempre se esquecer o Enter.
    $maximo = [math]::Ceiling(($f.texto -split '\s+').Count * 2) + 10
    Write-Host ''
    Write-Host "[$n/$($lista.Count)] $($f.id) ($($f.idioma))" -ForegroundColor Cyan
    Write-Host "    $($f.texto)" -ForegroundColor White
    Read-Host '    Enter para gravar (Ctrl+C sai); espere aparecer "Gravando: pode falar" e aperte Enter quando terminar' | Out-Null
    # Mostra só o aviso de início: falar antes dele corta a primeira palavra.
    & dotnet run --project "$raiz\src\Transcritor.Cli" --no-build -- captura --fonte Microfone --segundos $maximo --ate-enter --wav $wav |
        Where-Object { $_ -like '*>>>*' } | ForEach-Object { Write-Host $_ -ForegroundColor Yellow }
    if ($LASTEXITCODE -ne 0) { throw "A gravação de $($f.id) falhou." }
    # Pico do áudio: abaixo de ~10% o reconhecimento piora (aproxime-se ou suba o volume do microfone).
    $bytes = [IO.File]::ReadAllBytes((Resolve-Path $wav))
    $pico = 0
    for ($i = 44; $i + 1 -lt $bytes.Length; $i += 2) { $pico = [math]::Max($pico, [math]::Abs([int][BitConverter]::ToInt16($bytes, $i))) }
    $pct = [math]::Round(100 * $pico / 32767)
    $cor = if ($pct -lt 10) { 'Yellow' } else { 'Green' }
    Write-Host "    Salvo: $wav (pico $pct%$(if ($pct -lt 10) { ' - baixo: aproxime-se ou suba o volume do microfone' }))" -ForegroundColor $cor
}

Write-Host ''
Write-Host 'Pronto. Para medir:  dotnet run --project src/Transcritor.Cli -- avaliar-fala' -ForegroundColor Green
