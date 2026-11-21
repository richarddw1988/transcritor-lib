namespace Transcritor.Dicionario;

/// <summary>Palavra do dicionário que soa parecido com a procurada.</summary>
/// <param name="Termo">Palavra do dicionário (minúsculas).</param>
/// <param name="DistanciaPronuncia">0 = mesma chave de pronúncia; 1 = uma letra diferente na chave.</param>
/// <param name="DistanciaEscrita">Letras diferentes na escrita (Levenshtein) em relação à palavra procurada.</param>
/// <param name="Sentidos">Quantos verbetes a palavra tem (aproximação de quão comum ela é).</param>
public sealed record Parecida(string Termo, int DistanciaPronuncia, int DistanciaEscrita, int Sentidos);
