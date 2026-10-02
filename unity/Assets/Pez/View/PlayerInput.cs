using System.Collections.Generic;
using System.Linq;
using Pez.Sim;
using UnityEngine;

namespace Pez.View
{
    /// <summary>
    /// Mouse/keyboard control for the human team. Every action becomes a Commands.Execute call,
    /// exactly what an LLM would send over the API.
    /// Left click/drag: select. Right click: move / attack / harvest / rally, or repair/heal a damaged friendly with repair trucks/medics. F + right click: attack-move.
    /// X: stop. G: deploy outpost truck. U: unload transports. M + right-click: lay mines (shift: 5). Delete: sell.
    /// Right-click your own APC/chopper with infantry selected to board; right-click an enemy building with engineers to capture. Ctrl+A: select all combat units. Esc: cancel placement.
    /// </summary>
    public class PlayerInput : MonoBehaviour
    {
        public GameRunner Runner;
        public string PlacingKey;          // structure awaiting placement
        public string LastError;
        public float LastErrorTime;
        public bool Dragging;
        public Vector2 DragStart;
        bool attackMoveArmed, mineArmed;
        Transform ghost;
        Material ghostOk, ghostBad;

        World W => Runner.Game.World;
        int Team => Runner.HumanTeam;
        WorldView View => Runner.View;
        RtsCamera Cam => Runner.Camera;

        public bool AttackMoveArmed => attackMoveArmed;

        public JObj Exec(params object[] kv)
        {
            var d = new Dictionary<string, object>();
            for (int i = 0; i < kv.Length; i += 2)
            {
                var v = kv[i + 1];
                d[(string)kv[i]] = v is int n ? (double)n : v is float f ? (double)f : v is IEnumerable<int> ids ? ids.Select(x => (object)(double)x).ToList() : v;
            }
            var r = Commands.Execute(W, Team, d);
            if (!(r["ok"] is bool ok && ok)) { LastError = r["error"]?.ToString(); LastErrorTime = Time.time; }
            return r;
        }

        void Update()
        {
            // Escape deselects (which also stops the camera following a unit), for players and spectators alike.
            if (Runner != null && !Runner.InMenu && !Hud.Typing && Input.GetKeyDown(KeyCode.Escape) && PlacingKey == null && !attackMoveArmed && !mineArmed) View.Selected.Clear();
            if (Runner != null && !Runner.InMenu && Team < 0) { Dragging = false; ClearGhost(); Inspect(); return; }
            if (Runner == null || Runner.InMenu || Team < 0 || W.GameOver) { Dragging = false; ClearGhost(); return; }
            var mouse = (Vector2)Input.mousePosition;
            bool overUi = Runner.Hud != null && Runner.Hud.IsOverUi(mouse);
            Cam.GroundPoint(mouse, out var ground);
            var tile = new Int2(Mathf.FloorToInt(ground.x), Mathf.FloorToInt(ground.z));

            if (Hud.Typing) return; // keystrokes belong to the orders text box
            if (Input.GetKeyDown(KeyCode.O) && Runner.Hud != null) Runner.Hud.ShowOrders = !Runner.Hud.ShowOrders;
            if (Input.GetKeyDown(KeyCode.Escape)) { PlacingKey = null; attackMoveArmed = false; mineArmed = false; }
            if (Input.GetKeyDown(KeyCode.F)) attackMoveArmed = true;
            if (Input.GetKeyDown(KeyCode.M)) mineArmed = true;
            if (Input.GetKeyDown(KeyCode.U))
            {
                var tr = SelectedUnits().Where(id => W.Get(id)?.Def.Capacity > 0).ToList();
                if (tr.Count > 0) Exec("type", "unload", "units", tr);
            }
            if (Input.GetKeyDown(KeyCode.X)) { var u = SelectedUnits(); if (u.Count > 0) Exec("type", "stop", "units", u); }
            if (Input.GetKeyDown(KeyCode.G))
            {
                var dep = SelectedUnits().Where(id => W.Get(id)?.Def.DeploysInto != null).ToList();
                if (dep.Count > 0) Exec("type", "deploy", "units", dep);
            }
            if (Input.GetKeyDown(KeyCode.Delete) || Input.GetKeyDown(KeyCode.Backspace))
                foreach (var s in SelectedStructures()) Exec("type", "sell", "structure_id", s);
            if (Input.GetKeyDown(KeyCode.A) && (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.LeftCommand)))
            {
                View.Selected.Clear();
                foreach (var e in W.Owned(Team)) if (!e.IsStructure && e.IsArmed) View.Selected.Add(e.Id);
            }

