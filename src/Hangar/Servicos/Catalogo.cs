using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Hangar.Servicos;

/// <summary>Varre Documentos\Projetos, lê as fichas e junta os fatos automáticos (git, Docker, datas, portas).</summary>
public sealed partial class Catalogo(string raiz)
{
    public const string PastaSemUso = "_sem uso ha mais de 30 dias";
    public const string PastaArquivados = "_arquivados";
    public static readonly TimeSpan JanelaAtivo = TimeSpan.FromDays(30);

    // Pastas de dados/apoio dentro de Projetos que não são projetos.
    private static readonly HashSet<string> NaoSaoProjetos = new(StringComparer.OrdinalIgnoreCase)
        { "_dados", "_backups", "_migracao", "_lixeira" };

    // Pastas que mudam sozinhas (dependências, builds, caches) e não contam como "mexi no projeto".
    private static readonly HashSet<string> Ignorar = new(StringComparer.OrdinalIgnoreCase)
        { "node_modules", ".venv", "venv", ".git", "bin", "obj", "__pycache__", ".next", ".pnpm-store", "logs", "cache_pysus" };

    public string Raiz { get; } = raiz;

    /// <summary>Pasta "Projetos": a que contém a pasta do próprio Hangar; senão, Documentos\Projetos.</summary>
    public static string AcharRaiz()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d?.Parent != null; d = d.Parent)
            // Exige a ficha: no build de desenvolvimento, src\Hangar também se chama "Hangar".
            if (d.Name.Equals("hangar", StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(d.FullName, Ficha.Arquivo))) return d.Parent.FullName;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Projetos");
    }

    public IEnumerable<string> PastasDeProjeto()
    {
        if (!Directory.Exists(Raiz)) yield break;
        foreach (var d in Directory.EnumerateDirectories(Raiz).Order())
        {
            var nome = Path.GetFileName(d);
            if (nome.Equals(PastaSemUso, StringComparison.OrdinalIgnoreCase) || nome.Equals(PastaArquivados, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var sub in Directory.EnumerateDirectories(d).Order()) yield return sub;
            }
            else if (!NaoSaoProjetos.Contains(nome) && !nome.StartsWith('_')) yield return d;
        }
    }

    /// <summary>Resolve o id (caminho relativo) para a pasta, só se for mesmo um projeto do catálogo.</summary>
    public string? Resolver(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var cheio = Path.GetFullPath(Path.Combine(Raiz, id));
        return PastasDeProjeto().FirstOrDefault(p => string.Equals(Path.GetFullPath(p), cheio, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<Panorama> MontarAsync()
    {
        var docker = await Task.Run(LerDocker);
        var pastas = PastasDeProjeto().ToList();
        var projetos = await Task.WhenAll(pastas.Select(p => Task.Run(() => Montar(p, docker))));
        var alertas = Alertas(projetos);
        return new Panorama(Raiz, DateTime.Now, docker != null, projetos.ToList(), alertas);
    }

    private Projeto Montar(string pasta, List<Container>? docker)
    {
        var id = Path.GetRelativePath(Raiz, pasta);
        var nomePasta = Path.GetFileName(pasta);
        Ficha? ficha = null;
        string? erroFicha = null;
        try { ficha = Ficha.Ler(pasta); }
        catch (Exception e) { erroFicha = "hangar.yaml com erro: " + e.Message; }

        var arquivado = id.StartsWith(PastaArquivados + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        var (ultimaAlteracao, arquivoMaisRecente) = UltimaAlteracao(pasta);
        var git = LerGit(pasta);
        if (git?.UltimoCommit is { } c && (ultimaAlteracao == null || c.Data > ultimaAlteracao)) ultimaAlteracao = c.Data;

        var projetoDocker = ficha?.DockerProjeto ?? nomePasta;
        var containers = docker?.Where(k =>
            (k.Pasta != null && Path.GetFullPath(k.Pasta).StartsWith(Path.GetFullPath(pasta), StringComparison.OrdinalIgnoreCase)) ||
            string.Equals(k.Projeto, projetoDocker, StringComparison.OrdinalIgnoreCase)).ToList() ?? [];
        var portaAberta = ficha?.Links?.Teste is { } t && PortaLocalAberta(t);

        string situacao =
            arquivado ? "arquivado" :
            ficha?.Servidor == true ? "no-ar" :
            ultimaAlteracao is { } u && DateTime.Now - u <= JanelaAtivo ? "desenvolvimento" : "parado";

        return new Projeto(
            Id: id,
            Pasta: pasta,
            NomePasta: nomePasta,
            Ficha: ficha,
            ErroFicha: erroFicha,
            Situacao: situacao,
            UltimaAlteracao: ultimaAlteracao,
            ArquivoMaisRecente: arquivoMaisRecente,
            Git: git,
            Containers: containers.Count,
            ContainersLigados: containers.Count(k => k.Ligado),
            Ligado: containers.Any(k => k.Ligado) || portaAberta,
            TemClaudeMd: File.Exists(Path.Combine(pasta, "CLAUDE.md")),
            TemReadme: Directory.EnumerateFiles(pasta, "*.md").Any(f => Path.GetFileName(f).StartsWith("READ", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(f).StartsWith("LEIA", StringComparison.OrdinalIgnoreCase)),
            Tecnologias: DetectarTecnologias(pasta));
    }

    private List<Alerta> Alertas(IEnumerable<Projeto> projetos)
    {
        var lista = new List<Alerta>();
        foreach (var p in projetos.Where(p => p.Situacao != "arquivado"))
        {
            var nome = p.Ficha?.Nome ?? p.NomePasta;
            if (p.ErroFicha != null) lista.Add(new("erro", p.Id, $"{nome}: {p.ErroFicha}"));
            if (p.Ficha == null && p.ErroFicha == null) lista.Add(new("info", p.Id, $"{nome}: sem ficha (hangar.yaml)"));
            if (p.Git is { } g)
            {
                if (g.Remoto == null) lista.Add(new("aviso", p.Id, $"{nome}: tem git mas não está no GitHub (só existe neste PC)"));
                if (g.Pendentes > 0) lista.Add(new("aviso", p.Id, $"{nome}: {g.Pendentes} alteração(ões) não salva(s) no git"));
                if (g.NaoEnviados > 0) lista.Add(new("aviso", p.Id, $"{nome}: {g.NaoEnviados} commit(s) não enviado(s) ao GitHub"));
            }
        }
        // Decisão do usuário (2026-10-30): o Hangar nunca exclui sozinho; a cada 30 dias pergunta o que fazer com os arquivados.
        foreach (var p in projetos.Where(p => p.Situacao == "arquivado" && p.Ficha?.ArquivadoEm is { } a && DateTime.Now - a > JanelaAtivo))
            lista.Add(new("decisao", p.Id, $"{p.Ficha?.Nome ?? p.NomePasta}: arquivado há mais de 30 dias. Excluir ou manter arquivado?"));

        var gerado = Path.Combine(Raiz, "_migracao", "docker", "GERADO-EM.txt");
        if (File.Exists(gerado) && DateTime.TryParse(File.ReadAllText(gerado).Trim(), CultureInfo.InvariantCulture, out var quando) && DateTime.Now - quando > TimeSpan.FromDays(7))
            lista.Add(new("aviso", null, $"Backup dos bancos do Docker tem {(DateTime.Now - quando).Days} dias. Rode _migracao\\gerar-backup-docker.ps1 antes de trocar de PC."));
        return lista;
    }

    private static (DateTime?, string?) UltimaAlteracao(string pasta)
    {
        DateTime? maior = null; string? qual = null;
        var pilha = new Stack<DirectoryInfo>();
        pilha.Push(new DirectoryInfo(pasta));
        int vistos = 0;
        while (pilha.Count > 0 && vistos < 40_000)
        {
            var d = pilha.Pop();
            try
            {
                foreach (var f in d.EnumerateFiles())
                {
                    vistos++;
                    // Anotar no Hangar ou recriar um atalho não é mexer no projeto.
                    if (f.Name == Ficha.Arquivo || f.Extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase)) continue;
                    if (maior == null || f.LastWriteTime > maior) { maior = f.LastWriteTime; qual = Path.GetRelativePath(pasta, f.FullName); }
                }
                foreach (var s in d.EnumerateDirectories())
                    if (!Ignorar.Contains(s.Name) && !s.Attributes.HasFlag(FileAttributes.ReparsePoint)) pilha.Push(s);
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }
        return (maior, qual);
    }

    private static InfoGit? LerGit(string pasta)
    {
        if (!Directory.Exists(Path.Combine(pasta, ".git")) && !File.Exists(Path.Combine(pasta, ".git"))) return null;
        var status = Rodar("git", ["-C", pasta, "status", "--porcelain=v1", "-b"]);
        if (status == null) return null;
        var linhas = status.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var cabeca = linhas.FirstOrDefault(l => l.StartsWith("## ")) ?? "";
        var ramo = RamoRegex().Match(cabeca) is { Success: true } m ? m.Groups[1].Value : "?";
        var adiante = AdianteRegex().Match(cabeca) is { Success: true } a ? int.Parse(a.Groups[1].Value) : 0;
        var remoto = Rodar("git", ["-C", pasta, "remote", "get-url", "origin"])?.Trim();
        // Commits que só mexem na ficha do Hangar não contam como trabalho no projeto.
        var log = Rodar("git", ["-C", pasta, "log", "-5", "--format=%cI%x1f%h%x1f%s", "--", ".", ":(exclude)" + Ficha.Arquivo]) ?? "";
        var commits = log.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split('\x1f'))
            .Where(p => p.Length == 3)
            .Select(p => new Commit(DateTime.Parse(p[0], CultureInfo.InvariantCulture), p[1], p[2]))
            .ToList();
        return new InfoGit(ramo, string.IsNullOrEmpty(remoto) ? null : remoto,
            linhas.Count(l => !l.StartsWith("## ")), adiante, commits.FirstOrDefault(), commits);
    }

    private static List<Container>? LerDocker()
    {
        var saida = Rodar("docker", ["ps", "-a", "--format",
            "{{.Names}}|{{.State}}|{{.Label \"com.docker.compose.project\"}}|{{.Label \"com.docker.compose.project.working_dir\"}}"], 6000);
        if (saida == null) return null;
        return saida.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim().Split('|'))
            .Where(p => p.Length == 4)
            .Select(p => new Container(p[0], p[1] == "running", NuloSeVazio(p[2]), NuloSeVazio(p[3])))
            .ToList();
    }

    internal static bool PortaLocalAberta(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || !(u.IsLoopback || u.Host == "localhost")) return false;
        try
        {
            using var c = new TcpClient();
            return c.ConnectAsync(u.Host == "localhost" ? "127.0.0.1" : u.Host, u.Port).Wait(400) && c.Connected;
        }
        catch { return false; }
    }

    private static List<string> DetectarTecnologias(string pasta)
    {
        var t = new List<string>();
        bool Tem(params string[] nomes) => nomes.Any(n => File.Exists(Path.Combine(pasta, n)) || Directory.Exists(Path.Combine(pasta, n)));
        if (Tem("docker-compose.yml", "compose.yml", Path.Combine("sistema", "docker-compose.yml"), Path.Combine("infra", "docker-compose.yml"))) t.Add("Docker");
        if (Tem("manage.py")) t.Add("Django");
        else if (Tem("requirements.txt", "pyproject.toml") || Directory.EnumerateFiles(pasta, "*.py").Any()) t.Add("Python");
        if (Tem("package.json")) t.Add("Node");
        if (Tem("supabase")) t.Add("Supabase");
        if (Directory.EnumerateFiles(pasta, "*.sln", SearchOption.TopDirectoryOnly).Any() || Tem("src") && Directory.EnumerateFiles(Path.Combine(pasta, "src"), "*.csproj", SearchOption.AllDirectories).Any()) t.Add("C#");
        if (Directory.EnumerateFiles(pasta, "*.ps1").Any()) t.Add("PowerShell");
        if (Directory.EnumerateFiles(pasta, "*.pbix", SearchOption.AllDirectories).Take(1).Any() || Directory.EnumerateFiles(pasta, "*.pbip", SearchOption.AllDirectories).Take(1).Any()) t.Add("Power BI");
        return t;
    }

    /// <summary>Roda um programa e devolve a saída; null se não existir, falhar ou demorar demais.</summary>
    public static string? Rodar(string exe, IEnumerable<string> argumentos, int esperaMs = 8000)
    {
        try
        {
            var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            psi.StandardOutputEncoding = System.Text.Encoding.UTF8;
            foreach (var a in argumentos) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var saida = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(esperaMs)) { try { p.Kill(true); } catch { } return null; }
            return p.ExitCode == 0 ? saida.Result : null;
        }
        catch { return null; }
    }

    private static string? NuloSeVazio(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

    [GeneratedRegex(@"^## (?:No commits yet on )?([^\s.]+)")] private static partial Regex RamoRegex();
    [GeneratedRegex(@"ahead (\d+)")] private static partial Regex AdianteRegex();
}

public sealed record Panorama(string Raiz, DateTime GeradoEm, bool DockerDisponivel, List<Projeto> Projetos, List<Alerta> Alertas);

public sealed record Projeto(
    string Id, string Pasta, string NomePasta, Ficha? Ficha, string? ErroFicha, string Situacao,
    DateTime? UltimaAlteracao, string? ArquivoMaisRecente, InfoGit? Git,
    int Containers, int ContainersLigados, bool Ligado, bool TemClaudeMd, bool TemReadme, List<string> Tecnologias);

public sealed record InfoGit(string Ramo, string? Remoto, int Pendentes, int NaoEnviados, Commit? UltimoCommit, List<Commit> Commits);
public sealed record Commit(DateTime Data, string Hash, string Mensagem);
public sealed record Container(string Nome, bool Ligado, string? Projeto, string? Pasta);
/// <summary>Nivel: erro | aviso | info | decisao</summary>
public sealed record Alerta(string Nivel, string? ProjetoId, string Texto);
