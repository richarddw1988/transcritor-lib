using Microsoft.Extensions.Options;
using Transcritor.Core.Gpu;
using Transcritor.Core.Processos;

namespace Transcritor.Arquivo;

/// <summary>
/// Dá a vez na GPU ao processamento de arquivo, uma etapa por vez, e tira o tempo real ocioso do caminho.
/// <list type="bullet">
/// <item>Vez (<see cref="VezGpu"/>, arquivo <see cref="OpcoesArquivo.ArquivoTravaGpu"/>): outro processo que use a lib
/// espera em vez de carregar um segundo modelo junto (o Docker Desktop travou assim, com o Whisper de uma execução e o
/// modelo de 8 GB do Ollama de outra na placa de 12 GB). O tempo real tem prioridade: se uma sessão pediu a vez, o
/// arquivo espera ela fechar antes da próxima etapa.</item>
/// <item>Tempo real ocioso: o WhisperLiveKit segura o modelo enquanto está no ar. Com a vez na mão nenhuma sessão está
/// aberta, então ele é parado (<see cref="OpcoesArquivo.PararTempoRealDuranteArquivo"/>); a próxima sessão o liga de
/// novo.</item>
/// </list>
/// </summary>
public sealed class CoordenadorGpu(IExecutorProcesso executor, IOptions<OpcoesArquivo> opcoes)
{
    private readonly OpcoesArquivo _opcoes = opcoes.Value;

    internal TimeSpan IntervaloEspera { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Pega a vez para uma etapa (esperando, se preciso) e para o tempo real ocioso. Devolve o que solta a vez.</summary>
    /// <param name="aoEsperar">Chamado com quem está com a vez (ou pediu), se for preciso esperar.</param>
    /// <param name="cancellationToken">Cancelamento da espera.</param>
    public async Task<IAsyncDisposable> ReservarAsync(Action<string>? aoEsperar, CancellationToken cancellationToken)
    {
        var vez = _opcoes.CoordenarGpu
            ? await VezGpu.PegarAsync(_opcoes.ArquivoTravaGpu, VezGpu.Dono("arquivo"), prioridade: false, aoEsperar, IntervaloEspera, cancellationToken)
                .ConfigureAwait(false)
            : null;
        try
        {
            if (_opcoes.PararTempoRealDuranteArquivo)
            {
                var conteiner = new ConteinerDocker(executor, _opcoes.CaminhoDocker, _opcoes.ConteinerTempoReal);
                if (await conteiner.NoArAsync(cancellationToken).ConfigureAwait(false) == true)
                {
                    await conteiner.PararAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            return vez ?? Nada.Instancia;
        }
        catch
        {
            if (vez is not null)
            {
                await vez.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
    }

    private sealed class Nada : IAsyncDisposable
    {
        public static readonly Nada Instancia = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
