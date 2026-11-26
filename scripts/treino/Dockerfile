# Ambiente de treino (LoRA do Whisper) sobre a imagem do separador, que já traz PyTorch 2.5.1 + CUDA 12.4.
FROM transcritor/separador:demucs-4.0.1
RUN pip install --no-cache-dir transformers==4.47.1 peft==0.14.0 accelerate==1.2.1 datasets==3.2.0 jiwer==3.0.5 ctranslate2==4.6.0 huggingface_hub==0.27.0
ENTRYPOINT []
CMD ["python"]
