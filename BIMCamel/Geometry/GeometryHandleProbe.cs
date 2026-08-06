using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.Navisworks.Api.Interop.ComApi;

namespace BIMCamel.Geometry
{
    /// <summary>
    /// Measures whether a fragment's underlying geometry object can be used as a RELIABLE identity
    /// for repeated geometry — without changing what the export produces.
    ///
    /// Why this exists: reading triangles is ~85% of export time (measured), and it is dominated by
    /// a per-vertex COM marshal that costs ~3 µs however the model is shaped. On real models 4–6
    /// fragments out of every 5 are duplicates of geometry already read, so the single biggest win
    /// available is to not read a duplicate at all. That needs a key we can obtain BEFORE reading
    /// the triangles, and <c>InwOaFragment3.Geometry</c> → <c>InwOaGeometry.nwHandle/nwID</c> is the
    /// only candidate the shipped interop assembly exposes.
    ///
    /// Using it blindly would be reckless: if one handle can cover two different meshes, caching by
    /// handle would silently export the WRONG geometry — a correctness bug traded for speed. So this
    /// probe rides along on a normal export, pairs each fragment's handle with the content hash we
    /// already compute, and reports whether the mapping ever conflicts. Zero conflicts across real
    /// models is the evidence needed before the optimisation can ship.
    ///
    /// Cost is one property read plus a dictionary probe per fragment (its own elapsed time is
    /// reported, so the overhead is visible rather than assumed), and any failure disables the probe
    /// for the rest of the export. It can never alter the exported file.
    /// </summary>
    internal static class GeometryHandleProbe
    {
        private static readonly Dictionary<long, DedupKey> Seen = new Dictionary<long, DedupKey>();
        private static bool _enabled;
        private static long _ticks;

        private static int _fragments;      // fragments whose handle we could read
        private static int _distinct;       // distinct handles seen
        private static int _conflicts;      // handles that covered two DIFFERENT meshes — must be 0
        private static int _unreadable;     // fragments exposing no usable handle

        public static void Reset()
        {
            Seen.Clear();
            _enabled = true;
            _ticks = 0;
            _fragments = _distinct = _conflicts = _unreadable = 0;
        }

        /// <summary>Pairs this fragment's geometry handle with the content hash of what it read.</summary>
        public static void Record(InwOaFragment3 frag, DedupKey geometryOnlyKey)
        {
            if (!_enabled) return;
            long t0 = ExportTiming.Now;
            try
            {
                long handle = HandleOf(frag);
                if (handle == 0) { _unreadable++; return; }

                _fragments++;
                if (Seen.TryGetValue(handle, out var prev))
                {
                    // Same handle, different mesh ⇒ the handle is NOT a safe dedup key.
                    if (!prev.Equals(geometryOnlyKey)) _conflicts++;
                }
                else
                {
                    Seen[handle] = geometryOnlyKey;
                    _distinct++;
                }
            }
            catch
            {
                _enabled = false;   // diagnostics must never disturb an export
            }
            finally
            {
                _ticks += ExportTiming.Now - t0;
            }
        }

        private static long HandleOf(InwOaFragment3 frag)
        {
            // Geometry is typed as Object on the interop interface.
            if (!(frag.Geometry is InwOaGeometry g)) return 0;
            long id = g.nwID, h = g.nwHandle;
            if (id == 0 && h == 0) return 0;
            return (id << 32) ^ h;
        }

        /// <summary>Report line, or empty when the probe never ran (non-instanced export).</summary>
        public static string Report()
        {
            if (_fragments == 0 && _unreadable == 0) return "";

            var ms = ExportTiming.Ms(_ticks).ToString("N0", CultureInfo.InvariantCulture);
            if (!_enabled)
                return $"Geometry handles: probe unavailable on this model (cost {ms} ms)\n";
            if (_fragments == 0)
                return $"Geometry handles: not exposed by any of {_unreadable:N0} fragment(s) — handle reuse not possible here\n";

            int redundant = _fragments - _distinct;
            double pct = _fragments > 0 ? 100.0 * redundant / _fragments : 0;
            var verdict = _conflicts == 0
                ? "1:1 with mesh content — SAFE to reuse"
                : $"{_conflicts:N0} CONFLICT(S) — NOT safe to reuse";
            var unread = _unreadable > 0 ? $", {_unreadable:N0} without a handle" : "";
            return $"Geometry handles: {_distinct:N0} distinct over {_fragments:N0} fragment(s){unread}; {verdict}\n" +
                   $"                  reusing them would skip {redundant:N0} triangle reads ({pct:0.#}%) · probe cost {ms} ms\n";
        }
    }
}
