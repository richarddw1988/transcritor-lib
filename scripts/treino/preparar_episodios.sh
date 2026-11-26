# Prepara vídeos com legenda SDH em inglês embutida (mkv) para o treino: para cada vídeo, extrai a legenda
# "English [SDH]" (ou a 1ª em inglês), separa a voz da faixa em inglês no mesmo separador da lib (partes de 600 s,
# 44,1 kHz mono → voz 16 kHz) e grava em DESTINO/<nome>/: en-sdh.srt e voz16k.wav.
# Depois: transcrever os vídeos com a lib (--idioma en --saida Ingles) e rodar montar_pares.py em cada um.
# Uso (Git Bash): bash preparar_episodios.sh DESTINO video1.mkv [video2.mkv ...]
set -e
D="$1"; shift
docker start separador >/dev/null
until curl -s localhost:8002/health >/dev/null; do sleep 2; done
for f in "$@"; do
  nome=$(basename "$f"); nome="${nome%.*}"; o="$D/$nome"; mkdir -p "$o"
  si=$(ffprobe -v error -select_streams s -show_entries stream=index:stream_tags=title,language -of csv=p=0 "$f" | grep -n 'SDH' | head -1 | cut -d: -f1)
  [ -z "$si" ] && si=$(ffprobe -v error -select_streams s -show_entries stream_tags=language -of csv=p=0 "$f" | grep -n 'eng' | head -1 | cut -d: -f1)
  [ -z "$si" ] && { echo "$nome: sem legenda em inglês, pulado"; continue; }
  ffmpeg -v error -y -i "$f" -map 0:s:$((si-1)) "$o/en-sdh.srt"
  ffmpeg -v error -y -i "$f" -map 0:a:m:language:eng -ac 1 -ar 44100 "$o/en44.wav"
  dur=$(ffprobe -v error -show_entries format=duration -of csv=p=0 "$o/en44.wav")
  rm -f "$o"/parte*.wav; i=0
  for ini in $(seq 0 600 ${dur%.*}); do
    ffmpeg -v error -y -ss $ini -t 600 -i "$o/en44.wav" -c:a pcm_s16le "$o/in.wav"
    curl -sf --data-binary @"$o/in.wav" -H 'Content-Type: audio/wav' localhost:8002/separar -o "$o/parte$(printf %02d $i).wav"; i=$((i+1))
  done
  ls "$o"/parte*.wav | sed "s#.*/#file '#; s#\$#'#" > "$o/lista.txt"
  (cd "$o" && ffmpeg -v error -y -f concat -safe 0 -i lista.txt -c copy voz16k.wav)
  rm -f "$o"/parte*.wav "$o/in.wav" "$o/en44.wav" "$o/lista.txt"
  echo "$nome: $(grep -c -- '-->' "$o/en-sdh.srt") legendas, voz de $(ffprobe -v error -show_entries format=duration -of csv=p=0 "$o/voz16k.wav") s"
done
curl -s -X POST localhost:8002/descarregar >/dev/null || true
docker stop separador >/dev/null
