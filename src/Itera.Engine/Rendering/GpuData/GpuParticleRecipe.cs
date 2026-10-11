using System.Numerics;
using Itera.Engine.Particles;

namespace Itera.Engine.Rendering.GpuData;

/// <summary>
/// A <see cref="ParticleRecipe"/> as the particle shaders read it (<c>particle-data.glsl</c>, <c>layout(scalar)</c>,
/// set 1 binding 3), <see cref="ParticleRecipes.MaxRecipes"/> per frame slot. Written by the main thread each frame
/// (<c>ParticlePass.Write</c>). Field order and types match the GLSL struct; <c>ParticlePassTests</c> checks them.
/// </summary>
public struct GpuParticleRecipe
{
    public uint Draw;
    public uint Collision;
    public uint Stretch;
    public uint Flags;

    /// <summary>RGBA8, red lowest.</summary>
    public uint ColourStart;

    /// <summary>RGBA8, red lowest.</summary>
    public uint ColourEnd;
    public float Glow;
    public float Cover;
    public float SpeedMin;
    public float SpeedMax;

    /// <summary>The cosine of the spread's angle.</summary>
    public float SpreadCosine;
    public float LifetimeMin;
    public float LifetimeMax;
    public float SizeStart;
    public float SizeEnd;
    public float SizeJitter;
    public float ColourJitter;

    /// <summary>The recipe's gravity, metres per second squared: its multiple of the match's (<c>PhysicsWorld.Gravity</c>).</summary>
    public Vector3 Gravity;

    /// <summary>The match's up, against its gravity (+y with none): a particle rests on a surface facing it.</summary>
    public Vector3 Up;
    public float Drag;
    public float Restitution;
    public float Friction;
    public float Spin;
    public float StretchSeconds;
    public float Attraction;
    public float FadeIn;
    public float FadeOutShare;

    /// <summary>The recipe as the shaders read it, under the match's <paramref name="gravity"/>.</summary>
    public static GpuParticleRecipe From(in ParticleRecipe recipe, Vector3 gravity) =>
        new()
        {
            Draw = (uint)recipe.Draw,
            Collision = (uint)recipe.Collision,
            Stretch = (uint)recipe.Stretch,
            Flags = (uint)recipe.Traits,
            ColourStart = PackColour(recipe.ColourStart),
            ColourEnd = PackColour(recipe.ColourEnd),
            Glow = recipe.Glow,
            Cover = Math.Clamp(recipe.Cover, 0, 1),
            SpeedMin = recipe.SpeedMin,
            SpeedMax = recipe.SpeedMax,
            SpreadCosine = MathF.Cos(Math.Clamp(recipe.SpreadDegrees, 0, 180) * (MathF.PI / 180)),
            LifetimeMin = recipe.LifetimeMin,
            LifetimeMax = recipe.LifetimeMax,
            SizeStart = recipe.SizeStart,
            SizeEnd = recipe.SizeEnd,
            SizeJitter = Math.Clamp(recipe.SizeJitter, 0, 1),
            ColourJitter = Math.Clamp(recipe.ColourJitter, 0, 1),
            Gravity = gravity * recipe.Gravity,
            Up = gravity.LengthSquared() > 0 ? -Vector3.Normalize(gravity) : Vector3.UnitY,
            Drag = Math.Max(0, recipe.Drag),
            Restitution = Math.Clamp(recipe.Restitution, 0, 1),
            Friction = Math.Clamp(recipe.Friction, 0, 1),
            Spin = recipe.Spin,
            StretchSeconds = Math.Max(0, recipe.StretchSeconds),
            Attraction = Math.Max(0, recipe.Attraction),
            FadeIn = Math.Clamp(recipe.FadeIn, 0, 1),
            FadeOutShare = Math.Clamp(recipe.FadeOutShare, 0.001f, 1),
        };

    /// <summary>A linear colour, each part 0 to 1, as RGBA8 with red in the lowest byte (<c>unpackUnorm4x8</c>).</summary>
    public static uint PackColour(Vector4 colour)
    {
        static uint Byte(float value) => (uint)MathF.Round(Math.Clamp(value, 0, 1) * 255);

        return Byte(colour.X) | Byte(colour.Y) << 8 | Byte(colour.Z) << 16 | Byte(colour.W) << 24;
    }
}
