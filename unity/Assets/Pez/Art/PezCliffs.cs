// Scene component: builds the massif mesh and dresses it with boulders from the map's rock grid.
// Call Build(rockGrid) once at map load (after the map generator has decided which tiles are blocked).
using System.Collections.Generic;
using UnityEngine;

namespace Pez
{
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class PezCliffs : MonoBehaviour
    {
        public float tileSize = 1f;
        [Tooltip("crust T0, crust T1, crust T2, licorice face, talus")]
        public Material[] materials = new Material[5];
        [Tooltip("boulder_s1..s3, m1..m3, l1..l3 from models/terrain")]
        public GameObject[] boulderPrefabs = new GameObject[9];

        readonly List<GameObject> spawned = new List<GameObject>();

        public void Build(bool[,] rock)
        {
            foreach (var g in spawned) if (g) Destroy(g);
            spawned.Clear();
            List<PezRockDressing> dressing;
            var mesh = PezCliffBuilder.Build(rock, tileSize, out dressing);
            GetComponent<MeshFilter>().sharedMesh = mesh;
            GetComponent<MeshRenderer>().sharedMaterials = materials;
            var mc = GetComponent<MeshCollider>();
            if (mc) mc.sharedMesh = mesh;   // optional: lets mouse picks and projectiles hit cliffs
            foreach (var d in dressing)
            {
                var prefab = boulderPrefabs[d.variant];
                if (!prefab) continue;
                var go = Instantiate(prefab, transform);
                go.transform.localPosition = d.position;
                go.transform.localRotation = Quaternion.Euler(0f, d.yaw, 0f);
                go.transform.localScale = Vector3.one * d.scale;
                spawned.Add(go);
            }
        }
    }
}
