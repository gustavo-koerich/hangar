using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;

namespace Hangar.Servicos;

/// <summary>
/// Liga e desliga projetos com os comandos da ficha (ligar/desligar). Nada vem da tela além do id do projeto.
/// Com link de teste local: roda escondido, grava log e espera a porta responder. Sem link: abre o comando numa janela minimizada.
/// </summary>
public sealed class Controle
{
    private static readonly TimeSpan EsperaLigar = TimeSpan.FromMinutes(3);
    // Donos de porta que não são o projeto (o Docker publica as portas dos containers). Esses só param pelo comando "desligar".
    private static readonly HashSet<string> NaoEncerrar = new(StringComparer.OrdinalIgnoreCase)
        { "System", "Idle", "svchost", "com.docker.backend", "com.docker.proxy", "wslrelay", "vpnkit", "docker", "dockerd", "wsl", "wslhost" };

    private static readonly string PastaLogs = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hangar", "logs");

    private readonly Dictionary<string, Process> iniciados = new(StringComparer.OrdinalIgnoreCase);

    public sealed record Resultado(string Mensagem, string? Link);

    public async Task<Resultado> LigarAsync(string pasta, Ficha ficha)
    {
        if (string.IsNullOrWhiteSpace(ficha.Ligar)) throw new InvalidOperationException("a ficha não diz como ligar (campo ligar)");
        var link = LinkLocal(ficha);

        if (link == null)
        {
            Process.Start(new ProcessStartInfo("cmd.exe", "/d /c " + ficha.Ligar)
                { WorkingDirectory = pasta, UseShellExecute = true, WindowStyle = ProcessWindowStyle.Minimized });
            return new("Iniciado", null);
        }

        if (await Respondendo(link)) return new("Já estava ligado", link);

        Directory.CreateDirectory(PastaLogs);
        var log = Path.Combine(PastaLogs, Path.GetFileName(pasta) + ".log");
        // Redireciona para arquivo (não para a ponte): o projeto continua ligado mesmo se o Hangar fechar.
        var psi = new ProcessStartInfo("cmd.exe", $"/d /s /c \"{ficha.Ligar} > \"{log}\" 2>&1 < nul\"")
            { WorkingDirectory = pasta, UseShellExecute = false, CreateNoWindow = true };
        var processo = Process.Start(psi) ?? throw new InvalidOperationException("não consegui iniciar o comando");
        iniciados[pasta] = processo;

        var limite = DateTime.Now + EsperaLigar;
        while (DateTime.Now < limite)
        {
            await Task.Delay(700);
            if (await Respondendo(link)) return new("Ligado", link);
            if (processo.HasExited && processo.ExitCode != 0)
                throw new InvalidOperationException($"o comando falhou (código {processo.ExitCode}). {FimDoLog(log)}");
        }
        throw new InvalidOperationException($"{link} não respondeu em {EsperaLigar.TotalMinutes:0} minutos. {FimDoLog(log)}");
    }

    public async Task<Resultado> DesligarAsync(string pasta, Ficha ficha)
    {
        var link = LinkLocal(ficha);
        if (string.IsNullOrWhiteSpace(ficha.Desligar) && link == null)
            throw new InvalidOperationException("a ficha não diz como desligar (campo desligar) e não tem link de teste local");

        if (!string.IsNullOrWhiteSpace(ficha.Desligar))
        {
            var psi = new ProcessStartInfo("cmd.exe", $"/d /s /c \"{ficha.Desligar} 2>&1 < nul\"")
                { WorkingDirectory = pasta, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, StandardOutputEncoding = Encoding.UTF8 };
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("não consegui rodar o comando desligar");
            var saida = p.StandardOutput.ReadToEndAsync();
            using var tempo = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            try { await p.WaitForExitAsync(tempo.Token); }
            catch (OperationCanceledException) { try { p.Kill(true); } catch { } throw new InvalidOperationException("o comando desligar demorou mais de 2 minutos"); }
            if (p.ExitCode != 0) throw new InvalidOperationException($"o comando desligar falhou (código {p.ExitCode}). {Ultimas(await saida)}");
        }

        if (iniciados.Remove(pasta, out var nosso) && !nosso.HasExited)
            try { nosso.Kill(true); } catch { }

        if (link != null && await PortaAberta(link))
        {
            var dono = DonoDaPorta(new Uri(link).Port);
            if (dono == null || NaoEncerrar.Contains(dono.ProcessName))
            {
                if (string.IsNullOrWhiteSpace(ficha.Desligar))
                    throw new InvalidOperationException($"a porta {new Uri(link).Port} é do {dono?.ProcessName ?? "sistema"}; preencha o campo desligar na ficha");
            }
            else
            {
                try { dono.Kill(true); } catch (Exception e) { throw new InvalidOperationException($"não consegui encerrar {dono.ProcessName}: {e.Message}"); }
            }
            for (int i = 0; i < 20 && await PortaAberta(link); i++) await Task.Delay(500);
        }
        return new("Desligado", null);
    }

