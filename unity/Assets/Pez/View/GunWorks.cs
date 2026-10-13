using UnityEngine;

namespace Pez.View
{
    /// <summary>
    /// The gun turret's revolver (MOTION.md gun_turret). PezMotion yaws the turret and recoils the barrel; on each shot:
    ///  - the recoil spring round the barrel (the model's spring node, its local Y along the barrel) is pressed against the
    ///    sleeve by the barrel's collar: it's scaled to the gap the recoiling barrel leaves, so it compresses and pushes the
    ///    barrel home with it;
    ///  - the drum (drum node, turning about its local Z) indexes one chamber, 60 deg, 0.05 s after the shot and over
    ///    0.2 s, turning the spent chamber toward the ejection side (the eject marker) and the next candy-brick round up
    ///    to the barrel;
    ///  - halfway through the index the spent round's foil wrapper flicks out of that side and tumbles to the ground.
    /// Six identical chambers, so the drum never snaps back. A model without those nodes gets the parts it has.
    /// </summary>
    public class GunWorks
    {
        readonly Transform drum, spring, barrel, eject;
        readonly Quaternion drumRest;
        readonly Vector3 barrelRest;
        readonly float springLen, turn;
        float seenFire = float.NaN, indexT = -1f, baseAngle;
        bool ejected = true;

        const float IndexDelay = 0.05f, IndexTime = 0.2f;

        GunWorks(Transform model)
        {
            drum = PezMotion.FindDeep(model, "drum");
            spring = PezMotion.FindDeep(model, "spring");
            barrel = PezMotion.FindDeep(model, "barrel");
            eject = PezMotion.FindDeep(model, "eject");
            if (drum) drumRest = drum.localRotation;
            if (barrel) barrelRest = barrel.localPosition;
            if (spring)
            {
                // The coil runs from its node along local +Y; its wire sticks out as far below 0 as above the far end.
                var mf = spring.GetComponentInChildren<MeshFilter>();
                if (mf && mf.sharedMesh)
                {
                    var b = mf.sharedMesh.bounds;
                    springLen = b.max.y + b.min.y;
                }
                if (springLen < 0.02f) spring = null;
            }
            // Turn the fired (top) chamber toward the ejection side: a positive turn about Z carries the top toward -X.
            turn = 60f;
            if (drum && eject && eject.parent == drum.parent && eject.localPosition.x > drum.localPosition.x) turn = -60f;
        }

        public static GunWorks For(GameObject model) => new GunWorks(model.transform);

        /// <summary>`lastFire` is the view's time of this turret's last shot (EV.LastFire). The audience is for the
        /// spent wrapper.</summary>
        public void Tick(float lastFire, System.Collections.Generic.List<int> audience, float dt)
        {
            if (float.IsNaN(seenFire)) seenFire = lastFire; // a shot from before we were made isn't ours to show
            if (lastFire > seenFire)
            {
                seenFire = lastFire;
                if (indexT >= 0f) baseAngle = Mathf.Repeat(baseAngle + turn, 360f); // a shot mid-index: finish it first
                indexT = 0f;
                ejected = false;
            }
            if (drum && indexT >= 0f)
            {
                indexT += dt;
                float k = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((indexT - IndexDelay) / IndexTime));
                if (!ejected && k >= 0.5f)
                {
                    ejected = true;
                    if (eject)
                    {
                        Fx.Audience = audience;
                        var side = eject.position - drum.position; side.y = 0f;
                        Fx.SpentWrapper(eject.position, side.normalized);
                        Fx.Audience = null;
                    }
                }
                if (k >= 1f) { baseAngle = Mathf.Repeat(baseAngle + turn, 360f); indexT = -1f; k = 0f; }
                drum.localRotation = drumRest * Quaternion.Euler(0f, 0f, baseAngle + turn * k);
            }
            if (spring && barrel)
            {
                float d = Vector3.Dot(barrelRest - barrel.localPosition, barrel.localRotation * Vector3.forward);
                float s = Mathf.Clamp((springLen - Mathf.Max(d, 0f)) / springLen, 0.2f, 1f);
                if (!Mathf.Approximately(spring.localScale.y, s)) spring.localScale = new Vector3(1f, s, 1f);
            }
        }
    }
}
