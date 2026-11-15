using System.IO;
using System.IO.Packaging;
using System.Windows.Media;

namespace Musebase.Windows;

internal static class ClassicLyricsFonts
{
    private static readonly Uri ApplicationDirectory = new(AppContext.BaseDirectory, UriKind.Absolute);
    private static readonly Uri EmbeddedFontDirectory = CreateEmbeddedFontBase();
    private const string Fallback = "Microsoft YaHei UI, Malgun Gothic, Yu Gothic UI, Segoe UI";

    public static FontFamily Body { get; } = Load("SpotifyMixUI-Regular.ttf", "Spotify Mix UI");
    public static FontFamily Title { get; } = Load("SpotifyMixUITitle-700.ttf", "Spotify Mix UI Title Var Wide");

    private static Uri CreateEmbeddedFontBase()
    {
        _ = PackUriHelper.UriSchemePack;
        return new Uri("pack://application:,,,/Lyrx;component/assets/fonts/", UriKind.Absolute);
    }

    private static FontFamily Load(string file, string family)
    {
        if (File.Exists(Path.Combine(AppContext.BaseDirectory, "local-fonts", file)))
            return new FontFamily(ApplicationDirectory, $"./local-fonts/#{family}, {Fallback}");
        return new FontFamily(EmbeddedFontDirectory, $"./#{family}, {Fallback}");
    }
}
