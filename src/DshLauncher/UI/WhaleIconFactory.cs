using System.Drawing.Drawing2D;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using DshLauncher.Models;
using Microsoft.Win32;

namespace DshLauncher.UI;

internal sealed class WhaleIconFactory : IDisposable
{
    private readonly Dictionary<string, Icon> _icons = new(StringComparer.Ordinal);
    private readonly GraphicsPath _sourcePath = LoadPath();

    public Icon Get(DshActivityState state, bool alternateAttention = false)
    {
        var light = IsLightTheme();
        var key = state + ":" + alternateAttention + ":" + light;
        if (_icons.TryGetValue(key, out var existing)) return existing;

        var color = state switch
        {
            DshActivityState.Stopped => Color.FromArgb(255, 125, 125, 125),
            DshActivityState.Idle => light ? Color.Black : Color.White,
            DshActivityState.Busy => Color.FromArgb(255, 25, 180, 90),
            DshActivityState.Attention when alternateAttention => Color.FromArgb(145, 255, 190, 20),
            DshActivityState.Attention => Color.FromArgb(255, 255, 185, 0),
            _ => Color.FromArgb(255, 125, 125, 125)
        };

        using var bitmap = new Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Transparent);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var transformed = (GraphicsPath)_sourcePath.Clone();
            using var matrix = new Matrix();
            matrix.Translate(1, 1);
            matrix.Scale(0.60f, 0.60f);
            transformed.Transform(matrix);
            using var brush = new SolidBrush(color);
            graphics.FillPath(brush, transformed);
        }

        var handle = bitmap.GetHicon();
        try
        {
            var icon = (Icon)Icon.FromHandle(handle).Clone();
            _icons[key] = icon;
            return icon;
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static GraphicsPath LoadPath()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resource = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith("Assets.whale.svg", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException("Embedded whale.svg is missing.");
        using var reader = new StreamReader(stream);
        var svg = reader.ReadToEnd();
        var match = Regex.Match(svg, "<path[^>]+d=\"([^\"]+)\"");
        if (!match.Success) throw new InvalidOperationException("The whale SVG path could not be parsed.");
        return ParsePath(match.Groups[1].Value);
    }

    private static GraphicsPath ParsePath(string data)
    {
        var tokens = Regex.Matches(data, "[MCZ]|[-+]?(?:\\d*\\.)?\\d+(?:[eE][-+]?\\d+)?")
            .Select(match => match.Value)
            .ToArray();
        var path = new GraphicsPath(FillMode.Winding);
        var index = 0;
        var current = PointF.Empty;
        while (index < tokens.Length)
        {
            var command = tokens[index++];
            switch (command)
            {
                case "M":
                    current = new PointF(Number(tokens[index++]), Number(tokens[index++]));
                    path.StartFigure();
                    break;
                case "C":
                    var c1 = new PointF(Number(tokens[index++]), Number(tokens[index++]));
                    var c2 = new PointF(Number(tokens[index++]), Number(tokens[index++]));
                    var target = new PointF(Number(tokens[index++]), Number(tokens[index++]));
                    path.AddBezier(current, c1, c2, target);
                    current = target;
                    break;
                case "Z":
                    path.CloseFigure();
                    break;
                default:
                    throw new InvalidOperationException("Unsupported SVG command: " + command);
            }
        }

        return path;
    }

    private static float Number(string value) =>
        float.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);

    private static bool IsLightTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is not int value || value != 0;
    }

    public void SaveIco(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        Get(DshActivityState.Idle).Save(stream);
    }

    public void Dispose()
    {
        foreach (var icon in _icons.Values) icon.Dispose();
        _icons.Clear();
        _sourcePath.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint handle);
}
