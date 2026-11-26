# Erro do Whisper (transformers, fp16) nos pares de uma pasta, com ou sem adaptador LoRA. Roda na imagem de treino.
# Uso: python avaliar.py /pares SAIDA.json [/adaptador] [--metade2]
#   --metade2: só a 2ª metade do filme (pela ordem de tempo), a que o treino não viu.
# Contra os loops nas falas curtas (a 1ª avaliação deu 177% de erro nelas): limite de tokens pela duração do trecho
# e, como o faster-whisper do Speaches faz, refaz com temperatura quando o texto sai repetitivo (compressão gzip > 2,4)
# ou bate no limite. Lote de 4 para caber nos 12 GB (lote 16 com 5 hipóteses pediu 24 GB).
import gzip, json, re, sys, time, wave
import numpy as np, torch
from transformers import WhisperForConditionalGeneration, WhisperProcessor

args = [a for a in sys.argv[1:] if not a.startswith('--')]
pasta, arquivo = args[0], args[1]; adaptador = args[2] if len(args) > 2 else None
torch.cuda.set_per_process_memory_fraction(11 * 2**30 / torch.cuda.get_device_properties(0).total_memory)
torch.manual_seed(0)
proc = WhisperProcessor.from_pretrained('openai/whisper-large-v3')
modelo = WhisperForConditionalGeneration.from_pretrained('openai/whisper-large-v3', torch_dtype=torch.float16).to('cuda')
if adaptador:
    from peft import PeftModel
    modelo = PeftModel.from_pretrained(modelo, adaptador).merge_and_unload()
modelo.eval()

def norm(w): return re.sub(r"[^\w'\-]", '', w.lower()).strip("'-")
def toks(t): return [x for x in (norm(w) for w in t.replace('—', ' ').replace('...', ' ').split()) if x]
def ed(a, b):
    p = list(range(len(b) + 1))
    for i, x in enumerate(a, 1):
        c = [i]
        for j, y in enumerate(b, 1): c.append(min(p[j] + 1, c[j - 1] + 1, p[j - 1] + (x != y)))
        p = c
    return p[-1]
def ler(c):
    with wave.open(c) as w: return np.frombuffer(w.readframes(w.getnframes()), dtype=np.int16).astype(np.float32) / 32768
def compressao(t):
    b = t.strip().encode('utf-8')
    return len(b) / len(gzip.compress(b)) if b else 0.0

pares = sorted(json.load(open(f'{pasta}/pares.json', encoding='utf-8')), key=lambda p: p['inicio'])
if '--metade2' in sys.argv:
    pares = pares[len(pares) // 2:]
ini = time.time(); erros = n = refeitos = 0; saida = []
for k in range(0, len(pares), 4):
    lote = pares[k:k + 4]
    audios = [ler(f'{pasta}/{p["audio"]}') for p in lote]
    x = proc(audios, sampling_rate=16000, return_tensors='pt').input_features.to('cuda', torch.float16)
    limite = min(200, int(max(len(a) for a in audios) / 16000 * 8) + 12)
    with torch.no_grad():
        ids = modelo.generate(x, language='en', task='transcribe', max_new_tokens=limite, num_beams=5)
    textos = proc.batch_decode(ids, skip_special_tokens=True)
    for i, a in enumerate(audios):
        lim = min(200, int(len(a) / 16000 * 8) + 12)
        ruim = lambda t, n_ids: compressao(t) > 2.4 or n_ids >= lim
        n_ids = int((ids[i] != proc.tokenizer.pad_token_id).sum()) - 4
        if not ruim(textos[i], n_ids):
            continue
        refeitos += 1
        for temp in (0.2, 0.4, 0.6, 0.8, 1.0):
            with torch.no_grad():
                r = modelo.generate(x[i:i + 1], language='en', task='transcribe', max_new_tokens=lim, do_sample=True, temperature=temp)
            t = proc.batch_decode(r, skip_special_tokens=True)[0]
            if not ruim(t, r.shape[1] - 4):
                textos[i] = t; break
    for p, t in zip(lote, textos):
        r, h = toks(p['texto']), toks(t)
        e = ed(r, h); erros += e; n += len(r)
        saida.append({'audio': p['audio'], 'texto': p['texto'], 'saiu': t.strip(), 'erros': e, 'palavras': len(r),
                      'erro_lib': p['erro_whisper']})
json.dump(saida, open(f'{pasta}/{arquivo}', 'w', encoding='utf-8'), ensure_ascii=False, indent=0)
lib = sum(round(s['erro_lib'] * s['palavras']) for s in saida)
print(f'{len(pares)} trechos, {n} palavras, erro {100 * erros / n:.2f}% (lib {100 * lib / n:.2f}%), {refeitos} refeitos, '
      f'{time.time() - ini:.0f} s, pico VRAM {torch.cuda.max_memory_allocated() / 2**30:.2f} GB', flush=True)
