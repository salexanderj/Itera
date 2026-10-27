// The point lights' halos in the air: the light the haze scatters towards the eye from the frame's brightest point lights
// along a view ray, so lamps, fires, and flashes glow in the air round them. The frame's lights come brightest first
// (power over the squared distance from the eye), and only the look's halo count of them is taken, at a few
// instructions each. Include after view.glsl, look.glsl, and mesh-data.glsl.

// The halos' light along the ray from the eye along direction over t metres: for each of the brightest lights, its
// colour and power times the integral along the ray of one over the squared distance to it softened by the look's halo
// radius, times the look's halo strength, and faded to none where the ray passes past the light's reach. None in an
// orthographic view, whose rays do not start at the eye, or while the look's halo strength is 0.
vec3 lightHalos(vec3 direction, float t)
{
    uint count = min(lighting[view.slot].lightCount, LOOK.haloLights);
    if (count == 0u || LOOK.halo <= 0.0 || viewOrthographic())
    {
        return vec3(0.0);
    }

    uint first = view.slot * MESH_MOST_LIGHTS;
    float soft = LOOK.haloRadius * LOOK.haloRadius;
    vec3 sum = vec3(0.0);
    for (uint i = 0u; i < count; i++)
    {
        MeshLight light = lights[first + i];
        vec3 to = light.position - view.position;
        float along = dot(to, direction);
        float passing = max(dot(to, to) - along * along, 0.0);
        float reach = light.reach * light.reach;
        if (passing >= reach)
        {
            continue;
        }

        float near = sqrt(passing + soft);
        float gathered = atan((t - along) / near) + atan(along / near);
        float window = 1.0 - passing / reach;
        sum += light.colour * (light.power * gathered / near * window * window);
    }

    return sum * LOOK.halo;
}
