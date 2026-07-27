using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.ComApi;
using Autodesk.Navisworks.Api.Interop.ComApi;

namespace BIMCamel.Collect
{
    /// <summary>
    /// Walks the model tree to collect leaf-level items that carry geometry, and resolves the
    /// active section box. Leaf-walk + section-box logic are ported from the proven
    /// NavisworksExporter.ElementCollector.
    ///
    /// IMPORTANT: call from the Navisworks main (UI) thread only — the read API is
    /// single-threaded (the STA constraint that caps the geometry hot path, plan §5).
    /// </summary>
    public static class ItemCollector
    {
        public static List<ModelItem> GetAllLeafItemsWithGeometry(Document doc, Action<int>? onProgress = null)
        {
            var result = new List<ModelItem>();
            int visited = 0;
            if (doc != null)
                CollectLeaves(doc.Models.RootItems, result, includeHidden: true, onProgress, ref visited);
            return result;
        }

        public static List<ModelItem> GetVisibleLeafItemsWithGeometry(Document doc, Action<int>? onProgress = null)
        {
            var result = new List<ModelItem>();
            int visited = 0;
            if (doc != null)
                CollectLeaves(doc.Models.RootItems, result, includeHidden: false, onProgress, ref visited);
            return result;
        }

        /// <summary>Resolve any selection of items down to their geometry leaves.</summary>
        public static List<ModelItem> ResolveLeaves(IEnumerable<ModelItem> items, Action<int>? onProgress = null)
        {
            var result = new List<ModelItem>();
            int visited = 0;
            CollectLeaves(items, result, includeHidden: true, onProgress, ref visited);
            return result;
        }

        /// <summary>
        /// Visible geometry leaves whose bounding box overlaps the active section box.
        /// Throws <see cref="InvalidOperationException"/> if no section box is active.
        /// </summary>
        public static List<ModelItem> GetItemsInSectionBox(Document doc, Action<int>? onProgress = null)
        {
            if (doc == null) return new List<ModelItem>();

            var box = TryGetSectionBoxBounds(doc);
            if (box == null)
                throw new InvalidOperationException(
                    "No active section box found. Enable sectioning in Navisworks " +
                    "(Viewpoint → Enable Sectioning, Box mode — axis-aligned section planes " +
                    "work too), or pick a different scope.");

            var leaves = new List<ModelItem>();
            int visited = 0;
            CollectLeaves(doc.Models.RootItems, leaves, includeHidden: false, onProgress, ref visited);
            return leaves.Where(i => OverlapsBox(i, box)).ToList();
        }

        /// <summary>
        /// All saved selection AND search sets in the document, recursing folders. A search set is a
        /// <see cref="SelectionSet"/> with <c>Search != null</c>; both kinds live in
        /// <c>doc.SelectionSets</c> but are often organised inside folders, which the previous
        /// top-level-only scan skipped — so search sets never appeared in the mapping dropdown.
        /// </summary>
        public static List<SelectionSet> GetSelectionSets(Document doc)
        {
            var result = new List<SelectionSet>();
            try { CollectSets(doc.SelectionSets.ToSavedItemCollection(), result); }
            catch { /* no sets */ }
            return result;
        }

        private static void CollectSets(SavedItemCollection items, List<SelectionSet> result)
        {
            foreach (SavedItem si in items)
            {
                if (si is SelectionSet ss) result.Add(ss);
                else if (si is FolderItem fi && fi.Children != null) CollectSets(fi.Children, result);
            }
        }

        /// <summary>Geometry leaves resolved from a saved selection or search set.</summary>
        public static List<ModelItem> GetItemsFromSet(Document doc, SelectionSet set, Action<int>? onProgress = null)
        {
            var result = new List<ModelItem>();
            try
            {
                ModelItemCollection? mic =
                    set.HasExplicitModelItems ? set.ExplicitModelItems :
                    set.Search != null ? set.Search.FindAll(doc, false) : null;
                int visited = 0;
                if (mic != null) CollectLeaves(mic, result, includeHidden: true, onProgress, ref visited);
            }
            catch { /* unresolved set */ }
            return result;
        }

        /// <summary>
        /// Stable per-item key for matching set membership to scope items across separate
        /// traversals: InstanceGuid when present, else a tree path (display names). Must be
        /// computed identically wherever it's used.
        /// </summary>
        public static string ItemKey(ModelItem item)
        {
            try
            {
                if (item.InstanceGuid != Guid.Empty) return "G:" + item.InstanceGuid.ToString();
                var parts = new List<string>();
                foreach (var a in item.AncestorsAndSelf)
                    parts.Add(a.DisplayName ?? a.ClassName ?? "");
                return "P:" + string.Join("/", parts);
            }
            catch { return "P:" + (item.DisplayName ?? ""); }
        }

