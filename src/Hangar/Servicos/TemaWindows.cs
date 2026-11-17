using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace Hangar.Servicos;

/// <summary>Lê o tema (claro/escuro) e a cor de destaque do Windows e aplica na janela e na interface.</summary>
public static class TemaWindows
{
    // Cor padrão do Fluent, usada se o Windows não informar a cor de destaque.
    private static readonly Color[] PaletaPadrao =
    [
        Rgb(0x99, 0xEB, 0xFF), Rgb(0x4C, 0xC2, 0xFF), Rgb(0x00, 0x91, 0xF8), Rgb(0x00, 0x78, 0xD4),
        Rgb(0x00, 0x67, 0xC0), Rgb(0x00, 0x3E, 0x92), Rgb(0x00, 0x1A, 0x68),
    ];

    public static bool Escuro =>
        Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            "AppsUseLightTheme", 1) is int claro && claro == 0;

    /// <summary>Fundo da janela igual ao fundo da interface, para não piscar branco ao abrir.</summary>
    public static Color Fundo => Escuro ? Rgb(0x20, 0x20, 0x20) : Rgb(0xF3, 0xF3, 0xF3);

    public static void AplicarBarraDeTitulo(Window janela)
    {
        var hwnd = new WindowInteropHelper(janela).Handle;
        if (hwnd == IntPtr.Zero) return;
        int escuro = Escuro ? 1 : 0;
        DwmSetWindowAttribute(hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref escuro, sizeof(int));
        int cantos = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref cantos, sizeof(int));
    }

    /// <summary>CSS com a cor de destaque do Windows nas variáveis --marca* da interface.</summary>
    public static string CssDestaque()
    {
        var p = Paleta();
        return $":root{{--marca:{Hex(p[4])};--marca-clara:{Hex(p[3])};--sobre-marca:#FFFFFF}}" +
               $"@media (prefers-color-scheme: dark){{:root{{--marca:{Hex(p[1])};--marca-clara:{Hex(p[0])};--sobre-marca:#000000}}}}";
    }

    private static Color[] Paleta()
    {
        // AccentPalette: 8 cores RGBA, da mais clara (Light3) à mais escura (Dark3).
        if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Accent",
                "AccentPalette", null) is not byte[] b || b.Length < 28)
            return PaletaPadrao;
        var cores = new Color[7];
        for (int i = 0; i < 7; i++) cores[i] = Rgb(b[i * 4], b[i * 4 + 1], b[i * 4 + 2]);
        return cores;
    }

    private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);
    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int atributo, ref int valor, int tamanho);
}
