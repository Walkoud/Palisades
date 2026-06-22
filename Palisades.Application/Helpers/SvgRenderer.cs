using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Xml.Linq;

namespace Palisades.Helpers
{
    public static class SvgRenderer
    {
        public static DrawingImage? RenderSvg(string svgXml, Brush defaultForeground)
        {
            if (string.IsNullOrWhiteSpace(svgXml)) return null;

            try
            {
                XDocument doc = XDocument.Parse(svgXml);
                XElement? svgElement = doc.Root;
                if (svgElement == null || svgElement.Name.LocalName != "svg") return null;

                var drawingGroup = new DrawingGroup();

                // Parse viewBox or width/height for bounds
                Rect viewBox = Rect.Empty;
                var viewBoxAttr = svgElement.Attribute("viewBox");
                if (viewBoxAttr != null)
                {
                    string[] parts = viewBoxAttr.Value.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 4)
                    {
                        double vx = double.Parse(parts[0], CultureInfo.InvariantCulture);
                        double vy = double.Parse(parts[1], CultureInfo.InvariantCulture);
                        double vw = double.Parse(parts[2], CultureInfo.InvariantCulture);
                        double vh = double.Parse(parts[3], CultureInfo.InvariantCulture);
                        viewBox = new Rect(vx, vy, vw, vh);
                    }
                }

                if (viewBox.IsEmpty)
                {
                    double w = GetDoubleAttribute(svgElement, "width", 24);
                    double h = GetDoubleAttribute(svgElement, "height", 24);
                    viewBox = new Rect(0, 0, w, h);
                }

                if (!viewBox.IsEmpty)
                {
                    drawingGroup.ClipGeometry = new RectangleGeometry(viewBox);
                }

                // Process elements recursively
                ProcessElements(svgElement, drawingGroup, defaultForeground);

                var drawingImage = new DrawingImage(drawingGroup);
                return drawingImage;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"SVG parsing error: {ex.Message}");
                return null;
            }
        }

        private static void ProcessElements(XElement parent, DrawingGroup group, Brush defaultForeground, Brush? inheritedFill = null, Brush? inheritedStroke = null)
        {
            Brush? currentFill = inheritedFill;
            Brush? currentStroke = inheritedStroke;

            var fillAttr = parent.Attribute("fill")?.Value;
            if (fillAttr != null)
            {
                currentFill = ParseBrush(fillAttr, defaultForeground, true);
            }

            var strokeAttr = parent.Attribute("stroke")?.Value;
            if (strokeAttr != null)
            {
                currentStroke = ParseBrush(strokeAttr, defaultForeground, false);
            }

            foreach (var el in parent.Elements())
            {
                string localName = el.Name.LocalName;

                if (localName == "g")
                {
                    // Group element
                    var subGroup = new DrawingGroup();
                    var transform = ParseTransform(el.Attribute("transform")?.Value);
                    if (transform != null)
                    {
                        subGroup.Transform = transform;
                    }
                    ProcessElements(el, subGroup, defaultForeground, currentFill, currentStroke);
                    group.Children.Add(subGroup);
                    continue;
                }

                Geometry? geom = null;
                switch (localName)
                {
                    case "path":
                        string? d = el.Attribute("d")?.Value;
                        if (!string.IsNullOrEmpty(d))
                        {
                            try { geom = Geometry.Parse(d); } catch { }
                        }
                        break;

                    case "rect":
                        double x = GetDoubleAttribute(el, "x", 0);
                        double y = GetDoubleAttribute(el, "y", 0);
                        double w = GetDoubleAttribute(el, "width", 0);
                        double h = GetDoubleAttribute(el, "height", 0);
                        double rx = GetDoubleAttribute(el, "rx", 0);
                        double ry = GetDoubleAttribute(el, "ry", 0);
                        if (w > 0 && h > 0)
                        {
                            geom = new RectangleGeometry(new Rect(x, y, w, h), rx, ry);
                        }
                        break;

                    case "circle":
                        double cx = GetDoubleAttribute(el, "cx", 0);
                        double cy = GetDoubleAttribute(el, "cy", 0);
                        double r = GetDoubleAttribute(el, "r", 0);
                        if (r > 0)
                        {
                            geom = new EllipseGeometry(new Point(cx, cy), r, r);
                        }
                        break;

                    case "ellipse":
                        double ecx = GetDoubleAttribute(el, "cx", 0);
                        double ecy = GetDoubleAttribute(el, "cy", 0);
                        double erx = GetDoubleAttribute(el, "rx", 0);
                        double ery = GetDoubleAttribute(el, "ry", 0);
                        if (erx > 0 && ery > 0)
                        {
                            geom = new EllipseGeometry(new Point(ecx, ecy), erx, ery);
                        }
                        break;

                    case "polygon":
                    case "polyline":
                        string? pts = el.Attribute("points")?.Value;
                        if (!string.IsNullOrEmpty(pts))
                        {
                            geom = ParsePoints(pts, localName == "polygon");
                        }
                        break;

                    case "line":
                        double x1 = GetDoubleAttribute(el, "x1", 0);
                        double y1 = GetDoubleAttribute(el, "y1", 0);
                        double x2 = GetDoubleAttribute(el, "x2", 0);
                        double y2 = GetDoubleAttribute(el, "y2", 0);
                        geom = new LineGeometry(new Point(x1, y1), new Point(x2, y2));
                        break;
                }

                if (geom != null)
                {
                    var transform = ParseTransform(el.Attribute("transform")?.Value);
                    if (transform != null)
                    {
                        geom.Transform = transform;
                    }

                    Brush fill = ParseBrush(el.Attribute("fill")?.Value, currentFill ?? defaultForeground, true);
                    Brush stroke = ParseBrush(el.Attribute("stroke")?.Value, currentStroke ?? Brushes.Transparent, false);
                    double strokeWidth = GetDoubleAttribute(el, "stroke-width", 1);

                    Pen? pen = null;
                    if (stroke != Brushes.Transparent && strokeWidth > 0)
                    {
                        pen = new Pen(stroke, strokeWidth);
                        string? strokeLinecap = el.Attribute("stroke-linecap")?.Value;
                        if (strokeLinecap == "round") pen.StartLineCap = pen.EndLineCap = pen.DashCap = PenLineCap.Round;
                        else if (strokeLinecap == "square") pen.StartLineCap = pen.EndLineCap = pen.DashCap = PenLineCap.Square;

                        string? strokeLinejoin = el.Attribute("stroke-linejoin")?.Value;
                        if (strokeLinejoin == "round") pen.LineJoin = PenLineJoin.Round;
                        else if (strokeLinejoin == "bevel") pen.LineJoin = PenLineJoin.Bevel;
                    }

                    if (el.Attribute("fill") == null && el.Attribute("stroke") == null && currentFill == null && currentStroke == null)
                    {
                        fill = Brushes.Transparent;
                        pen = new Pen(defaultForeground, 1.5)
                        {
                            StartLineCap = PenLineCap.Round,
                            EndLineCap = PenLineCap.Round,
                            LineJoin = PenLineJoin.Round
                        };
                    }

                    var draw = new GeometryDrawing(fill, pen, geom);
                    group.Children.Add(draw);
                }
            }
        }

        private static Transform? ParseTransform(string? transformStr)
        {
            if (string.IsNullOrWhiteSpace(transformStr)) return null;

            transformStr = transformStr.Trim();
            var transformGroup = new TransformGroup();

            int idx = 0;
            while (idx < transformStr.Length)
            {
                int open = transformStr.IndexOf('(', idx);
                if (open < 0) break;
                int close = transformStr.IndexOf(')', open);
                if (close < 0) break;

                string type = transformStr.Substring(idx, open - idx).Trim().ToLowerInvariant();
                type = type.TrimStart(',', ' ', '\t', '\r', '\n');

                string argsStr = transformStr.Substring(open + 1, close - open - 1);
                string[] args = argsStr.Split(new[] { ' ', ',', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

                if (type == "translate")
                {
                    if (args.Length >= 1 && double.TryParse(args[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double tx))
                    {
                        double ty = 0;
                        if (args.Length >= 2)
                        {
                            double.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out ty);
                        }
                        transformGroup.Children.Add(new TranslateTransform(tx, ty));
                    }
                }
                else if (type == "scale")
                {
                    if (args.Length >= 1 && double.TryParse(args[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double sx))
                    {
                        double sy = sx;
                        if (args.Length >= 2)
                        {
                            double.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out sy);
                        }
                        transformGroup.Children.Add(new ScaleTransform(sx, sy));
                    }
                }
                else if (type == "matrix")
                {
                    if (args.Length == 6 &&
                        double.TryParse(args[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double m11) &&
                        double.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double m12) &&
                        double.TryParse(args[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double m21) &&
                        double.TryParse(args[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double m22) &&
                        double.TryParse(args[4], NumberStyles.Float, CultureInfo.InvariantCulture, out double offsetX) &&
                        double.TryParse(args[5], NumberStyles.Float, CultureInfo.InvariantCulture, out double offsetY))
                    {
                        transformGroup.Children.Add(new MatrixTransform(new Matrix(m11, m12, m21, m22, offsetX, offsetY)));
                    }
                }
                else if (type == "rotate")
                {
                    if (args.Length >= 1 && double.TryParse(args[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double angle))
                    {
                        double cx = 0, cy = 0;
                        if (args.Length >= 3)
                        {
                            double.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out cx);
                            double.TryParse(args[2], NumberStyles.Float, CultureInfo.InvariantCulture, out cy);
                        }
                        transformGroup.Children.Add(new RotateTransform(angle, cx, cy));
                    }
                }
                else if (type == "skewx")
                {
                    if (args.Length >= 1 && double.TryParse(args[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double angleX))
                    {
                        transformGroup.Children.Add(new SkewTransform(angleX, 0));
                    }
                }
                else if (type == "skewy")
                {
                    if (args.Length >= 1 && double.TryParse(args[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double angleY))
                    {
                        transformGroup.Children.Add(new SkewTransform(0, angleY));
                    }
                }

                idx = close + 1;
            }

            return transformGroup.Children.Count > 0 ? transformGroup : null;
        }

        private static double GetDoubleAttribute(XElement el, string attrName, double defaultValue)
        {
            var attr = el.Attribute(attrName);
            if (attr != null && double.TryParse(attr.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double val))
            {
                return val;
            }
            return defaultValue;
        }

        private static Geometry? ParsePoints(string pointsString, bool close)
        {
            try
            {
                string[] parts = pointsString.Split(new[] { ' ', ',', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) return null;

                var points = new PointCollection();
                for (int i = 0; i < parts.Length - 1; i += 2)
                {
                    if (double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out double x) &&
                        double.TryParse(parts[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out double y))
                    {
                        points.Add(new Point(x, y));
                    }
                }

                if (points.Count < 2) return null;

                var figure = new PathFigure
                {
                    StartPoint = points[0],
                    IsClosed = close
                };

                for (int i = 1; i < points.Count; i++)
                {
                    figure.Segments.Add(new LineSegment(points[i], true));
                }

                var geom = new PathGeometry();
                geom.Figures.Add(figure);
                return geom;
            }
            catch
            {
                return null;
            }
        }

        private static Brush ParseBrush(string? value, Brush defaultBrush, bool isFill)
        {
            if (value == null)
            {
                return isFill ? defaultBrush : Brushes.Transparent;
            }

            value = value.Trim();
            if (value.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                return Brushes.Transparent;
            }

            if (value.Equals("currentColor", StringComparison.OrdinalIgnoreCase))
            {
                return defaultBrush;
            }

            try
            {
                var color = (Color)ColorConverter.ConvertFromString(value);
                return new SolidColorBrush(color);
            }
            catch
            {
                return defaultBrush;
            }
        }
    }
}
