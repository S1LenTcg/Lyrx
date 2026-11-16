using System.Globalization;
using System.IO;
using System.Windows.Media;
using Musebase.Windows.Services;

namespace Musebase.Windows;

internal static class ClassicLyricsFonts
{
    public static FontFamily Family { get; } = new("Microsoft YaHei UI, Malgun Gothic, Yu Gothic UI, Segoe UI");
    public static FontFamily Body { get; } = LoadLocalBody();
    public static string DirectoryPath => Path.Combine(AppSettings.DataDirectory, "fonts");

    internal sealed record Choice(string? Id, string Label, FontFamily Font);

    private static FontFamily LoadLocalBody()
    {
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "local-fonts", "SpotifyMixUI-Regular.ttf")))
            return Family;
        return new FontFamily(new Uri(AppContext.BaseDirectory), $"./local-fonts/#Spotify Mix UI, {Family.Source}");
    }

    public static IReadOnlyList<Choice> GetChoices()
    {
        var choices = new List<Choice> { new(null, "Windows 默认字体", Family) };
        try
        {
            if (Directory.Exists(DirectoryPath))
            {
                choices.AddRange(Directory.GetFiles(DirectoryPath)
                    .SelectMany(ReadFile)
                    .OrderBy(choice => choice.Label, StringComparer.CurrentCultureIgnoreCase));
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Log.Write($"[fonts] {error.Message}");
        }
        return choices;
    }

    public static Choice? Import(string path)
    {
        if (ReadFile(path).Count == 0) return null;

        Directory.CreateDirectory(DirectoryPath);
        var name = Path.GetFileName(path);
        var destination = Path.Combine(DirectoryPath, name);
        if (!string.Equals(path, destination, StringComparison.OrdinalIgnoreCase))
        {
            for (var suffix = 2; File.Exists(destination); suffix++)
                destination = Path.Combine(DirectoryPath, $"{Path.GetFileNameWithoutExtension(name)} ({suffix}){Path.GetExtension(name)}");
            File.Copy(path, destination);
        }
        return ReadFile(destination).FirstOrDefault();
    }

    private static IReadOnlyList<Choice> ReadFile(string path)
    {
        var extension = Path.GetExtension(path);
        if (!extension.Equals(".ttf", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".otf", StringComparison.OrdinalIgnoreCase)) return [];

        try
        {
            // Use the file URI to distinguish different files that share a family name.
            var file = Path.GetFileName(path);
            var language = System.Windows.Markup.XmlLanguage.GetLanguage(CultureInfo.CurrentUICulture.IetfLanguageTag);
            return Fonts.GetFontFamilies(new Uri(path).AbsoluteUri).Select(font =>
            {
                var family = font.Source[(font.Source.LastIndexOf('#') + 1)..];
                var label = font.FamilyNames.TryGetValue(language, out var localized) ? localized : family;
                return new Choice($"{file}|{family}", $"{label} · {file}", new FontFamily($"{font.Source}, {Family.Source}"));
            }).ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or FileFormatException)
        {
            Log.Write($"[fonts] {error.Message}");
            return [];
        }
    }
}
