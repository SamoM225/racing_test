using System;
using System.Collections.Generic;
using UnityEngine;

namespace Racing
{
    /// <summary>
    /// One racing_unity race map, extracted from its FinalRaceMaps scene (spline knots, road, surfaces, checkpoints, grid,
    /// fog). Loaded from Resources/maps.json. "_T2" maps are the same loop driven in reverse, as in the game.
    /// </summary>
    [Serializable]
    public class MapData
    {
        public string name, env;
        public int laps;
        public float length, halfLeft, halfRight, runoffLeft, runoffRight;
        public bool fogOn;
        public float[] fog;
        public float[] kp, ki, ko;           // Bezier knots: position, in-handle, out-handle (world, flattened xyz)
        public float[] bandFrom, bandTo;     // surface bands in metres from the finish line (to < from wraps)
        public int[] bandMat;                // racing_unity TrackSurfaceMaterial id
        public float[] checkpoints;          // metres from the finish line
        public float[] finish;               // finish line position
        public float[] gridPos, gridYaw;     // start slots

        public string Title => name.Replace("_T2", " (reverse)").Replace('_', ' ');
        public Vector3 Knot(float[] a, int i) => new Vector3(a[i * 3], a[i * 3 + 1], a[i * 3 + 2]);
        public int KnotCount => kp.Length / 3;
    }

    public static class MapLibrary
    {
        [Serializable] class Wrapper { public MapData[] maps; }
        static MapData[] _maps;

        public static MapData[] All
        {
            get
            {
                if (_maps == null)
                {
                    var text = Resources.Load<TextAsset>("maps");
                    _maps = text != null ? JsonUtility.FromJson<Wrapper>(text.text).maps : new MapData[0];
                }
                return _maps;
            }
        }

        public static MapData Find(string name)
        {
            foreach (var m in All) if (m.name == name) return m;
            return All.Length > 0 ? All[0] : null;
        }

        /// <summary>Forward layouts first (the game's default track id), then the reverse ones.</summary>
        public static List<MapData> Ordered()
        {
            var list = new List<MapData>(All);
            list.Sort((a, b) =>
            {
                bool ra = a.name.EndsWith("_T2"), rb = b.name.EndsWith("_T2");
                return ra != rb ? ra.CompareTo(rb) : string.CompareOrdinal(a.name, b.name);
            });
            return list;
        }
    }
}