            if (PlacingKey != null) { Placement(tile, overUi); return; }
            ClearGhost();

            // Selection
            if (Input.GetMouseButtonDown(0) && !overUi) { Dragging = true; DragStart = mouse; }
            if (Input.GetMouseButtonUp(0) && Dragging)
            {
                Dragging = false;
                bool add = Input.GetKey(KeyCode.LeftShift);
                if (!add) View.Selected.Clear();
                if ((mouse - DragStart).magnitude < 6f)
                {
                    var hit = Pick(ground, Team);
                    if (hit != null) { if (add && View.Selected.Contains(hit.Id)) View.Selected.Remove(hit.Id); else View.Selected.Add(hit.Id); }
                }
                else
                {
                    var rect = Rect.MinMaxRect(Mathf.Min(DragStart.x, mouse.x), Mathf.Min(DragStart.y, mouse.y), Mathf.Max(DragStart.x, mouse.x), Mathf.Max(DragStart.y, mouse.y));
                    foreach (var e in W.Owned(Team))
                    {
                        if (e.IsStructure) continue;
                        var sp = Cam.Cam.WorldToScreenPoint(WorldView.W(e.Pos));
                        if (sp.z > 0 && rect.Contains(sp)) View.Selected.Add(e.Id);
                    }
                }
            }

            // Orders
            if (Input.GetMouseButtonDown(1) && !overUi)
            {
                var units = SelectedUnits();
                var structures = SelectedStructures();
                var target = Pick(ground, -1);
                if (units.Count > 0)
                {
                    var unitEntities = units.Select(W.Get).Where(e => e != null).ToList();
                    var engineers = unitEntities.Where(e => e.Def.Engineer).Select(e => e.Id).ToList();
                    var layers = unitEntities.Where(e => e.Def.LaysMines).Select(e => e.Id).ToList();
                    var infantry = unitEntities.Where(e => e.Def.Armor == Armor.Infantry).Select(e => e.Id).ToList();
                    var healers = unitEntities.Where(e => e.Def.RepairRate > 0 && target != null && World.CanTend(e, target)).Select(e => e.Id).ToList();
                    if (mineArmed && layers.Count > 0)
                        Exec("type", "lay_mines", "units", layers, "x", ground.x, "y", ground.z, "count", Input.GetKey(KeyCode.LeftShift) ? 5 : 1);
                    else if (target != null && target.Team == Team && target.Def.Capacity > 0 && infantry.Count > 0)
                        Exec("type", "load", "units", infantry, "transport", target.Id);
                    else if (target != null && target.IsStructure && target.Team != Team && engineers.Count > 0)
                    {
                        // Engineers capture; anyone else selected keeps shooting at it.
                        Exec("type", "capture", "units", engineers, "target", target.Id);
                        var rest = units.Except(engineers).ToList();
                        if (rest.Count > 0) Exec("type", "attack", "units", rest, "target", target.Id);
                    }
                    else if (healers.Count > 0)
                    {
                        // Medics/repair trucks tend the target; everyone else moves up beside it.
                        Exec("type", "repair", "units", healers, "target", target.Id);
                        var rest = units.Except(healers).ToList();
                        if (rest.Count > 0) Exec("type", "move", "units", rest, "x", ground.x, "y", ground.z);
                    }
                    else if (target != null && target.Team != Team && W.IsVisibleTo(Team, target)) Exec("type", "attack", "units", units, "target", target.Id);
                    else if (W.Map.OreAt(tile.X, tile.Y) > 0 && unitEntities.Any(e => e.IsHarvester)) Exec("type", "harvest", "units", units, "x", ground.x, "y", ground.z);
                    else Exec("type", attackMoveArmed ? "attack_move" : "move", "units", units, "x", ground.x, "y", ground.z);
                    Fx.MuzzleFlash(new Vector3(ground.x, 0.05f, ground.z), 0.08f); // click marker
                }
                else if (structures.Count > 0) Exec("type", "rally", "structure_id", structures[0], "x", ground.x, "y", ground.z);
                attackMoveArmed = false;
                mineArmed = false;
            }
        }

