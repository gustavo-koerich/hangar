using System.IO;
using System.Text.Json;
using System.Windows;
using Hangar.Servicos;
using Microsoft.Web.WebView2.Core;

namespace Hangar;

/// <summary>Casca do Hangar: hospeda a interface local no WebView2 e repassa os pedidos dela para a Ponte.</summary>
public partial class JanelaPrincipal : Window
{
    private const string Origem = "https://hangar.local";
    private const string LinkWebView2 = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";
    private readonly Ponte _ponte;

    public JanelaPrincipal()
    {
        InitializeComponent();
        _ponte = new Ponte(new Catalogo(Catalogo.AcharRaiz()));
        SourceInitialized += (_, _) => TemaWindows.AplicarBarraDeTitulo(this);
        Loaded += async (_, _) => await IniciarAsync();
    }

    private async Task IniciarAsync()
    {
        try
        {
            var dados = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hangar", "WebView2");
            var ambiente = await CoreWebView2Environment.CreateAsync(null, dados);
            await Navegador.EnsureCoreWebView2Async(ambiente);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            MessageBox.Show($"O componente Microsoft Edge WebView2 não está instalado. Baixe em {LinkWebView2} e abra o Hangar de novo.",
                "Hangar", MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
            return;
        }

        var web = Navegador.CoreWebView2;
        Navegador.DefaultBackgroundColor = System.Drawing.Color.FromArgb(TemaWindows.Fundo.R, TemaWindows.Fundo.G, TemaWindows.Fundo.B);
        var cfg = web.Settings;
        cfg.IsStatusBarEnabled = false;
        cfg.IsGeneralAutofillEnabled = false;
        cfg.IsPasswordAutosaveEnabled = false;
#if !DEBUG
        cfg.AreDevToolsEnabled = false;
        cfg.AreDefaultContextMenusEnabled = false;
        cfg.AreBrowserAcceleratorKeysEnabled = false;
#endif
        web.SetVirtualHostNameToFolderMapping("hangar.local",
            Path.Combine(AppContext.BaseDirectory, "interface"), CoreWebView2HostResourceAccessKind.Deny);
        await web.AddScriptToExecuteOnDocumentCreatedAsync($$"""
            if (location.origin === {{Js(Origem)}}) {
              document.addEventListener("DOMContentLoaded", () => {
                const s = document.createElement("style");
                s.textContent = {{Js(TemaWindows.CssDestaque())}};
                document.head.appendChild(s);
              });
            }
            """);

        // Só a interface local carrega nesta janela; links externos vão para o navegador padrão pela Ponte.
        web.NavigationStarting += (_, e) => { if (!e.Uri.StartsWith(Origem + "/", StringComparison.Ordinal)) e.Cancel = true; };
        web.NewWindowRequested += (_, e) => e.Handled = true;
        web.WebMessageReceived += async (_, e) =>
        {
            if (!e.Source.StartsWith(Origem + "/", StringComparison.Ordinal)) return;
            var resposta = await _ponte.ResponderAsync(e.WebMessageAsJson);
            web.PostWebMessageAsJson(resposta);
        };
        web.Navigate(Origem + "/index.html");
    }

    private static string Js(string s) => JsonSerializer.Serialize(s);
}
