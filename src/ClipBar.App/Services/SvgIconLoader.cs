using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Xml.Linq;

namespace ClipBar.App.Services;

/// <summary>
/// Minimal SVG reader for SnipDo extension icons.
///
/// Real SnipDo bundles ship their icons as .svg (WPF's Image cannot decode them),
/// so the shapes used by extension icons are converted into a DrawingImage: paths,
/// basic shapes, nested groups, matrix/translate/scale/rotate transforms, solid
/// fills, linear/radial gradients and strokes. Anything outside that subset is
/// ignored, and a file that yields no geometry returns null so the caller can fall
/// back to the built-in symbol icon.
/// </summary>
public static class SvgIconLoader
{
    /// <summary>Fills darker than this are treated as "monochrome black" and tinted.</summary>
    private const byte DarkThreshold = 0x30;

    /// <summary>Neutral gray that stays readable on both the light and dark panel.</summary>
    private static readonly Color DefaultMonochromeTint = Color.FromRgb(0x8C, 0x8C, 0x8C);

    public static ImageSource? Load(string path, double targetSize, Color? monochromeTint = null)
    {
        XDocument document;
        try
        {
            using var stream = File.OpenRead(path);

            // An icon is untrusted input: DTDs must be prohibited (no external entity fetch,
            // no billion-laughs expansion) and the document size bounded so a huge file
            // cannot exhaust memory during parse.
            var settings = new System.Xml.XmlReaderSettings
            {
                DtdProcessing = System.Xml.DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 1L << 20,
                MaxCharactersFromEntities = 0,
            };

            using var reader = System.Xml.XmlReader.Create(stream, settings);
            document = XDocument.Load(reader, LoadOptions.None);
        }
        catch (Exception ex) when (ex is IOException or System.Xml.XmlException or UnauthorizedAccessException)
        {
            return null;
        }

        var root = document.Root;
        if (root is null || !string.Equals(root.Name.LocalName, "svg", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var gradients = ReadGradients(root);
        var shapes = new List<GeometryDrawing>();
        Walk(root, Matrix.Identity, gradients, monochromeTint ?? DefaultMonochromeTint, shapes, depth: 0);

        if (shapes.Count == 0)
        {
            return null;
        }

        var (width, height) = ReadViewport(root);
        var scale = targetSize / Math.Max(1d, Math.Max(width, height));

        var group = new DrawingGroup
        {
            Transform = new MatrixTransform(scale, 0, 0, scale, 0, 0),
        };

        foreach (var drawing in shapes)
        {
            group.Children.Add(drawing);
        }

        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }

    private static void Walk(
        XElement element,
        Matrix inherited,
        Dictionary<string, GradientSpec> gradients,
        Color tint,
        List<GeometryDrawing> output,
        int depth)
    {
        if (depth > 8)
        {
            return;
        }

        var local = inherited;
        if (TryReadTransform(element, out var transform))
        {
            local = transform * inherited;
        }

        var name = element.Name.LocalName;
        switch (name.ToLowerInvariant())
        {
            case "defs":
            case "lineargradient":
            case "radialgradient":
            case "title":
            case "desc":
            case "metadata":
                return;
            case "g":
            case "svg":
                foreach (var child in element.Elements())
                {
                    Walk(child, local, gradients, tint, output, depth + 1);
                }

                return;
        }

        var geometry = TryBuildGeometry(element);
        if (geometry is null)
        {
            return;
        }

        // Geometry.Parse and the shape classes can hand back frozen instances, and
        // Geometry.Transform is not settable on a frozen object — clone first.
        var transformed = geometry.Clone();
        transformed.Transform = new MatrixTransform(local);

        var drawing = new GeometryDrawing
        {
            Geometry = transformed,
            Brush = ReadBrush(element, local, gradients, tint),
            Pen = ReadPen(element, local, gradients, tint),
        };

        if (drawing.Brush is null && drawing.Pen is null)
        {
            return;
        }

        output.Add(drawing);
    }

    private static Geometry? TryBuildGeometry(XElement element)
    {
        try
        {
            switch (element.Name.LocalName.ToLowerInvariant())
            {
                case "path":
                {
                    var data = element.Attribute("d")?.Value;
                    return string.IsNullOrWhiteSpace(data) ? null : Geometry.Parse(data);
                }

                case "rect":
                {
                    var x = Number(element, "x");
                    var y = Number(element, "y");
                    var w = Number(element, "width");
                    var h = Number(element, "height");
                    if (w <= 0 || h <= 0)
                    {
                        return null;
                    }

                    var rx = Number(element, "rx");
                    var ry = Number(element, "ry");
                    return new RectangleGeometry(new Rect(x, y, w, h), rx, ry);
                }

                case "circle":
                {
                    var r = Number(element, "r");
                    var cx = Number(element, "cx");
                    var cy = Number(element, "cy");
                    return r <= 0 ? null : new EllipseGeometry(new Point(cx, cy), r, r);
                }

                case "ellipse":
                {
                    var rx = Number(element, "rx");
                    var ry = Number(element, "ry");
                    var cx = Number(element, "cx");
                    var cy = Number(element, "cy");
                    return rx <= 0 || ry <= 0 ? null : new EllipseGeometry(new Point(cx, cy), rx, ry);
                }

                case "line":
                    return new LineGeometry(
                        new Point(Number(element, "x1"), Number(element, "y1")),
                        new Point(Number(element, "x2"), Number(element, "y2")));

                case "polyline":
                case "polygon":
                {
                    var points = ParsePoints(element.Attribute("points")?.Value);
                    if (points.Count < 2)
                    {
                        return null;
                    }

                    var figure = new PathFigure { StartPoint = points[0], IsClosed = element.Name.LocalName == "polygon" };
                    figure.Segments.Add(new PolyLineSegment(points.Skip(1), true));
                    var path = new PathGeometry();
                    path.Figures.Add(figure);
                    return path;
                }

                default:
                    return null;
            }
        }
        catch (FormatException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static Brush? ReadBrush(
        XElement element,
        Matrix transform,
        Dictionary<string, GradientSpec> gradients,
        Color tint)
    {
        var fill = StyleValue(element, "fill");
        if (fill is null || string.Equals(fill, "none", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var opacity = Math.Clamp(Number(element, "fill-opacity", 1d), 0d, 1d);

        if (fill.StartsWith("url(", StringComparison.OrdinalIgnoreCase))
        {
            var id = fill.Trim();
            var open = id.IndexOf('(');
            var close = id.LastIndexOf(')');
            if (open < 0 || close <= open)
            {
                return null;
            }

            id = id[(open + 1)..close].Trim().TrimStart('#').Trim();
            return gradients.TryGetValue(id, out var spec) ? spec.ToBrush(transform, tint) : null;
        }

        var color = ParseColor(fill);
        if (color is null)
        {
            return null;
        }

        var resolved = TintIfMonochrome(color.Value, tint);
        var brush = new SolidColorBrush(resolved) { Opacity = opacity };
        brush.Freeze();
        return brush;
    }

    private static Pen? ReadPen(
        XElement element,
        Matrix transform,
        Dictionary<string, GradientSpec> gradients,
        Color tint)
    {
        var stroke = StyleValue(element, "stroke");
        if (stroke is null || string.Equals(stroke, "none", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var brush = ReadBrushWith(element, "stroke", transform, gradients, tint);
        if (brush is null)
        {
            return null;
        }

        var width = Math.Max(0.1, Number(element, "stroke-width", 1d));
        var pen = new Pen(brush, width);
        pen.Freeze();
        return pen;
    }

    private static Brush? ReadBrushWith(
        XElement element,
        string property,
        Matrix transform,
        Dictionary<string, GradientSpec> gradients,
        Color tint)
    {
        var value = StyleValue(element, property);
        if (value is null || string.Equals(value, "none", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (value.StartsWith("url(", StringComparison.OrdinalIgnoreCase))
        {
            var id = value.Trim();
            id = id[(id.IndexOf('(') + 1)..id.LastIndexOf(')')].Trim().TrimStart('#').Trim();
            return gradients.TryGetValue(id, out var spec) ? spec.ToBrush(transform, tint) : null;
        }

        var color = ParseColor(value);
        if (color is null)
        {
            return null;
        }

        var brush = new SolidColorBrush(TintIfMonochrome(color.Value, tint));
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// Extension icons that ship only a black glyph would disappear on the dark
    /// theme, so near-black fills are replaced with a neutral gray.
    /// </summary>
    private static Color TintIfMonochrome(Color color, Color tint)
    {
        if (color.A == 0)
        {
            return color;
        }

        return color.R <= DarkThreshold && color.G <= DarkThreshold && color.B <= DarkThreshold
            ? Color.FromArgb(color.A, tint.R, tint.G, tint.B)
            : color;
    }

    private static Dictionary<string, GradientSpec> ReadGradients(XElement root)
    {
        var result = new Dictionary<string, GradientSpec>(StringComparer.Ordinal);
        foreach (var element in root.Descendants())
        {
            var localName = element.Name.LocalName.ToLowerInvariant();
            if (localName is not ("lineargradient" or "radialgradient"))
            {
                continue;
            }

            var id = element.Attribute("id")?.Value;
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var stops = new List<(double Offset, Color Color)>();
            foreach (var stop in element.Elements())
            {
                if (!string.Equals(stop.Name.LocalName, "stop", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var offset = ParseOffset(stop.Attribute("offset")?.Value);
                var color = ParseColor(StyleValue(stop, "stop-color") ?? "black");
                if (color is null)
                {
                    continue;
                }

                var opacity = Math.Clamp(Number(stop, "stop-opacity", 1d), 0d, 1d);
                stops.Add((offset, Color.FromArgb((byte)(opacity * 255), color.Value.R, color.Value.G, color.Value.B)));
            }

            if (stops.Count == 0)
            {
                continue;
            }

            stops.Sort((a, b) => a.Offset.CompareTo(b.Offset));

            result[id] = new GradientSpec(
                IsRadial: localName == "radialGradient",
                X1: Number(element, "x1"),
                Y1: Number(element, "y1"),
                X2: Number(element, "x2", 1d),
                Y2: Number(element, "y2"),
                R: Number(element, "r"),
                Cx: Number(element, "cx"),
                Cy: Number(element, "cy"),
                Stops: stops);
        }

        return result;
    }

    private static double ParseOffset(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0d;
        }

        var trimmed = text.Trim();
        if (trimmed.EndsWith('%'))
        {
            return double.TryParse(trimmed[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var pct)
                ? pct / 100d
                : 0d;
        }

        return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? Math.Clamp(value, 0d, 1d)
            : 0d;
    }

    private static (double Width, double Height) ReadViewport(XElement root)
    {
        var viewBox = root.Attribute("viewBox")?.Value;
        if (!string.IsNullOrWhiteSpace(viewBox))
        {
            var parts = viewBox.Split([' ', ',', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 4
                && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var vbw)
                && double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var vbh)
                && vbw > 0 && vbh > 0)
            {
                return (vbw, vbh);
            }
        }

        var width = Number(root, "width", 0d);
        var height = Number(root, "height", 0d);
        if (width > 0 && height > 0)
        {
            return (width, height);
        }

        return (24d, 24d);
    }

    private static bool TryReadTransform(XElement element, out Matrix matrix)
    {
        matrix = Matrix.Identity;
        var text = element.Attribute("transform")?.Value;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var result = Matrix.Identity;
        foreach (System.Text.RegularExpressions.Match match in
                 System.Text.RegularExpressions.Regex.Matches(text, "([a-zA-Z]+)\\s*\\(([^)]*)\\)"))
        {
            var numbers = match.Groups[2].Value
                .Split([' ', ',', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(n => double.TryParse(n, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0d)
                .ToArray();

            var step = Matrix.Identity;
            switch (match.Groups[1].Value.ToLowerInvariant())
            {
                case "matrix" when numbers.Length >= 6:
                    step = new Matrix(numbers[0], numbers[1], numbers[2], numbers[3], numbers[4], numbers[5]);
                    break;
                case "translate" when numbers.Length >= 1:
                    step = new Matrix(1, 0, 0, 1, numbers[0], numbers.Length > 1 ? numbers[1] : 0d);
                    break;
                case "scale" when numbers.Length >= 1:
                    step = new Matrix(numbers[0], 0, 0, numbers.Length > 1 ? numbers[1] : numbers[0], 0, 0);
                    break;
                case "rotate" when numbers.Length >= 1:
                {
                    var angle = numbers[0];
                    if (numbers.Length >= 3)
                    {
                        step = new Matrix(1, 0, 0, 1, -numbers[1], -numbers[2]);
                        step *= Rotate(angle);
                        step *= new Matrix(1, 0, 0, 1, numbers[1], numbers[2]);
                    }
                    else
                    {
                        step = Rotate(angle);
                    }

                    break;
                }
            }

            result = step * result;
        }

        matrix = result;
        return !result.IsIdentity;
    }

    private static Matrix Rotate(double degrees)
    {
        var radians = degrees * Math.PI / 180d;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        return new Matrix(cos, sin, -sin, cos, 0, 0);
    }

    private static List<Point> ParsePoints(string? text)
    {
        var result = new List<Point>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return result;
        }

        var numbers = text
            .Split([' ', ',', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(n => double.TryParse(n, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN)
            .ToList();

        for (var i = 0; i + 1 < numbers.Count; i += 2)
        {
            if (!double.IsNaN(numbers[i]) && !double.IsNaN(numbers[i + 1]))
            {
                result.Add(new Point(numbers[i], numbers[i + 1]));
            }
        }

        return result;
    }

    private static double Number(XElement element, string name, double fallback = 0d)
    {
        var raw = element.Attribute(name)?.Value;
        if (string.IsNullOrWhiteSpace(raw))
        {
            raw = StyleValue(element, name);
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        var text = raw.Trim();
        // Strip CSS units; icon files only use unitless numbers or px.
        while (text.Length > 0 && (char.IsLetter(text[^1]) || text[^1] == '%'))
        {
            text = text[..^1];
        }

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;
    }

    /// <summary>Reads a property from an attribute, falling back to the inline style.</summary>
    private static string? StyleValue(XElement element, string property)
    {
        var direct = element.Attribute(property)?.Value;
        if (!string.IsNullOrWhiteSpace(direct))
        {
            return direct.Trim();
        }

        var style = element.Attribute("style")?.Value;
        if (string.IsNullOrWhiteSpace(style))
        {
            return null;
        }

        foreach (var declaration in style.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = declaration.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            if (string.Equals(declaration[..colon].Trim(), property, StringComparison.OrdinalIgnoreCase))
            {
                return declaration[(colon + 1)..].Trim();
            }
        }

        return null;
    }

    private static Color? ParseColor(string text)
    {
        var value = text.Trim();
        if (value.StartsWith('#'))
        {
            var hex = value[1..];
            return hex.Length switch
            {
                3 => Color.FromRgb(Expand(hex[0]), Expand(hex[1]), Expand(hex[2])),
                4 => Color.FromArgb(Expand(hex[3]), Expand(hex[0]), Expand(hex[1]), Expand(hex[2])),
                6 => Color.FromRgb(Byte(hex, 0), Byte(hex, 2), Byte(hex, 4)),
                8 => Color.FromArgb(Byte(hex, 6), Byte(hex, 0), Byte(hex, 2), Byte(hex, 4)),
                _ => null,
            };
        }

        if (value.StartsWith("rgb(", StringComparison.OrdinalIgnoreCase))
        {
            var numbers = value[(value.IndexOf('(') + 1)..value.LastIndexOf(')')]
                .Split([',', ' '], StringSplitOptions.RemoveEmptyEntries)
                .Select(n => double.TryParse(n, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0d)
                .ToArray();
            if (numbers.Length >= 3)
            {
                return Color.FromRgb(Clamp(numbers[0]), Clamp(numbers[1]), Clamp(numbers[2]));
            }

            return null;
        }

        return value.ToLowerInvariant() switch
        {
            "black" => Colors.Black,
            "white" => Colors.White,
            "red" => Colors.Red,
            "green" => Colors.Green,
            "blue" => Colors.Blue,
            "gray" or "grey" => Colors.Gray,
            "transparent" => Colors.Transparent,
            _ => null,
        };

        static byte Expand(char c) => Convert.ToByte(new string(c, 2), 16);
        static byte Byte(string hex, int offset) => Convert.ToByte(hex.Substring(offset, 2), 16);
        static byte Clamp(double v) => (byte)Math.Clamp(v, 0d, 255d);
    }

    private sealed record GradientSpec(
        bool IsRadial,
        double X1,
        double Y1,
        double X2,
        double Y2,
        double R,
        double Cx,
        double Cy,
        List<(double Offset, Color Color)> Stops)
    {
        public Brush ToBrush(Matrix transform, Color tint)
        {
            var (a, b) = Apply(transform, new Point(X1, Y1), new Point(X2, Y2));
            var (center, edge) = Apply(transform, new Point(Cx, Cy), new Point(Cx + R, Cy));

            GradientStopCollection stops = new();
            foreach (var (offset, color) in Stops)
            {
                stops.Add(new GradientStop(TintIfMonochrome(color, tint), offset));
            }

            stops.Freeze();

            Brush brush = IsRadial
                ? new RadialGradientBrush(stops)
                {
                    Center = center,
                    GradientOrigin = center,
                    RadiusX = Math.Max(0.01, Math.Abs(edge.X - center.X)),
                    RadiusY = Math.Max(0.01, Math.Abs(edge.Y - center.Y)),
                    MappingMode = BrushMappingMode.Absolute,
                }
                : new LinearGradientBrush(stops, a, b)
                {
                    MappingMode = BrushMappingMode.Absolute,
                };

            brush.Freeze();
            return brush;
        }

        private static (Point A, Point B) Apply(Matrix transform, Point a, Point b)
        {
            var ta = transform.Transform(a);
            var tb = transform.Transform(b);
            return (ta, tb);
        }
    }
}
