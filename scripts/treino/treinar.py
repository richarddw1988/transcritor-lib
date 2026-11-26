# Treino LoRA do Whisper large-v3 nos pares de uma pasta (pares.json + WAVs). Roda na imagem de treino.
# Uso: python treinar.py /saida EPOCAS /pares1[:metade1] [/pares2 ...] [--teste N]
#   Treina com todos os pares das pastas; ":metade1" usa só a 1ª metade daquela pasta (pela ordem de tempo),
#   deixando a 2ª para medir (avaliar.py --metade2).
#   --teste N: só N passos de um trecho, com os trechos de texto mais longo (pior caso de memória), sem salvar.
# Memória: modelo em fp16 congelado, LoRA em fp32 com autocast, recálculo na volta (gradient checkpointing),
# 1 trecho por vez acumulando ACUMULAR. A GPU fica limitada a 11 GB para dar erro em vez de transbordar
# para a memória comum do Windows (o que deixou a avaliação 10x mais lenta).
import json, random, sys, time, wave
import numpy as np, torch
from transformers import WhisperForConditionalGeneration, WhisperProcessor
from peft import LoraConfig, get_peft_model

saida, epocas = sys.argv[1], int(sys.argv[2])
pastas = [a for a in sys.argv[3:] if a.startswith('/')]
teste = int(sys.argv[sys.argv.index('--teste') + 1]) if '--teste' in sys.argv else 0
ACUMULAR, LR = 8, 5e-4
torch.cuda.set_per_process_memory_fraction(11 / torch.cuda.get_device_properties(0).total_memory * 2**30)
random.seed(0); torch.manual_seed(0)

proc = WhisperProcessor.from_pretrained('openai/whisper-large-v3')
proc.tokenizer.set_prefix_tokens(language='en', task='transcribe')
modelo = WhisperForConditionalGeneration.from_pretrained('openai/whisper-large-v3', torch_dtype=torch.float16).to('cuda')
modelo.config.use_cache = False
modelo.gradient_checkpointing_enable(gradient_checkpointing_kwargs={'use_reentrant': False})
modelo.enable_input_require_grads()
modelo = get_peft_model(modelo, LoraConfig(r=32, lora_alpha=64, lora_dropout=0.05,
                                           target_modules=['q_proj', 'k_proj', 'v_proj', 'out_proj', 'fc1', 'fc2']))
for p in modelo.parameters():
    if p.requires_grad: p.data = p.data.float()
modelo.print_trainable_parameters()
print('VRAM carregado (GB):', round(torch.cuda.memory_allocated() / 2**30, 2), flush=True)

def ler(c):
    with wave.open(c) as w: return np.frombuffer(w.readframes(w.getnframes()), dtype=np.int16).astype(np.float32) / 32768

treino = []
for item in pastas:
    pasta, _, parte = item.partition(':')
    pares = sorted(json.load(open(f'{pasta}/pares.json', encoding='utf-8')), key=lambda p: p['inicio'])
    if parte == 'metade1': pares = pares[:len(pares) // 2]
    treino += [dict(p, audio=f'{pasta}/{p["audio"]}') for p in pares]
    print(f'{item}: {len(pares)} trechos, {sum(p["fim"] - p["inicio"] for p in pares) / 60:.1f} min', flush=True)
if teste:
    treino = sorted(treino, key=lambda p: -len(p['texto']))[:teste]
print(f'treino: {len(treino)} trechos', flush=True)

inicio_id = modelo.config.decoder_start_token_id
def exemplo(p):
    x = proc(ler(p['audio']), sampling_rate=16000, return_tensors='pt').input_features.to('cuda', torch.float16)
    ids = proc.tokenizer(p['texto']).input_ids
    if ids[0] == inicio_id: ids = ids[1:]
    return x, torch.tensor([ids], device='cuda')

treinaveis = [p for p in modelo.parameters() if p.requires_grad]
otim = torch.optim.AdamW(treinaveis, lr=LR, weight_decay=0.01)
total = max(1, (len(treino) * (1 if teste else epocas)) // ACUMULAR)
aquecer = max(1, total // 10)
agenda = torch.optim.lr_scheduler.LambdaLR(otim, lambda s: min((s + 1) / aquecer, max(0.0, (total - s) / max(1, total - aquecer))))
escala = torch.amp.GradScaler('cuda')
modelo.train(); torch.cuda.reset_peak_memory_stats()
ini = time.time(); passo = 0; soma = 0.0
for epoca in range(1 if teste else epocas):
    ordem = treino[:] if teste else random.sample(treino, len(treino))
    for i, p in enumerate(ordem, 1):
        x, y = exemplo(p)
        with torch.autocast('cuda', dtype=torch.float16):
            perda = modelo(input_features=x, labels=y).loss / ACUMULAR
        escala.scale(perda).backward(); soma += perda.item()
        if teste:
            print(f'trecho {i}: {len(y[0])} tokens, {time.time() - ini:.1f} s, pico VRAM {torch.cuda.max_memory_allocated() / 2**30:.2f} GB', flush=True)
        if i % ACUMULAR == 0 or i == len(ordem):
            escala.unscale_(otim); torch.nn.utils.clip_grad_norm_(treinaveis, 1.0)
            escala.step(otim); escala.update(); otim.zero_grad(set_to_none=True); agenda.step(); passo += 1
            if not teste and passo % 5 == 0:
                print(f'época {epoca + 1} passo {passo}/{total}: perda {soma / 5:.3f}, {time.time() - ini:.0f} s, '
                      f'pico VRAM {torch.cuda.max_memory_allocated() / 2**30:.2f} GB', flush=True)
                soma = 0.0
print(f'fim: {time.time() - ini:.0f} s, {(time.time() - ini) / (len(treino) * (1 if teste else epocas)):.2f} s por trecho, '
      f'pico VRAM {torch.cuda.max_memory_allocated() / 2**30:.2f} GB, reservado {torch.cuda.max_memory_reserved() / 2**30:.2f} GB', flush=True)
if not teste:
    modelo.save_pretrained(saida); print('adaptador salvo em', saida)
