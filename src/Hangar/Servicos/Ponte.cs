using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;

namespace Hangar.Servicos;

/// <summary>
/// Recebe os pedidos da interface ({id, acao, projeto, valor}) e responde ({id, ok, resultado|erro}).
/// A interface nunca passa caminhos: só o id do projeto, que é conferido contra o catálogo.
/// </summary>
public sealed class Ponte(Catalogo catalogo)
{
    private readonly Controle controle = new();

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed record Pedido(int Id, string Acao, string? Projeto, string? Valor);

    public async Task<string> ResponderAsync(string json)
    {
        Pedido? pedido = null;
        try
        {
            pedido = JsonSerializer.Deserialize<Pedido>(json, Json) ?? throw new InvalidOperationException("pedido vazio");
            object? resultado = pedido.Acao switch
            {
                "listar" => await catalogo.MontarAsync(),
                "abrirPasta" => Abrir("explorer.exe", Pasta(pedido)),
                "abrirVsCode" => AbrirVsCode(Pasta(pedido)),
                "abrirLink" => AbrirLink(pedido),
                "copiar" => Copiar(pedido.Valor),
                "salvarAnotacoes" => SalvarAnotacoes(pedido),
                "ligar" => await controle.LigarAsync(Pasta(pedido), FichaDe(pedido)),
                "desligar" => await controle.DesligarAsync(Pasta(pedido), FichaDe(pedido)),
                "abrirClaude" => await AbrirClaudeAsync(pedido),
                _ => throw new InvalidOperationException($"ação desconhecida: {pedido.Acao}"),
            };
            return JsonSerializer.Serialize(new { id = pedido.Id, ok = true, resultado }, Json);
        }
        catch (Exception e)
        {
            return JsonSerializer.Serialize(new { id = pedido?.Id ?? 0, ok = false, erro = e.Message }, Json);
        }
    }

    private string Pasta(Pedido p) => catalogo.Resolver(p.Projeto) ?? throw new InvalidOperationException("projeto não encontrado");

    private Ficha FichaDe(Pedido p) => Ficha.Ler(Pasta(p)) ?? throw new InvalidOperationException("projeto sem ficha (hangar.yaml)");

    /// <summary>
    /// Abre o VS Code na pasta e o painel do Claude com um pedido de "onde paramos" já escrito.
    /// O prompt completo (vem da tela, como em "copiar") fica na área de transferência como reserva.
    /// </summary>
    private async Task<bool> AbrirClaudeAsync(Pedido p)
    {
        var pasta = Pasta(p);
        Clipboard.SetText(p.Valor ?? "");
        AbrirVsCode(pasta);
        // O link vai para a janela do VS Code em foco; espera a da pasta abrir e a extensão carregar.
        await Task.Delay(4000);
        var pedido = Controle.PedidoOndeParamos(pasta, Ficha.Ler(pasta));
        Process.Start(new ProcessStartInfo("vscode://anthropic.claude-code/open?prompt=" + Uri.EscapeDataString(pedido)) { UseShellExecute = true });
        return true;
    }

    private static bool Abrir(string exe, string argumento)
    {
        Process.Start(new ProcessStartInfo(exe) { ArgumentList = { argumento }, UseShellExecute = false });
        return true;
    }

    private static bool AbrirVsCode(string pasta)
    {
        var code = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Microsoft VS Code", "Code.exe");
        if (File.Exists(code)) return Abrir(code, pasta);
        // Instalação para todos os usuários ou em outro lugar: usa o "code" do PATH.
        Process.Start(new ProcessStartInfo("cmd.exe") { ArgumentList = { "/c", "code", pasta }, UseShellExecute = false, CreateNoWindow = true });
        return true;
    }

    /// <summary>Só abre links que estão na ficha do projeto (nada de URL arbitrária vinda da tela).</summary>
    private bool AbrirLink(Pedido p)
    {
        var ficha = Ficha.Ler(Pasta(p));
        var permitidos = new[] { ficha?.Links?.Teste, ficha?.Links?.Producao };
        if (p.Valor == null || !permitidos.Contains(p.Valor) ||
            !Uri.TryCreate(p.Valor, UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https"))
            throw new InvalidOperationException("link não está na ficha do projeto");
        Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true });
        return true;
    }

    private static bool Copiar(string? texto)
    {
        Clipboard.SetText(texto ?? "");
        return true;
    }

    private bool SalvarAnotacoes(Pedido p)
    {
        var pasta = Pasta(p);
        var ficha = Ficha.Ler(pasta) ?? new Ficha { Nome = Path.GetFileName(pasta) };
        ficha.Anotacoes = string.IsNullOrWhiteSpace(p.Valor) ? null : p.Valor.Replace("\r\n", "\n").TrimEnd();
        ficha.Salvar(pasta);
        return true;
    }
}
