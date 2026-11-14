using System.Xml.Linq;
using Turian.Engine.Core;

namespace Turian.Editor.Core;

/// <summary>Turns the project's icon into Android launcher resources, including an adaptive icon.</summary>
public static class AndroidIcons
{
    /// <summary>The generated resources, relative to the export project.</summary>
    public const string ResourceGlob = "Resources/**/*";

    const string IconName = "appicon";
    const string ForegroundName = "appicon_foreground";
    const string ResourceDirectory = "Resources";
    static readonly XNamespace AndroidNamespace = "http://schemas.android.com/apk/res/android";

    /// <summary>The density folders and their legacy icon and adaptive foreground sizes in pixels.</summary>
    internal static readonly (string Density, int Icon, int Foreground)[] Densities =
    [
        ("mdpi", 48, 108), ("hdpi", 72, 162), ("xhdpi", 96, 216), ("xxhdpi", 144, 324), ("xxxhdpi", 192, 432),
    ];

    /// <summary>Writes the launcher icons and points the manifest at them, or removes stale ones when the project has none.</summary>
    /// <param name="settings">The project being exported.</param>
    /// <param name="exportDirectory">The generated export project's folder.</param>
    /// <returns>True when icons were written.</returns>
    public static bool Write(IAppSettings settings, string exportDirectory)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var resources = Path.Combine(exportDirectory, ResourceDirectory);
        if (Directory.Exists(resources))
        {
            Directory.Delete(resources, recursive: true);
        }

        if (ProjectIcon.FindSource(settings) is not { } source)
        {
            return false;
        }

        var image = ProjectIcon.Decode(source)
            ?? throw new InvalidDataException($"The project icon '{source}' is not a readable image.");
        foreach (var (density, icon, foreground) in Densities)
        {
            var directory = Directory.CreateDirectory(Path.Combine(resources, $"mipmap-{density}")).FullName;
            File.WriteAllBytes(Path.Combine(directory, $"{IconName}.png"), ProjectIcon.EncodePng(image, icon));
            File.WriteAllBytes(Path.Combine(directory, $"{ForegroundName}.png"), EncodeForeground(image, foreground));
        }

        WriteAdaptiveIcon(resources);
        ReferenceFromManifest(Path.Combine(exportDirectory, "AndroidManifest.xml"));
        return true;
    }

    /// <summary>Centers the image in the middle two thirds of the canvas, the part no launcher mask can cut off.</summary>
    static byte[] EncodeForeground(RasterImage image, int size)
    {
        var inner = size * 2 / 3;
        var fitted = image.FitSquare(inner);
        var canvas = new RasterImage(size, size);
        var offset = (size - inner) / 2;
        for (var y = 0; y < inner; y++)
        {
            fitted.Pixels.AsSpan(y * inner * 4, inner * 4)
                .CopyTo(canvas.Pixels.AsSpan((((offset + y) * size) + offset) * 4));
        }

        return PngEncoder.Encode(canvas);
    }

    static void WriteAdaptiveIcon(string resources)
    {
        var directory = Directory.CreateDirectory(Path.Combine(resources, "mipmap-anydpi-v26")).FullName;
        new XDocument(new XElement("adaptive-icon",
            new XAttribute(XNamespace.Xmlns + "android", AndroidNamespace),
            new XElement("background", new XAttribute(AndroidNamespace + "drawable", "@android:color/white")),
            new XElement("foreground", new XAttribute(AndroidNamespace + "drawable", $"@mipmap/{ForegroundName}"))))
            .Save(Path.Combine(directory, $"{IconName}.xml"));
    }

    static void ReferenceFromManifest(string manifestPath)
    {
        var manifest = XDocument.Load(manifestPath);
        var application = manifest.Root!.Element("application")
            ?? throw new InvalidDataException("The Android manifest has no application element.");
        application.SetAttributeValue(AndroidNamespace + "icon", $"@mipmap/{IconName}");
        application.SetAttributeValue(AndroidNamespace + "roundIcon", $"@mipmap/{IconName}");
        manifest.Save(manifestPath);
    }
}
