namespace Transcritor.Dicionario;

/// <summary>De onde veio o verbete.</summary>
public enum TipoVerbete
{
    /// <summary>Tradução direta (palavra ou expressão no outro idioma), com o sentido a que se refere.</summary>
    Traducao,

    /// <summary>Definição curta escrita no outro idioma (ex.: "pêssego" → "peach (fruit)").</summary>
    Definicao,
}

/// <summary>Um sentido de um termo no dicionário.</summary>
/// <param name="Termo">Termo como está no dicionário (pode ser a forma base de <c>Lema</c>).</param>
/// <param name="Lema">
/// Forma base quando o termo procurado era uma flexão ("gave" → "give", "desisti" → "desistir"); nulo quando o
/// termo foi achado como está.
/// </param>
/// <param name="Classe">Classe gramatical em português (substantivo, verbo, expressão...).</param>
/// <param name="Traducao">Tradução, ou definição quando <paramref name="Tipo"/> é <see cref="TipoVerbete.Definicao"/>.</param>
/// <param name="Sentido">Sentido a que a tradução se refere, em inglês, como está no Wiktionary; nulo se não informado.</param>
/// <param name="Tipo">Tradução direta ou definição.</param>
public sealed record Verbete(string Termo, string? Lema, string Classe, string Traducao, string? Sentido, TipoVerbete Tipo);
