# Speaches 0.8.3 com uma correção, usada pelo docker-compose.yml do projeto.
#
# Na 0.8.3, DELETE /api/ps/{modelo} tira o Whisper da GPU, mas trava o serviço: o gerenciador de modelos pega a
# própria trava (threading.Lock) duas vezes na mesma linha de execução, a resposta não volta e as transcrições
# seguintes ficam penduradas até reiniciar o contêiner. Com threading.RLock a segunda tomada passa.
# A 0.9.0-rc.3 já corrige isso, mas transcreve diferente (corta o áudio por VAD e transcreve os pedaços em lote,
# sem o texto anterior como contexto: perde pontuação e maiúsculas) e não devolve a confiança das palavras.
#
# O /health passa a responder com o cabeçalho X-Transcritor-Descarregar: 1. A lib só manda o DELETE quando vê esse
# cabeçalho; com a imagem original ela apenas espera o tempo ocioso (WHISPER__TTL).
FROM ghcr.io/speaches-ai/speaches:0.8.3-cuda-12.6.3

RUN set -e; \
    gerenciador=/home/ubuntu/speaches/src/speaches/executors/whisper/model_manager.py; \
    sed -i 's/self._lock = threading.Lock()/self._lock = threading.RLock()/' "$gerenciador"; \
    grep -q 'self._lock = threading.RLock()' "$gerenciador"; \
    rotas=/home/ubuntu/speaches/src/speaches/routers/misc.py; \
    sed -i 's/return Response(status_code=200, content="OK")/return Response(status_code=200, content="OK", headers={"X-Transcritor-Descarregar": "1"})/' "$rotas"; \
    grep -q 'X-Transcritor-Descarregar' "$rotas"