    /// <summary>
    /// Pedido curto para o Claude se situar sozinho (lê os arquivos em vez de receber tudo pelo link).
    /// As memórias ficam na pasta do Claude Code de onde as conversas costumam começar (o perfil do usuário);
    /// uma conversa aberta na pasta do projeto não as carrega, então o pedido aponta as que citam o projeto.
    /// </summary>
    public static string PedidoOndeParamos(string pasta, Ficha? ficha)
    {
        var nome = ficha?.Nome ?? Path.GetFileName(pasta);
        var memorias = MemoriasDoProjeto(pasta, nome, ficha?.Memorias);
        return string.Join("\n",
            $"Projeto: {nome} ({pasta}).",
            "Antes de qualquer alteração, me diga em poucas linhas onde paramos e o que ficou pendente. Para isso leia: hangar.yaml, CLAUDE.md ou README, git status e git log recentes" +
                (memorias.Count > 0 ? " e estas memórias do Claude: " + string.Join("; ", memorias) : "") + ".",
            "Depois espere meu pedido.");
    }

    private static List<string> MemoriasDoProjeto(string pasta, string nome, List<string>? daFicha)
    {
        var raiz = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");
        if (!Directory.Exists(raiz)) return [];
        var todas = Directory.EnumerateFiles(raiz, "*.md", SearchOption.AllDirectories)
            .Where(a => a.Contains(Path.DirectorySeparatorChar + "memory" + Path.DirectorySeparatorChar) && Path.GetFileName(a) != "MEMORY.md")
            .ToList();
        // A ficha manda; a busca por texto é só para projetos sem a lista.
        if (daFicha is { Count: > 0 })
            return daFicha.SelectMany(n => todas.Where(a => Path.GetFileName(a).Equals(n, StringComparison.OrdinalIgnoreCase)).Take(1)).ToList();
        var termos = new[] { Path.GetFileName(pasta), nome }.Where(t => t.Length >= 4).Distinct().ToArray();
        var achadas = new List<(string Arquivo, DateTime Quando)>();
        foreach (var arquivo in todas)
        {
            try
            {
                if (termos.Any(t => File.ReadAllText(arquivo).Contains(t, StringComparison.OrdinalIgnoreCase)))
                    achadas.Add((arquivo, File.GetLastWriteTime(arquivo)));
            }
            catch (IOException) { }
        }
        // Poucas, para o link não ficar enorme: primeiro as que são do projeto (pelo nome do arquivo), depois as mais recentes.
        var chave = Path.GetFileName(pasta).Replace('-', '_').Replace(' ', '_');
        return achadas
            .OrderByDescending(a => Path.GetFileName(a.Arquivo).Contains(chave, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(a => a.Quando)
            .Take(3).Select(a => a.Arquivo).ToList();
    }

    private static string? LinkLocal(Ficha ficha) =>
        ficha.Links?.Teste is { } t && Uri.TryCreate(t, UriKind.Absolute, out var u) && (u.IsLoopback || u.Host == "localhost") ? t : null;

    private static Task<bool> PortaAberta(string link) => Task.Run(() => Catalogo.PortaLocalAberta(link));

    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(3) };

    /// <summary>Qualquer resposta HTTP (até 302 ou 500) conta como ligado. Só a porta aberta não basta: o Docker abre a porta antes do app subir.</summary>
    private static async Task<bool> Respondendo(string link)
    {
        if (!await PortaAberta(link)) return false;
        try { using var r = await Http.GetAsync(link, HttpCompletionOption.ResponseHeadersRead); return true; }
        catch { return false; }
    }

    /// <summary>Processo que escuta na porta (netstat -ano), ou null.</summary>
    private static Process? DonoDaPorta(int porta)
    {
        var saida = Catalogo.Rodar("netstat", ["-ano", "-p", "TCP"]) ?? "";
        foreach (var linha in saida.Split('\n'))
        {
            var c = linha.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (c.Length == 5 && c[3] == "LISTENING" && c[1].EndsWith(":" + porta) && int.TryParse(c[4], out var pid))
                try { return Process.GetProcessById(pid); } catch { }
        }
        return null;
    }

    private static string FimDoLog(string log)
    {
        try { return Ultimas(File.ReadAllText(log)); } catch { return ""; }
    }

    private static string Ultimas(string texto)
    {
        var linhas = texto.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return linhas.Length == 0 ? "" : "Últimas linhas: " + string.Join(" | ", linhas.TakeLast(4));
    }
}
