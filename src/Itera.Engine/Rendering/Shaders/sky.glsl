// Sky colour for a view direction: a gradient from the horizon colour to a deeper zenith (by the look's zenith power), a
// darker colour below the horizon, a band of light along the horizon, darker away from the sun and lighter over the half
// of the sky towards it, the sun's disc in the sunlight's colour, and the glow round it, as the frame's look and
// atmosphere make them (look.glsl, worked out once a frame: an overcast sky flattens the gradient towards the horizon
// colour and hides the disc). The fog takes this colour behind a surface (viewRayTo), so the haze takes the band, the
// light towards the sun, and the glow too. Needs view.glsl.
#include "look.glsl"

vec3 skyColour(vec3 direction)
{
    float towards = 0.5 + 0.5 * dot(direction, view.sunDirection);
    vec3 colour = direction.y >= 0.0
        ? mix(view.skyHorizon, LOOK.zenith, pow(direction.y, LOOK.zenithPower))
        : mix(view.skyHorizon, LOOK.below, sqrt(-direction.y));
    colour += LOOK.horizonBand * exp2(-abs(direction.y) * LOOK.horizonBandFalloff);
    float away = 1.0 - towards;
    colour *= 1.0 - LOOK.skyAwayDarken * away * away;
    colour += LOOK.skyLobe * pow(towards, LOOK.skyLobeSharpness);
    float sun = max(dot(direction, view.sunDirection), 0.0);
    return colour + LOOK.sunDisc * pow(sun, LOOK.sunDiscSharpness) + LOOK.sunGlow * pow(sun, LOOK.sunGlowSharpness);
}

// The light of glow the haze scatters towards the eye along the ray from the eye along direction over t metres: its
// colour times the integral along the ray of one over the squared distance to its middle softened by its radius
// (scaled so a ray through the middle from far away takes all its colour), dimmed by density's haze between the eye and
// the middle.
vec3 hazeGlow(HazeGlow glow, vec3 direction, float t, float density)
{
    vec3 to = glow.place - view.position;
    float along = dot(to, direction);
    float near = sqrt(max(dot(to, to) - along * along, 0.0) + glow.radius * glow.radius);
    float gathered = atan((t - along) / near) + atan(along / near);
    return glow.colour * (glow.radius * 0.31830989 * gathered / near * exp2(-length(to) * density));
}

// The light of the frame's haze glows (HazeGlow; at most four) along the ray from the eye along direction over t metres,
// dimmed by the look's haze. None in an orthographic view, whose rays do not start at the eye.
vec3 hazeGlows(vec3 direction, float t)
{
    if (LOOK.glowCount == 0u || viewOrthographic())
    {
        return vec3(0.0);
    }

    float density = LOOK.hazePerMetre;
    vec3 sum = hazeGlow(LOOK.glow0, direction, t, density);
    if (LOOK.glowCount > 1u)
    {
        sum += hazeGlow(LOOK.glow1, direction, t, density);
    }

    if (LOOK.glowCount > 2u)
    {
        sum += hazeGlow(LOOK.glow2, direction, t, density);
    }

    if (LOOK.glowCount > 3u)
    {
        sum += hazeGlow(LOOK.glow3, direction, t, density);
    }

    return sum;
}
