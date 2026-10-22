using UnityEngine;

// Exterior ballistics in the style of Arma 3 with ACE3 Advanced Ballistics: bullets are simulated as forces
// (gravity + drag), not rays. Drag uses the bullet's G7 ballistic coefficient against the standard G7 drag
// curve, so it changes with Mach number (sharp rise through the sound barrier), and against the air the bullet
// moves through, so wind pushes it. Air density follows altitude and temperature.
// Units: SI throughout, except the ballistic coefficient (lb/in², as published) and barrel length (mm, as ACE3).
public static class Ballistics
{
    public const float Gravity = 9.80665f;
    const float LbPerSqInToKgPerSqM = 703.0696f;

    // Standard G7 drag function (Mach, drag coefficient), from the published G7 reference projectile table.
    static readonly float[] g7Mach =
    {
        0f, 0.5f, 0.7f, 0.75f, 0.8f, 0.825f, 0.85f, 0.875f, 0.9f, 0.925f, 0.95f, 0.975f, 1f, 1.025f, 1.05f,
        1.075f, 1.1f, 1.15f, 1.2f, 1.3f, 1.4f, 1.5f, 1.6f, 1.8f, 2f, 2.2f, 2.4f, 2.6f, 2.8f, 3f, 3.5f, 4f, 5f
    };
    static readonly float[] g7Cd =
    {
        0.1198f, 0.1194f, 0.1202f, 0.1215f, 0.1242f, 0.1266f, 0.1306f, 0.1368f, 0.1464f, 0.1660f, 0.2054f, 0.2993f, 0.3803f, 0.4015f, 0.4043f,
        0.4034f, 0.4014f, 0.3955f, 0.3884f, 0.3732f, 0.3580f, 0.3440f, 0.3315f, 0.3117f, 0.2980f, 0.2864f, 0.2752f, 0.2643f, 0.2533f, 0.2424f, 0.2154f, 0.1935f, 0.1618f
    };

    public static float G7DragCoefficient(float mach)
    {
        if (mach <= 0f) return g7Cd[0];
        int last = g7Mach.Length - 1;
        if (mach >= g7Mach[last]) return g7Cd[last];
        int i = 1;
        while (g7Mach[i] < mach) i++;
        float t = (mach - g7Mach[i - 1]) / (g7Mach[i] - g7Mach[i - 1]);
        return Mathf.Lerp(g7Cd[i - 1], g7Cd[i], t);
    }

    // International Standard Atmosphere: sea level 101325 Pa, temperature falling 6.5 °C per km.
    public static float AirTemperature(float seaLevelCelsius, float altitude) => seaLevelCelsius - 0.0065f * altitude;

    public static float AirDensity(float seaLevelCelsius, float altitude)
    {
        float pressure = 101325f * Mathf.Pow(Mathf.Max(1f - 2.25577e-5f * altitude, 0.01f), 5.25588f);
        return pressure / (287.05f * (AirTemperature(seaLevelCelsius, altitude) + 273.15f));
    }

    public static float SpeedOfSound(float celsius) => 331.3f * Mathf.Sqrt(Mathf.Max(1f + celsius / 273.15f, 0.1f));

    // Drag deceleration (m/s²) for a bullet moving at airSpeed through air of this density.
    // a = ρ v² Cd(M) π / (8 BC), with BC converted to kg/m² (a G7 BC is sectional density over form factor).
    public static float DragDeceleration(float g7BC, float airSpeed, float density, float speedOfSound)
    {
        float cd = G7DragCoefficient(airSpeed / speedOfSound);
        return density * airSpeed * airSpeed * cd * Mathf.PI / (8f * g7BC * LbPerSqInToKgPerSqM);
    }

    // One integration step of a bullet's flight: gravity plus drag against the air (velocity minus wind).
    public static void Step(ref Vector3 position, ref Vector3 velocity, float g7BC, Vector3 wind, float density, float speedOfSound, float dt)
    {
        Vector3 air = velocity - wind;
        float airSpeed = air.magnitude;
        Vector3 accel = Vector3.down * Gravity;
        if (airSpeed > 0.01f)
            accel -= air / airSpeed * DragDeceleration(g7BC, airSpeed, density, speedOfSound);
        // Semi-implicit Euler: stable at the step sizes used (a few ms).
        velocity += accel * dt;
        position += velocity * dt;
    }

    // Launch angle above the line of sight (degrees) that brings the bullet back to the line of sight at zeroRange:
    // what a rifle's sights are set to. Still air at sea level, 15 °C.
    public static float ZeroAngle(BulletType ammo, float muzzleVelocity, float zeroRange)
    {
        if (zeroRange <= 0f) return 0f;
        float angle = 0f;
        for (int i = 0; i < 5; i++)
        {
            float height = HeightAt(ammo, muzzleVelocity, angle, zeroRange);
            if (float.IsNaN(height)) break;
            angle -= Mathf.Atan2(height, zeroRange) * Mathf.Rad2Deg;
        }
        return angle;
    }

