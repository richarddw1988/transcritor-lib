# Monta pares (áudio, texto) para treinar o Whisper a partir de um filme: legenda SDH + voz separada + transcrição da lib.
# Junta legendas seguidas (pausa < 1 s) em trechos de até 28 s; o texto é o da legenda, limpo (sem [ruídos], música, nomes
# de quem fala). Cada trecho leva o erro do Whisper contra a legenda, para filtrar legenda que não bate com o áudio.
# Uso: python montar_pares.py legenda.srt voz16k.wav transcricao.json pasta_saida [--gravar]
import json, os, re, sys, wave
src = open(os.path.join(os.path.dirname(os.path.abspath(__file__)), 'filme_wer.py'), encoding='utf-8').read().split('import os')[0]
exec(src)
srt, wav, trans, saida = sys.argv[1:5]
gravar = '--gravar' in sys.argv
palavras = [(p['inicio'], p['fim'], p['texto']) for f in json.load(open(trans, encoding='utf-8'))['frases'] for p in f['palavras']]

def texto_treino(t):
    t = limpar(t)
    t = re.sub(r'\s+', ' ', t).strip()
    return t

trechos = []
for ini, fim, texto in ler_srt(srt):
    t = texto_treino(texto)
    if not t:
        trechos.append(None)   # legenda só de ruído/música: quebra a sequência
        continue
    if trechos and trechos[-1] and ini - trechos[-1][1] < 1.0 and fim - trechos[-1][0] <= 28:
        trechos[-1] = (trechos[-1][0], fim, trechos[-1][2] + ' ' + t)
    else:
        trechos.append((ini, fim, t))
trechos = [t for t in trechos if t]

w = wave.open(wav); taxa = w.getframerate()
os.makedirs(saida, exist_ok=True)
linhas = []; faixas = {'0%': [0, 0.0], 'até 20%': [0, 0.0], '20-50%': [0, 0.0], 'mais de 50%': [0, 0.0]}
for n, (ini, fim, texto) in enumerate(trechos):
    ref = toks(texto)
    hyp = toks(' '.join(p[2] for p in palavras if ini - 0.7 <= (p[0] + p[1]) / 2 <= fim + 0.7))
    erro = ed(ref, hyp) / max(1, len(ref))
    a, b = max(0, ini - 0.3), fim + 0.3
    f = '0%' if erro == 0 else 'até 20%' if erro <= 0.2 else '20-50%' if erro <= 0.5 else 'mais de 50%'
    faixas[f][0] += 1; faixas[f][1] += b - a
    nome = f'{n:05d}.wav'
    linhas.append({'audio': nome, 'inicio': round(a, 2), 'fim': round(b, 2), 'texto': texto, 'erro_whisper': round(erro, 3), 'palavras': len(ref)})
    if gravar:
        w.setpos(int(a * taxa)); dados = w.readframes(int((b - a) * taxa))
        o = wave.open(os.path.join(saida, nome), 'wb'); o.setnchannels(1); o.setsampwidth(2); o.setframerate(taxa); o.writeframes(dados); o.close()
json.dump(linhas, open(os.path.join(saida, 'pares.json'), 'w', encoding='utf-8'), ensure_ascii=False, indent=0)
total = sum(l['fim'] - l['inicio'] for l in linhas)
print(f'{len(linhas)} trechos, {total / 60:.1f} min de áudio, {sum(l["palavras"] for l in linhas)} palavras')
for k, (q, d) in faixas.items():
    print(f'  erro do Whisper {k}: {q} trechos, {d / 60:.1f} min')
dur = sorted(l['fim'] - l['inicio'] for l in linhas)
print(f'  duração: mediana {dur[len(dur) // 2]:.1f} s, máxima {dur[-1]:.1f} s; até 3 palavras: {sum(1 for l in linhas if l["palavras"] <= 3)} trechos')
