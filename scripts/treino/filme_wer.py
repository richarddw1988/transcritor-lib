# Compara a transcrição da lib (JSON) com a legenda SDH (SRT), trecho a trecho pelo tempo.
# Uso: python filme_wer.py legenda.srt transcricao.json [nomes.txt]
import json, re, sys
from collections import Counter

def ler_srt(caminho):
    blocos = re.split(r'\n\s*\n', open(caminho, encoding='utf-8-sig').read().replace('\r', ''))
    saida = []
    for b in blocos:
        linhas = b.strip().split('\n')
        if len(linhas) < 3 or '-->' not in linhas[1]:
            continue
        def seg(t):
            h, m, s = t.strip().replace(',', '.').split(':')
            return int(h) * 3600 + int(m) * 60 + float(s)
        ini, fim = (seg(x) for x in linhas[1].split('-->'))
        texto = ' '.join(linhas[2:])
        saida.append((ini, fim, texto))
    return saida

def limpar(texto):
    t = re.sub(r'\{[^}]*\}', ' ', texto)          # {\an8}
    t = re.sub(r'<[^>]*>', ' ', t)                 # <i>
    t = re.sub(r'\[[^\]]*\]', ' ', t)              # [música], [Jerrod]
    t = re.sub(r'\([^)]*\)', ' ', t)
    if '♪' in t:
        return ''                                   # letra de música
    t = re.sub(r'^\s*-\s*', ' ', t)
    t = re.sub(r'\s-\s', ' ', t)
    return t

def norm(w):
    return re.sub(r"[^\w'\-]", '', w.lower()).strip("'-")

def toks(t):
    return [x for x in (norm(w) for w in t.replace('—', ' ').replace('...', ' ').split()) if x]

def ed(a, b):
    p = list(range(len(b) + 1))
    for i, x in enumerate(a, 1):
        c = [i]
        for j, y in enumerate(b, 1):
            c.append(min(p[j] + 1, c[j - 1] + 1, p[j - 1] + (x != y)))
        p = c
    return p[-1]

import os
DEBUG = os.environ.get("DEBUG")
legenda = ler_srt(sys.argv[1])
trans = json.load(open(sys.argv[2], encoding='utf-8'))
palavras = [(p['inicio'], p['fim'], p['texto']) for f in trans['frases'] for p in f['palavras']]
nomes = [l.strip() for l in open(sys.argv[3], encoding='utf-8')] if len(sys.argv) > 3 else []
nomes_norm = {norm(n): n for nome in nomes for n in nome.split() if norm(n)}

# Trechos: legendas seguidas com menos de 2 s entre elas.
trechos = []
for ini, fim, texto in legenda:
    if trechos and ini - trechos[-1][1] < 2.0:
        trechos[-1] = (trechos[-1][0], fim, trechos[-1][2] + ' ' + limpar(texto))
    else:
        trechos.append((ini, fim, limpar(texto)))

erros = n_ref = 0
usadas = set()
nomes_ref = nomes_ok = 0
faltou = Counter()
for ini, fim, texto in trechos:
    ref = toks(texto)
    hyp_idx = [i for i, (a, b, _) in enumerate(palavras) if ini - 0.7 <= (a + b) / 2 <= fim + 0.7 and i not in usadas]
    usadas.update(hyp_idx)
    hyp = toks(' '.join(palavras[i][2] for i in hyp_idx))
    e = ed(ref, hyp); erros += e
    if DEBUG and len(ref) > 0 and e / len(ref) > 0.5: print(round(ini), e, len(ref), "|", " ".join(ref)[:150], "||", " ".join(hyp)[:150])
    n_ref += len(ref)
    hs = set(hyp)
    for w in ref:
        if w in nomes_norm:
            nomes_ref += 1
            if w in hs:
                nomes_ok += 1
            else:
                faltou[nomes_norm[w]] += 1

fora = [palavras[i][2] for i in range(len(palavras)) if i not in usadas]
print(f"palavras da legenda: {n_ref}; da transcrição: {len(palavras)} ({len(fora)} fora de qualquer legenda)")
print(f"WER por trecho (sem as palavras fora): {erros / n_ref:.2%}")
print(f"WER contando as de fora como sobras: {(erros + len(fora)) / n_ref:.2%}")
if nomes:
    print(f"nomes da lista na legenda: {nomes_ref}; escritos certo: {nomes_ok} ({nomes_ok / max(1, nomes_ref):.0%})")
    print("mais errados:", faltou.most_common(15))
print("exemplo de fora:", ' '.join(fora[:60]))
