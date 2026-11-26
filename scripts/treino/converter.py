# Junta o adaptador LoRA ao whisper-large-v3 e converte para CTranslate2 (formato do faster-whisper/Speaches).
# Uso: python converter.py /adaptador /saida_ct2
import shutil, subprocess, sys, torch
from transformers import WhisperForConditionalGeneration, WhisperProcessor
from peft import PeftModel
adaptador, saida = sys.argv[1], sys.argv[2]
hf = '/tmp/junto'
m = WhisperForConditionalGeneration.from_pretrained('openai/whisper-large-v3', torch_dtype=torch.float16)
m = PeftModel.from_pretrained(m, adaptador).merge_and_unload()
m.save_pretrained(hf)
from huggingface_hub import snapshot_download
orig = snapshot_download('openai/whisper-large-v3', allow_patterns=['*.json', '*.txt'])
for f in ['tokenizer.json', 'preprocessor_config.json', 'tokenizer_config.json', 'vocab.json', 'merges.txt', 'special_tokens_map.json', 'added_tokens.json', 'normalizer.json']:
    shutil.copy(f'{orig}/{f}', hf)
subprocess.run(['ct2-transformers-converter', '--model', hf, '--output_dir', saida, '--force', '--quantization', 'float16',
                '--copy_files', 'tokenizer.json', 'preprocessor_config.json'], check=True)
print('convertido em', saida)