        /// <summary>Spectators can't command, but a click picks any unit or building for the HUD's selection card.</summary>
        void Inspect()
        {
            var mouse = (Vector2)Input.mousePosition;
            if (Hud.Typing || !Input.GetMouseButtonDown(0) || (Runner.Hud != null && Runner.Hud.IsOverUi(mouse))) return;
            if (!Cam.GroundPoint(mouse, out var ground)) return;
            var p = WorldView.S(ground);
            Entity best = null; float bd = float.MaxValue;
            foreach (var e in W.Entities)
            {
                if (e.Dead || e.IsMine) continue;
                float d = e.DistFrom(p);
                if (d <= (e.IsStructure ? 0.05f : 0.45f) && d < bd) { bd = d; best = e; }
            }
            View.Selected.Clear();
            if (best != null) View.Selected.Add(best.Id);
        }

        void Placement(Int2 tile, bool overUi)
        {
            var def = Defs.Get(PlacingKey);
            var origin = new Int2(tile.X - (def.SizeX - 1) / 2, tile.Y - (def.SizeY - 1) / 2);
            bool ok = W.CanPlace(Team, PlacingKey, origin.X, origin.Y) == null;
            if (ghost == null)
            {
                if (ghostOk == null) ghostOk = Mats.Unlit(new Color(0.2f, 1f, 0.3f, 0.35f));
                if (ghostBad == null) ghostBad = Mats.Unlit(new Color(1f, 0.15f, 0.1f, 0.35f));
                ghost = Models.Part(null, PrimitiveType.Cube, Vector3.zero, Vector3.one, ghostOk);
                ghost.GetComponent<Renderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }
            ghost.position = new Vector3(origin.X + def.SizeX / 2f, 0.25f, origin.Y + def.SizeY / 2f);
            ghost.localScale = new Vector3(def.SizeX, 0.5f, def.SizeY);
            ghost.GetComponent<Renderer>().sharedMaterial = ok ? ghostOk : ghostBad;
            if (Input.GetMouseButtonDown(0) && !overUi)
            {
                var r = Exec("type", "build", "structure", PlacingKey, "x", origin.X, "y", origin.Y);
                if (r["ok"] is bool b && b && !Input.GetKey(KeyCode.LeftShift)) PlacingKey = null;
            }
            if (Input.GetMouseButtonDown(1)) PlacingKey = null;
        }

        void ClearGhost()
        {
            if (ghost != null) { Destroy(ghost.gameObject); ghost = null; }
        }

        List<int> SelectedUnits() => View.Selected.Select(W.Get).Where(e => e != null && e.Team == Team && !e.IsStructure).Select(e => e.Id).ToList();
        List<int> SelectedStructures() => View.Selected.Select(W.Get).Where(e => e != null && e.Team == Team && e.IsStructure).Select(e => e.Id).ToList();

        /// <summary>Entity under a ground point. team = -1 picks any visible entity.</summary>
        Entity Pick(Vector3 ground, int team)
        {
            var p = WorldView.S(ground);
            Entity best = null; float bd = float.MaxValue;
            foreach (var e in W.Entities)
            {
                if (e.Dead || (team >= 0 && e.Team != team)) continue;
                if (e.Team != Team && !W.IsVisibleTo(Team, e)) continue;
                float d = e.DistFrom(p);
                float slack = e.IsStructure ? 0.05f : 0.45f;
                if (d <= slack && d < bd) { bd = d; best = e; }
            }
            return best;
        }
    }
}