        /// <summary>
        /// Build a map of item-key → IFC class key from set→class rules. Resolves each set once;
        /// earlier rules win on overlap.
        /// </summary>
        public static Dictionary<string, string> BuildClassMap(Document doc, IEnumerable<(SelectionSet set, string classKey)> rules)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (set, classKey) in rules)
            {
                if (set == null || string.IsNullOrEmpty(classKey)) continue;
                foreach (var leaf in GetItemsFromSet(doc, set))
                {
                    var k = ItemKey(leaf);
                    if (!map.ContainsKey(k)) map[k] = classKey;
                }
            }
            return map;
        }

        /// <summary>
        /// World-space min corner of the scope, from each item's <see cref="ModelItem.BoundingBox()"/>
        /// (cheap — no triangle reads). Returned in the model's own units; callers scale to metres.
        /// Used by the exporter for the base-point offset so we never traverse all vertices just to
        /// find the origin (v3 Part A3). Returns (0,0,0) if nothing has a bounding box.
        /// </summary>
        public static (double x, double y, double z) ScopeMinCorner(IEnumerable<ModelItem> items, Action<int>? onProgress = null)
        {
            double mnX = double.MaxValue, mnY = double.MaxValue, mnZ = double.MaxValue;
            int n = 0;
            foreach (var item in items)
            {
                try
                {
                    var bb = item.BoundingBox();
                    if (bb != null)
                    {
                        if (bb.Min.X < mnX) mnX = bb.Min.X;
                        if (bb.Min.Y < mnY) mnY = bb.Min.Y;
                        if (bb.Min.Z < mnZ) mnZ = bb.Min.Z;
                    }
                }
                catch { /* skip odd nodes */ }
                if ((++n & 0x3FF) == 0) onProgress?.Invoke(n);
            }
            if (mnX == double.MaxValue) return (0, 0, 0);
            return (mnX, mnY, mnZ);
        }

        // Walks the model tree on the UI thread (the API is STA). On large models this is slow, so
        // it reports nodes visited every 1024 so the caller can pump the message loop and the UI
        // does not appear frozen (v3 follow-up: the pre-dialog freeze was this walk with no feedback).
        private static void CollectLeaves(IEnumerable<ModelItem> items, List<ModelItem> result, bool includeHidden, Action<int>? onProgress, ref int visited)
        {
            foreach (var item in items)
            {
                visited++;
                if ((visited & 0x3FF) == 0) onProgress?.Invoke(visited);

                if (!includeHidden && item.IsHidden) continue;

                if (item.Children.Any())
                    CollectLeaves(item.Children, result, includeHidden, onProgress, ref visited);
                else if (item.HasGeometry)
                    result.Add(item);
            }
        }

        // ── Section box resolution ───────────────────────────────────────────────
        private sealed class Box
        {
            public double MinX, MinY, MinZ, MaxX, MaxY, MaxZ;

            /// <summary>Boundary accumulation can land inverted when a producer's plane-normal
            /// convention differs (min face vs max face) — normalise so overlap tests never see
            /// an inside-out box.</summary>
            public Box Normalized()
            {
                if (MinX > MaxX) { var t = MinX; MinX = MaxX; MaxX = t; }
                if (MinY > MaxY) { var t = MinY; MinY = MaxY; MaxY = t; }
                if (MinZ > MaxZ) { var t = MinZ; MinZ = MaxZ; MaxZ = t; }
                return this;
            }
        }

        /// <summary>
        /// The active section box as an axis-aligned world box, or null when sectioning is off.
        /// Primary source is the .NET API's <c>View.GetClippingPlanes()</c> JSON — the documented
        /// cross-year surface, which also carries the real Box-mode section box. The legacy COM
        /// <c>CurrentSectionView</c> route is kept only as a fallback: it throws a COMException on
        /// Navisworks 2026 (issue #24) and never saw Box-mode sections at all.
        /// </summary>
        private static Box? TryGetSectionBoxBounds(Document doc)
            => TryGetSectionBoxFromDotNet(doc) ?? TryGetSectionBoxFromCom();

        private static Box? TryGetSectionBoxFromDotNet(Document doc)
        {
            try
            {
                var json = doc.ActiveView?.GetClippingPlanes();
                if (string.IsNullOrWhiteSpace(json)) return null;
                if (!(new System.Web.Script.Serialization.JavaScriptSerializer().DeserializeObject(json)
                        is Dictionary<string, object> root)) return null;

                // Sectioning disabled entirely → no box, whatever else the JSON says.
                if (root.TryGetValue("Enabled", out var enabled) && enabled is bool en && !en) return null;

                var mode = root.TryGetValue("Mode", out var m) ? m as string : null;
                bool wantBox = mode == null || mode.IndexOf("box", StringComparison.OrdinalIgnoreCase) >= 0;
                bool wantPlanes = mode == null || mode.Equals("Planes", StringComparison.OrdinalIgnoreCase);

                if (wantBox && FromOrientedBox(root) is Box b) return b;
                if (wantPlanes && FromPlaneList(root) is Box p) return p;
                return null;
            }
            catch
            {
                return null;
            }
        }

        // {"OrientedBox":{"Box":[[minX,minY,minZ],[maxX,maxY,maxZ]],"Rotation":[x,y,z,w]},...}
        private static Box? FromOrientedBox(Dictionary<string, object> root)
        {
            if (!(root.TryGetValue("OrientedBox", out var obObj) ? obObj : (root.TryGetValue("Box", out var bObj) ? bObj : null))
                    is Dictionary<string, object> ob) return null;
            if (!(ob.TryGetValue("Box", out var corners) && corners is object[] pair && pair.Length >= 2
                  && TryVec3(pair[0], out var min) && TryVec3(pair[1], out var max))) return null;

            double qx = 0, qy = 0, qz = 0, qw = 1;
            if (ob.TryGetValue("Rotation", out var rot) && rot is object[] q && q.Length >= 4)
            { qx = Num(q[0]); qy = Num(q[1]); qz = Num(q[2]); qw = Num(q[3]); }

            // Identity rotation: the stored corners ARE the world box. Rotated: take the
            // axis-aligned bounds of the 8 rotated corners (rotation about the box centre, matching
            // the UI gizmo) — a conservative cover, which is the right bias for a scope filter.
            bool identity = Math.Abs(qx) < 1e-9 && Math.Abs(qy) < 1e-9 && Math.Abs(qz) < 1e-9;
            if (identity)
                return new Box { MinX = min.x, MinY = min.y, MinZ = min.z, MaxX = max.x, MaxY = max.y, MaxZ = max.z }.Normalized();

            double cx = (min.x + max.x) / 2, cy = (min.y + max.y) / 2, cz = (min.z + max.z) / 2;
            var result = new Box
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

        // {"Planes":[{"Plane":[nx,ny,nz,d],"Enabled":true} | {"Normal":[...],"Distance":d}, ...]}
        private static Box? FromPlaneList(Dictionary<string, object> root)
        {
            if (!(root.TryGetValue("Planes", out var planesObj) && planesObj is object[] planes)) return null;

            var acc = new AxisBoundsAccumulator();
            foreach (var entry in planes)
            {
                if (!(entry is Dictionary<string, object> plane)) continue;
                if (plane.TryGetValue("Enabled", out var enObj) && enObj is bool en && !en) continue;

                double nx, ny, nz, d;
                if (plane.TryGetValue("Plane", out var coef) && coef is object[] c && c.Length >= 4)
                { nx = Num(c[0]); ny = Num(c[1]); nz = Num(c[2]); d = Num(c[3]); }
                else if (plane.TryGetValue("Normal", out var nObj) && TryVec3(nObj, out var n)
                         && plane.TryGetValue("Distance", out var dObj))
                { nx = n.x; ny = n.y; nz = n.z; d = Num(dObj); }
                else continue;

                acc.AddPlane(nx, ny, nz, d);
            }
            return acc.ToBox();
        }

        private static bool TryVec3(object? obj, out (double x, double y, double z) v)
        {
            if (obj is object[] a && a.Length >= 3) { v = (Num(a[0]), Num(a[1]), Num(a[2])); return true; }
            v = default;
            return false;
        }

        private static double Num(object? o) => Convert.ToDouble(o, System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>
        /// Rebuilds an axis-aligned box from individual axis-aligned clip planes (a plane with
        /// normal (±1,0,0) bounds x at −n·d, etc.). Shared by the JSON and COM paths.
        /// </summary>
        private sealed class AxisBoundsAccumulator
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

            public Box? ToBox()
            {
                if (_found < 2) return null;
                return new Box
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

        private static Box? TryGetSectionBoxFromCom()
        {
            try
            {
                // Throws COMException on Navisworks 2026 — only reached when the .NET JSON route
                // above yielded nothing, and the whole method is exception-safe.
                var sectionView = ComApiBridge.State.CurrentSectionView as InwOpAnonView;
                var clipPlanes = sectionView?.ClippingPlanes();
                if (clipPlanes == null) return null;

                var acc = new AxisBoundsAccumulator();
                foreach (InwOaClipPlane plane in clipPlanes)
                {
                    if (!plane.Enabled) continue;
                    var p = plane.Plane;
                    var n = p.GetNormal();
                    acc.AddPlane(n.data1, n.data2, n.data3, p.distance());
                }
                return acc.ToBox();
            }
            catch
            {
                return null;
            }
        }

        private static bool OverlapsBox(ModelItem item, Box b)
        {
            try
            {
                var bb = item.BoundingBox();
                if (bb == null) return false;
                return bb.Min.X <= b.MaxX && bb.Max.X >= b.MinX &&
                       bb.Min.Y <= b.MaxY && bb.Max.Y >= b.MinY &&
                       bb.Min.Z <= b.MaxZ && bb.Max.Z >= b.MinZ;
            }
            catch { return false; }
        }
    }
}
