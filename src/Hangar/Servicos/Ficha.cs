using System.IO;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.EventEmitters;
using YamlDotNet.Serialization.NamingConventions;

namespace Hangar.Servicos;

/// <summary>
/// A ficha do projeto (hangar.yaml na pasta dele). Fica dentro do projeto de propósito:
/// viaja junto na migração e entra no git. O Hangar não guarda nada fora das fichas.
/// </summary>
public sealed class Ficha
{
    public const string Arquivo = "hangar.yaml";

    public string? Nome { get; set; }
    public string? Resumo { get; set; }
    /// <summary>Trabalho | Pessoal | Estudos</summary>
    public string? Area { get; set; }
    public string? Tecnologia { get; set; }
    /// <summary>Em que pé o projeto está (texto livre, entra no prompt).</summary>
    public string? Situacao { get; set; }
    public Links? Links { get; set; }
    /// <summary>Comandos (fase 2/3), rodados na pasta do projeto.</summary>
    public string? Ligar { get; set; }
    public string? Desligar { get; set; }
    public string? Publicar { get; set; }
    public string? RetirarServidor { get; set; }
    /// <summary>Publicado no mini servidor (manual até a fase 3 checar sozinho).</summary>
    public bool Servidor { get; set; }
    /// <summary>Nome do projeto no Docker Compose, se diferente do nome da pasta.</summary>
    public string? DockerProjeto { get; set; }
    public string? Anotacoes { get; set; }
    /// <summary>Memórias do Claude Code deste projeto (nomes de arquivo, ex.: project_frota.md), indicadas no "Abrir Claude aqui".</summary>
    public List<string>? Memorias { get; set; }
    public DateTime? ArquivadoEm { get; set; }

    private static readonly IDeserializer Leitor = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private static readonly ISerializer Escritor = new SerializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull | DefaultValuesHandling.OmitDefaults)
        .WithEventEmitter(proximo => new TextoEmBloco(proximo))
        .Build();

    /// <summary>Lê a ficha; devolve null se não existir. Erro de sintaxe vira exceção com a linha.</summary>
    public static Ficha? Ler(string pastaProjeto)
    {
        var caminho = Path.Combine(pastaProjeto, Arquivo);
        if (!File.Exists(caminho)) return null;
        return Leitor.Deserialize<Ficha>(File.ReadAllText(caminho)) ?? new Ficha();
    }

    public void Salvar(string pastaProjeto) =>
        File.WriteAllText(Path.Combine(pastaProjeto, Arquivo), Escritor.Serialize(this));

    /// <summary>Textos com quebra de linha saem no formato "|" (legível para editar à mão).</summary>
    private sealed class TextoEmBloco(IEventEmitter proximo) : ChainedEventEmitter(proximo)
    {
        public override void Emit(ScalarEventInfo info, IEmitter emitter)
        {
            if (info.Source.Value is string s && s.Contains('\n')) info.Style = ScalarStyle.Literal;
            base.Emit(info, emitter);
        }
    }
}

public sealed class Links
{
    public string? Teste { get; set; }
    public string? Producao { get; set; }
}
