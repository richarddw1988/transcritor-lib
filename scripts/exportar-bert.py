"""
Converte os modelos BERT da escolha de homófonos para ONNX (lidos pela lib com o ONNX Runtime, sem Python).

Uso (o preparar-ambiente.ps1 roda isto dentro de um contêiner python:3.12-slim; não precisa de Python no Windows):
    python exportar-bert.py <pasta de saída>

Gera, para cada idioma, <saída>/<idioma>/model.onnx e vocab.txt, e confere que o ONNX dá os mesmos logits que o
PyTorch numa frase de teste. Modelos: google-bert/bert-base-cased (inglês) e neuralmind/bert-base-portuguese-cased
(BERTimbau base, português), ambos com cabeça de lacuna (masked LM).
"""
import os
import shutil
import sys
import warnings

# Sem barras de progresso nem o pedido de token do Hugging Face (o download anônimo basta).
os.environ.setdefault('HF_HUB_DISABLE_PROGRESS_BARS', '1')
os.environ.setdefault('HF_HUB_VERBOSITY', 'error')
os.environ.setdefault('TRANSFORMERS_VERBOSITY', 'error')

import torch  # noqa: E402
from huggingface_hub import hf_hub_download  # noqa: E402
from transformers import AutoModelForMaskedLM, AutoTokenizer  # noqa: E402
from transformers.utils import logging as logging_transformers  # noqa: E402

# Avisos da exportação que não se aplicam aqui: o exportador antigo (TorchScript) é o que sai certo para este modelo,
# e os avisos de que o traço pode não generalizar são conferidos abaixo com frases de outro tamanho e com preenchimento.
# O "LOAD REPORT" (pesos do pooler e da previsão da próxima frase sem uso na lacuna) também é esperado.
logging_transformers.set_verbosity_error()
logging_transformers.disable_progress_bar()
warnings.filterwarnings('ignore', category=DeprecationWarning)
warnings.filterwarnings('ignore', message='Converting a tensor to a Python boolean')
warnings.filterwarnings('ignore', message='Exporting aten::index operator')
warnings.filterwarnings('ignore', message='.*unauthenticated requests to the HF Hub')

MODELOS = {
    'en': ('google-bert/bert-base-cased', 'The knight rode through the forest at night.',
           'She did not know whether the weather would change before the long journey home.'),
    'pt': ('neuralmind/bert-base-portuguese-cased', 'A sela do cavalo estava rasgada.',
           'O conserto do relógio ficou pronto antes do concerto de piano da escola.'),
}


class SoLogits(torch.nn.Module):
    """Saída única ("logits"), com as três entradas que a lib passa."""

    def __init__(self, modelo):
        super().__init__()
        self.modelo = modelo

    def forward(self, input_ids, attention_mask, token_type_ids):
        return self.modelo(input_ids=input_ids, attention_mask=attention_mask, token_type_ids=token_type_ids).logits


def exportar(idioma, nome, frase, frase_longa, saida):
    pasta = os.path.join(saida, idioma)
    os.makedirs(pasta, exist_ok=True)
    tok = AutoTokenizer.from_pretrained(nome)
    # Atenção "eager": a otimizada (sdpa) não sai certa no ONNX (logits diferentes do PyTorch).
    # eval() no invólucro também: a exportação devolve o módulo ao modo em que o achou, e em modo de treino o dropout
    # deixaria a comparação abaixo diferente.
    modelo = SoLogits(AutoModelForMaskedLM.from_pretrained(nome, attn_implementation='eager')).eval()
    ent = tok([frase, frase], return_tensors='pt')
    args = (ent['input_ids'], ent['attention_mask'], ent['token_type_ids'])
    destino = os.path.join(pasta, 'model.onnx')
    lote, tamanho = 'lote', 'tamanho'
    torch.onnx.export(
        modelo, args, destino,
        input_names=['input_ids', 'attention_mask', 'token_type_ids'],
        output_names=['logits'],
        dynamic_axes={n: {0: lote, 1: tamanho} for n in ['input_ids', 'attention_mask', 'token_type_ids', 'logits']},
        opset_version=17,
        dynamo=False,
    )
    # vocab.txt do próprio repositório do modelo (WordPiece, com maiúsculas e acentos).
    shutil.copyfile(hf_hub_download(nome, 'vocab.txt'), os.path.join(pasta, 'vocab.txt'))

    # Conferência com entradas diferentes das do traço: uma frase mais longa e a curta preenchida até o tamanho dela.
    import onnxruntime as ort
    sessao = ort.InferenceSession(destino, providers=['CPUExecutionProvider'])
    teste = tok([frase_longa, frase], padding=True, return_tensors='pt')
    onnx_logits = sessao.run(None, {k: v.numpy() for k, v in teste.items()})[0]
    with torch.no_grad():
        torch_logits = modelo.eval()(teste['input_ids'], teste['attention_mask'], teste['token_type_ids']).numpy()
    reais = teste['attention_mask'].numpy().astype(bool)
    dif = abs(onnx_logits - torch_logits)[reais].max()
    print(f'{idioma}: {destino} ({os.path.getsize(destino) / 1e6:.0f} MB); diferença máxima ONNX x PyTorch: {dif:.2e}')
    if dif > 1e-2:
        sys.exit(f'{idioma}: o ONNX não bate com o PyTorch')


def main():
    if len(sys.argv) != 2:
        sys.exit('Uso: python exportar-bert.py <pasta de saída>')
    saida = sys.argv[1]
    for idioma, (nome, frase, frase_longa) in MODELOS.items():
        exportar(idioma, nome, frase, frase_longa, saida)


if __name__ == '__main__':
    main()
