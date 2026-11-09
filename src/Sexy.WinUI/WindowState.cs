using System.Text.Json;
using Microsoft.UI.Windowing;

namespace Sexy;

public sealed class WindowStateData
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}

/// <summary>
/// Remembers the window position and size between launches in
/// %LOCALAPPDATA%\Sexy\window.json. Never throws - a broken file just
/// resets to the default size and position.
/// </summary>
internal static class WindowState
{
    private static readonly string StatePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Sexy",
        "window.json");

    public static WindowStateData? Load()
    {
        try
        {
            if (!File.Exists(StatePath))
            {
                return null;
            }

            return JsonSerializer.Deserialize<WindowStateData>(File.ReadAllText(StatePath));
        }
        catch
        {
            return null;
        }
    }

    public static void Save(AppWindow window)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            var pos = window.Position;
            var size = window.Size;
            var data = new WindowStateData
            {
                X = pos.X,
                Y = pos.Y,
                Width = size.Width,
                Height = size.Height
            };
            File.WriteAllText(StatePath, JsonSerializer.Serialize(data));
        }
        catch
        {
            // Never let a state write break closing the window.
        }
    }
}
