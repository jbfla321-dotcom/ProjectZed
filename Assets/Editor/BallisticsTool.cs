using System.Text;
using UnityEditor;
using UnityEngine;

// Tools > Ballistics > Print Range Card: drop, drift and speed every 100 m for the rifle's ammo, like ACE3's
// range card. Uses the scene's rifle if there is one, otherwise the default 5.56.
public static class BallisticsTool
{
    [MenuItem("Tools/Ballistics/Print Range Card")]
    public static void PrintRangeCard()
    {
        var gun = Object.FindAnyObjectByType<HitscanGun>(FindObjectsInactive.Include);
        var ammo = gun != null ? gun.ammo : new BulletType();
        float barrel = gun != null ? gun.barrelLength : 368.3f;
        float zero = gun != null ? gun.ZeroRange : 100f;
        Debug.Log(RangeCard(ammo, barrel, zero, new Vector3(4f, 0f, 0f)));
    }

    // Still air and a 4 m/s full crosswind (from the left), 15 °C at sea level.
    public static string RangeCard(BulletType ammo, float barrel, float zero, Vector3 crosswind)
    {
        float v0 = ammo.MuzzleVelocity(barrel, 15f);
        float angle = Ballistics.ZeroAngle(ammo, v0, zero) * Mathf.Deg2Rad;
        float density = Ballistics.AirDensity(15f, 0f), sound = Ballistics.SpeedOfSound(15f);
        var sb = new StringBuilder();
        sb.AppendLine($"{ammo.name}, {barrel:F0} mm barrel, {v0:F0} m/s, zero {zero:F0} m (launch {angle * Mathf.Rad2Deg * 60f:F1} MOA)");
        sb.AppendLine("range  drop(cm)  wind(cm)  speed  time");
        Vector3 pos = Vector3.zero, vel = new Vector3(0f, Mathf.Sin(angle), Mathf.Cos(angle)) * v0;
        Vector3 posW = pos, velW = vel;
        float t = 0f, next = 100f;
        const float dt = 0.001f;
        while (next <= 1000f && t < 6f)
        {
            Ballistics.Step(ref pos, ref vel, ammo.ballisticCoefficient, Vector3.zero, density, sound, dt);
            Ballistics.Step(ref posW, ref velW, ammo.ballisticCoefficient, crosswind, density, sound, dt);
            t += dt;
            if (pos.z < next) continue;
            sb.AppendLine($"{next,5:F0}  {pos.y * 100f,8:F0}  {posW.x * 100f,8:F0}  {vel.magnitude,5:F0}  {t:F2}s");
            next += 100f;
        }
        return sb.ToString();
    }

    public static void PrintDefaultForBatch() => Debug.Log("RANGECARD\n" + RangeCard(new BulletType(), 368.3f, 100f, new Vector3(4f, 0f, 0f)));
}
