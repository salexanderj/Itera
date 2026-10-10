// Sky colour for a view direction: a gradient from the horizon colour to a deeper zenith, a darker colour below the
// horizon, and the sun's disc in the sunlight's colour, as the frame's look and atmosphere make them (look.glsl, worked
// out once a frame: an overcast sky flattens the gradient towards the horizon colour and hides the disc). Needs
// view.glsl.
#include "look.glsl"

vec3 skyColour(vec3 direction)
{
    vec3 colour = direction.y >= 0.0
        ? mix(view.skyHorizon, LOOK.zenith, sqrt(direction.y))
        : mix(view.skyHorizon, LOOK.below, sqrt(-direction.y));
    float sun = max(dot(direction, view.sunDirection), 0.0);
    return colour + LOOK.sunDisc * pow(sun, LOOK.sunDiscSharpness);
}