    // Height of the bullet (m, relative to the bore line's start) when it has gone this far downrange.
    static float HeightAt(BulletType ammo, float muzzleVelocity, float angleDegrees, float range)
    {
        float rad = angleDegrees * Mathf.Deg2Rad;
        Vector3 pos = Vector3.zero, vel = new Vector3(0f, Mathf.Sin(rad), Mathf.Cos(rad)) * muzzleVelocity;
        float density = AirDensity(15f, 0f), sound = SpeedOfSound(15f);
        const float dt = 0.002f;
        for (float t = 0f; t < 10f; t += dt)
        {
            Vector3 prev = pos;
            Step(ref pos, ref vel, ammo.ballisticCoefficient, Vector3.zero, density, sound, dt);
            if (pos.z >= range)
                return Mathf.Lerp(prev.y, pos.y, (range - prev.z) / Mathf.Max(pos.z - prev.z, 1e-5f));
        }
        return float.NaN;
    }

    // Spin drift (m) after this long in flight, from Bryan Litz's approximation: 1.25 (Sg + 1.2) t^1.83 inches.
    // Right-hand rifling drifts the bullet right.
    public static float SpinDrift(float gyroscopicStability, float time) =>
        1.25f * (gyroscopicStability + 1.2f) * Mathf.Pow(time, 1.83f) * 0.0254f;

    // ---------- Terminal: penetration and ricochet ----------

    // How a material treats bullets. Resistance: speed lost (m/s) per cm of material by a bullet of penetration 1.
    // Ricochet: below this angle between the bullet's path and the surface, the bullet may glance off.
    public struct SurfaceResponse
    {
        public float resistance;
        public float ricochetAngle;
        public bool stops; // never passes through (bodies: the shot is taken there)
        public SurfaceResponse(float resistance, float ricochetAngle, bool stops = false)
        {
            this.resistance = resistance;
            this.ricochetAngle = ricochetAngle;
            this.stops = stops;
        }
    }

    // Tuned so a 5.56 at the muzzle goes through ~35 cm of timber, ~1.3 cm of mild steel, ~3 cm of concrete,
    // and skips off steel and rock at shallow angles (Arma's "deflecting" is ~20-25° for rifle rounds).
    public static SurfaceResponse For(SurfaceType surface)
    {
        switch (surface)
        {
            case SurfaceType.Wood: return new SurfaceResponse(25f, 8f);
            case SurfaceType.Forest: return new SurfaceResponse(30f, 4f);
            case SurfaceType.Dirt: return new SurfaceResponse(45f, 6f);
            case SurfaceType.Gravel: return new SurfaceResponse(110f, 12f);
            case SurfaceType.Water: return new SurfaceResponse(160f, 7f);
            case SurfaceType.Rock: return new SurfaceResponse(300f, 20f);
            case SurfaceType.Metal: return new SurfaceResponse(650f, 25f);
            case SurfaceType.Glass: return new SurfaceResponse(15f, 0f);
            default: return new SurfaceResponse(0f, 0f, true); // Flesh
        }
    }
}

// One kind of cartridge. Defaults are 5.56x45 M855-ish, with ACE3's numbers for it.
[System.Serializable]
public class BulletType
{
    public string name = "5.56x45mm";
    [Tooltip("G7 ballistic coefficient (lb/in²). ACE3: 5.56 0.151, 6.5 Caseless 0.263, 7.62x51 0.2.")]
    public float ballisticCoefficient = 0.151f;
    [Tooltip("Bullet mass (g).")]
    public float mass = 4.0f;
    [Tooltip("Penetration (Arma's 'caliber'): material resistance is divided by this. 5.56 ~0.9, 6.5 ~1, 7.62 ~1.6.")]
    public float penetration = 0.9f;
    [Tooltip("Gyroscopic stability factor, for spin drift. ~1.5 for most rifle bullets.")]
    public float stability = 1.5f;
    [Tooltip("Muzzle velocity (m/s) at each barrel length (mm), from ACE3. Interpolated for the rifle's barrel.")]
    public float[] barrelLengths = { 210.82f, 269.24f, 330.2f, 391.16f, 449.58f, 508f };
    public float[] muzzleVelocities = { 723f, 796f, 843f, 878f, 906f, 922f };
    [Tooltip("Muzzle velocity change per °C of powder temperature away from 15 °C (warm powder burns faster).")]
    public float velocityPerDegree = 0.9f;

    public float MuzzleVelocity(float barrelLength, float celsius)
    {
        float v = muzzleVelocities.Length > 0 ? muzzleVelocities[0] : 900f;
        int n = Mathf.Min(barrelLengths.Length, muzzleVelocities.Length);
        for (int i = 1; i < n; i++)
        {
            v = muzzleVelocities[i];
            if (barrelLength <= barrelLengths[i])
            {
                float t = Mathf.InverseLerp(barrelLengths[i - 1], barrelLengths[i], barrelLength);
                v = Mathf.Lerp(muzzleVelocities[i - 1], muzzleVelocities[i], t);
                break;
            }
        }
        return v + (celsius - 15f) * velocityPerDegree;
    }
}
