// The air between the eye and a surface, for what mixes into the sky (mesh-shade.glsl, grass.frag, particle-cubes.frag):
// the surface's colour faded into the sky's colour behind it (sky.glsl), each colour by the look's fade in the haze
// (lighting.glsl's fogAmounts), and the frame's haze glows' light along the way. At the view's end every colour is the
// sky's, so the far ground meets the sky. Include after view.glsl, sky.glsl, and lighting.glsl.

// colour, linear and lit, as seen through the air from the eye to offset when the view ends at maxT metres. fog is how
// much of it is lost, as one colour (for what fades with the fog: the split tone, a glow).
vec3 throughAir(vec3 colour, vec3 offset, float maxT, out float fog)
{
    vec3 amounts = fogAmounts(offset, maxT);
    fog = dot(amounts, LumaWeights);
    vec3 ray = viewRayTo(offset);
    return mix(colour, skyColour(ray), amounts) + hazeGlows(ray, length(offset));
}
