// Sky colour for a view direction: a gradient from the horizon colour to a deeper zenith, a darker colour below the
// horizon, and the sun's disc in the sunlight's colour. An overcast sky (view.air's y) flattens the gradient towards
// the horizon colour and hides the disc. Needs view.glsl.

vec3 skyColour(vec3 direction)
{
    float overcast = unpackUnorm4x8(view.air).y;
    vec3 zenith = view.skyHorizon * mix(vec3(0.35, 0.5, 0.85), vec3(0.8), overcast);
    vec3 below = view.skyHorizon * 0.45;
    vec3 colour = direction.y >= 0.0
        ? mix(view.skyHorizon, zenith, sqrt(direction.y))
        : mix(view.skyHorizon, below, sqrt(-direction.y));
    float sun = max(dot(direction, view.sunDirection), 0.0);
    return colour + view.sunColour * pow(sun, 400.0) * 4.0 * (1.0 - 0.9 * overcast);
}
