using System.Globalization;
using System.IO;
using System.Windows.Media;
using Musebase.Windows.Services;

namespace Musebase.Windows;

internal static class ClassicLyricsFonts
{
    public static FontFamily SystemFamily { get; } = new("Microsoft YaHei UI, Malgun Gothic, Yu Gothic UI, Segoe UI");
    public static FontFamily Body { get; } = LoadLocalBody();
    public static FontFamily Family => Body;
    public static FontFamily SpotifyTitle { get; } = LoadLocalTitle();
    public static bool HasBundledSpotifyMix { get; private set; }
    public static string DirectoryPath => Path.Combine(AppSettings.DataDirectory, "fonts");

    internal sealed record Choice(string? Id, string Label, FontFamily Font);

    private static FontFamily LoadLocalBody()
    {
        var fontsDir = Path.Combine(AppContext.BaseDirectory, "local-fonts");
        if (!File.Exists(Path.Combine(fontsDir, "SpotifyMixUI-Regular.ttf")))
        {
            HasBundledSpotifyMix = false;
            return SystemFamily;
        }
        HasBundledSpotifyMix = true;
        var baseUri = new Uri(fontsDir + Path.DirectorySeparatorChar);
        return new FontFamily(baseUri, $"./#Spotify Mix UI, {SystemFamily.Source}");
    }

    private static FontFamily LoadLocalTitle()
    {
        var fontsDir = Path.Combine(AppContext.BaseDirectory, "local-fonts");
        if (!File.Exists(Path.Combine(fontsDir, "SpotifyMixUITitle-700.ttf")))
            return Body;
        var baseUri = new Uri(fontsDir + Path.DirectorySeparatorChar);
        return new FontFamily(baseUri, $"./#Spotify Mix UI Title Var Wide, ./#Spotify Mix UI, {SystemFamily.Source}");
    }

    public static FontFamily Resolve(string? fontId) =>
        string.IsNullOrWhiteSpace(fontId)
            ? Family
            : GetChoices().FirstOrDefault(choice => string.Equals(choice.Id, fontId, StringComparison.Ordinal))?.Font ?? Family;

    public static IReadOnlyList<Choice> GetChoices()
    {
        var choices = new List<Choice>();
        if (HasBundledSpotifyMix)
        {
            choices.Add(new(null, "Spotify Mix（默认）", Body));
            choices.Add(new("windows-default", "Windows 默认字体", SystemFamily));
        }
        else
        {
            choices.Add(new(null, "Windows 默认字体", SystemFamily));
        }
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
