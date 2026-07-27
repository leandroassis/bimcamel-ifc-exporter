using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace BIMCamel.Collect
{
    /// <summary>An axis-aligned world-space box (the resolved section box).</summary>
    internal sealed class SectionBox
    {
        public double MinX, MinY, MinZ, MaxX, MaxY, MaxZ;

        /// <summary>Boundary accumulation can land inverted when a producer's plane-normal
        /// convention differs (min face vs max face) — normalise so overlap tests never see
        /// an inside-out box.</summary>
        public SectionBox Normalized()
        {
            if (MinX > MaxX) { var t = MinX; MinX = MaxX; MaxX = t; }
            if (MinY > MaxY) { var t = MinY; MinY = MaxY; MaxY = t; }
            if (MinZ > MaxZ) { var t = MinZ; MinZ = MaxZ; MaxZ = t; }
            return this;
        }
    }

    /// <summary>
    /// Parses the clip-plane-set JSON returned by the Navisworks .NET API's
    /// <c>View.GetClippingPlanes()</c> into an axis-aligned <see cref="SectionBox"/>.
    ///
    /// Deliberately free of any Navisworks or third-party dependency: JSON is read through the
    /// framework's <see cref="System.Runtime.Serialization.Json.JsonReaderWriterFactory"/>
    /// (JSON-as-XML), because referencing System.Web.Extensions for JavaScriptSerializer breaks
    /// the WPF markup compiler on net48 under the modern SDK (MC1000: could not find System.Web).
    /// Being dependency-free also lets CI run this exact file's logic as a plain .NET test.
    ///
    /// Handled shapes (all fields optional, unknown shapes degrade to null — never throw):
    ///  - {"Enabled":false, ...}                                   → null (sectioning off)
    ///  - {"Mode":"OrientedBox","OrientedBox":{"Box":[[minX,minY,minZ],[maxX,maxY,maxZ]],
    ///     "Rotation":[qx,qy,qz,qw]}}                              → the section box; rotated boxes
    ///     yield the axis-aligned cover of the 8 corners rotated about the box centre (matching
    ///     the UI gizmo) — a conservative superset, the right bias for a scope filter.
    ///  - {"Mode":"Planes","Planes":[{"Plane":[nx,ny,nz,d],"Enabled":true} |
    ///     {"Normal":[nx,ny,nz],"Distance":d}, ...]}               → box rebuilt from axis-aligned
    ///     plane pairs (a plane with normal (±1,0,0) bounds x at −n·d, etc.).
    /// </summary>
    internal static class SectionBoxJson
    {
        public static SectionBox? Parse(string json)
        {
            try
            {
                XElement root;
                using (var reader = System.Runtime.Serialization.Json.JsonReaderWriterFactory.CreateJsonReader(
                           Encoding.UTF8.GetBytes(json), XmlDictionaryReaderQuotas.Max))
                    root = XElement.Load(reader);

                // Sectioning disabled entirely → no box, whatever else the JSON says.
                if (BoolOf(root.Element("Enabled")) == false) return null;

                var mode = root.Element("Mode")?.Value;
                bool wantBox = mode == null || mode.IndexOf("box", StringComparison.OrdinalIgnoreCase) >= 0;
                bool wantPlanes = mode == null || mode.Equals("Planes", StringComparison.OrdinalIgnoreCase);

                if (wantBox && FromOrientedBox(root) is SectionBox b) return b;
                if (wantPlanes && FromPlaneList(root) is SectionBox p) return p;
                return null;
            }
            catch
            {
                return null;
            }
        }

        private static SectionBox? FromOrientedBox(XElement root)
        {
            var ob = root.Element("OrientedBox") ?? root.Element("Box");
            if (ob == null) return null;

            // Corners live under an inner "Box" array; tolerate the object itself being the array.
            var cornersEl = ob.Element("Box") ?? (IsArray(ob) ? ob : null);
            var corners = cornersEl?.Elements("item").ToList();
            if (corners == null || corners.Count < 2) return null;
            if (!TryVec3(corners[0], out var min) || !TryVec3(corners[1], out var max)) return null;

            double qx = 0, qy = 0, qz = 0, qw = 1;
            var rot = (ob.Element("Rotation") ?? root.Element("Rotation"))?.Elements("item").Select(NumOf).ToArray();
            if (rot != null && rot.Length >= 4) { qx = rot[0]; qy = rot[1]; qz = rot[2]; qw = rot[3]; }

            bool identity = Math.Abs(qx) < 1e-9 && Math.Abs(qy) < 1e-9 && Math.Abs(qz) < 1e-9;
            if (identity)
                return new SectionBox { MinX = min.x, MinY = min.y, MinZ = min.z, MaxX = max.x, MaxY = max.y, MaxZ = max.z }.Normalized();

            // Rotated: axis-aligned bounds of the 8 corners rotated about the box centre.
            double cx = (min.x + max.x) / 2, cy = (min.y + max.y) / 2, cz = (min.z + max.z) / 2;
            var result = new SectionBox
            {
                MinX = double.MaxValue, MinY = double.MaxValue, MinZ = double.MaxValue,
                MaxX = double.MinValue, MaxY = double.MinValue, MaxZ = double.MinValue
            };
            for (int i = 0; i < 8; i++)
            {
                double lx = ((i & 1) == 0 ? min.x : max.x) - cx;
                double ly = ((i & 2) == 0 ? min.y : max.y) - cy;
                double lz = ((i & 4) == 0 ? min.z : max.z) - cz;
                // v' = v + 2*qw*(q×v) + 2*(q×(q×v))
                double tx = 2 * (qy * lz - qz * ly), ty = 2 * (qz * lx - qx * lz), tz = 2 * (qx * ly - qy * lx);
                double wx = cx + lx + qw * tx + (qy * tz - qz * ty);
                double wy = cy + ly + qw * ty + (qz * tx - qx * tz);
                double wz = cz + lz + qw * tz + (qx * ty - qy * tx);
                if (wx < result.MinX) result.MinX = wx; if (wx > result.MaxX) result.MaxX = wx;
                if (wy < result.MinY) result.MinY = wy; if (wy > result.MaxY) result.MaxY = wy;
                if (wz < result.MinZ) result.MinZ = wz; if (wz > result.MaxZ) result.MaxZ = wz;
            }
            return result.Normalized();
        }

        private static SectionBox? FromPlaneList(XElement root)
        {
            var planes = root.Element("Planes");
            if (planes == null) return null;

            var acc = new AxisBoundsAccumulator();
            foreach (var plane in planes.Elements("item"))
            {
                if (BoolOf(plane.Element("Enabled")) == false) continue;

                double nx, ny, nz, d;
                var coef = plane.Element("Plane")?.Elements("item").Select(NumOf).ToArray();
                if (coef != null && coef.Length >= 4)
                {
                    nx = coef[0]; ny = coef[1]; nz = coef[2]; d = coef[3];
                }
                else if (plane.Element("Normal") is XElement nEl && TryVec3(nEl, out var n)
                         && plane.Element("Distance") is XElement dEl)
                {
                    nx = n.x; ny = n.y; nz = n.z; d = NumOf(dEl);
                }
                else continue;

                acc.AddPlane(nx, ny, nz, d);
            }
            return acc.ToBox();
        }

        private static bool IsArray(XElement el) =>
            string.Equals(el.Attribute("type")?.Value, "array", StringComparison.OrdinalIgnoreCase);

        private static bool TryVec3(XElement el, out (double x, double y, double z) v)
        {
            var items = el.Elements("item").ToList();
            if (items.Count >= 3) { v = (NumOf(items[0]), NumOf(items[1]), NumOf(items[2])); return true; }
            v = default;
            return false;
        }

        private static double NumOf(XElement el) => double.Parse(el.Value, NumberStyles.Float, CultureInfo.InvariantCulture);

        private static bool? BoolOf(XElement? el) =>
            el == null ? (bool?)null : string.Equals(el.Value, "true", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Rebuilds an axis-aligned box from individual axis-aligned clip planes. Shared by the
        /// JSON path above and the legacy COM fallback in <c>ItemCollector</c>.
        /// </summary>
        internal sealed class AxisBoundsAccumulator
        {
            private const double Tol = 0.001;
            private double _xMin = double.MinValue, _yMin = double.MinValue, _zMin = double.MinValue;
            private double _xMax = double.MaxValue, _yMax = double.MaxValue, _zMax = double.MaxValue;
            private int _found;

            public void AddPlane(double nx, double ny, double nz, double dist)
            {
                if (Math.Abs(nx - 1) < Tol && Math.Abs(ny) < Tol && Math.Abs(nz) < Tol) { _xMin = Math.Max(_xMin, -dist); _found++; }
                else if (Math.Abs(nx + 1) < Tol && Math.Abs(ny) < Tol && Math.Abs(nz) < Tol) { _xMax = Math.Min(_xMax, dist); _found++; }
                else if (Math.Abs(ny - 1) < Tol && Math.Abs(nx) < Tol && Math.Abs(nz) < Tol) { _yMin = Math.Max(_yMin, -dist); _found++; }
                else if (Math.Abs(ny + 1) < Tol && Math.Abs(nx) < Tol && Math.Abs(nz) < Tol) { _yMax = Math.Min(_yMax, dist); _found++; }
                else if (Math.Abs(nz - 1) < Tol && Math.Abs(nx) < Tol && Math.Abs(ny) < Tol) { _zMin = Math.Max(_zMin, -dist); _found++; }
                else if (Math.Abs(nz + 1) < Tol && Math.Abs(nx) < Tol && Math.Abs(ny) < Tol) { _zMax = Math.Min(_zMax, dist); _found++; }
            }

            public SectionBox? ToBox()
            {
                if (_found < 2) return null;
                return new SectionBox
                {
                    MinX = _xMin == double.MinValue ? -1e10 : _xMin,
                    MinY = _yMin == double.MinValue ? -1e10 : _yMin,
                    MinZ = _zMin == double.MinValue ? -1e10 : _zMin,
                    MaxX = _xMax == double.MaxValue ? 1e10 : _xMax,
                    MaxY = _yMax == double.MaxValue ? 1e10 : _yMax,
                    MaxZ = _zMax == double.MaxValue ? 1e10 : _zMax,
                }.Normalized();
            }
        }
    }
}
