#version 460
#extension GL_EXT_scalar_block_layout : require

// The loading screen (LoadingPass): a retro ring of voxels in chunky pixels, a wave running round it and a brighter head
// sweeping it, over dark blue, with CRT scan lines. From a Shadertoy shader; Shadertoy counts
// fragCoord from the bottom-left corner and Vulkan's gl_FragCoord from the top-left, so y is turned over. Its colours
// are display (sRGB) values, made linear for an sRGB swapchain image, which encodes what it is given. Premultiplied by
// alpha for the fade over the world.
layout(location = 0) in vec2 inNdc;
layout(location = 0) out vec4 outColour;

layout(push_constant, scalar) uniform LoadingConstants
{
    uint width;
    uint height;
    float time;
    float alpha;
    uint linearOutput;
}
loading;

const float PI = 3.14159265359;

vec3 srgbToLinear(vec3 c)
{
    return mix(c / 12.92, pow((c + 0.055) / 1.055, vec3(2.4)), greaterThan(c, vec3(0.04045)));
}

void main()
{
    vec2 resolution = vec2(loading.width, loading.height);
    vec2 fragCoord = vec2(gl_FragCoord.x, resolution.y - gl_FragCoord.y);

    // Chunky pixels: every 6 x 6 block of screen pixels takes one colour.
    float pixelScale = 6.0;
    vec2 pixelatedCoord = floor(fragCoord / pixelScale) * pixelScale;

    // -1 to 1 up the screen's height, 0 at its centre.
    vec2 uv = (pixelatedCoord - 0.5 * resolution) / resolution.y;

    // Polar coordinates: the distance from the centre and the angle round it, 0 to 2 pi.
    float dist = length(uv);
    float angle = atan(uv.y, uv.x) + PI;

    // Voxels: 8 steps out from the centre, 24 wedges round it.
    float radialSteps = 8.0;
    float angularSteps = 24.0;
    float vRadius = floor(dist * radialSteps);
    float vAngle = floor((angle / (2.0 * PI)) * angularSteps);

    // Where the point lies inside its voxel, for the voxels' outlines.
    float fRadius = fract(dist * radialSteps);
    float fAngle = fract((angle / (2.0 * PI)) * angularSteps);

    // The background: a dark blue.
    vec3 colour = vec3(0.04, 0.04, 0.08);

    // The ring: voxels from the third step out to the fifth.
    if (vRadius >= 2.0 && vRadius <= 4.0)
    {
        // A wave running round the ring sets how far out each wedge's voxels reach, in whole voxels.
        float waveInput = vAngle - loading.time * 4.0;
        float targetRadius = 2.0 + floor((sin(waveInput * 0.4) * 0.5 + 0.5) * 3.0);
        if (vRadius <= targetRadius)
        {
            // From a warm grey at the inner edge to an ochre brown at the outer.
            vec3 innerColour = vec3(0.5686, 0.5569, 0.5216);
            vec3 outerColour = vec3(0.5137, 0.3961, 0.2235);
            colour = mix(innerColour, outerColour, (vRadius - 2.0) / 2.0);

            // The head: the wedges near a sweep round the ring are brighter.
            float spinnerSweep = fract(loading.time * 0.5) * angularSteps;
            if (abs(vAngle - floor(spinnerSweep)) < 2.0)
            {
                colour *= 1.4;
            }

            // Dark seams between the voxels.
            if (step(0.85, fRadius) + step(0.85, fAngle) > 0.0)
            {
                colour *= 0.15;
            }
        }
    }

    // CRT scan lines.
    colour *= sin(pixelatedCoord.y * 1.2) * 0.2 + 0.8;

    colour = clamp(colour, 0.0, 1.0);
    vec3 linearColour = loading.linearOutput != 0u ? srgbToLinear(colour) : colour;
    outColour = vec4(linearColour * loading.alpha, loading.alpha);
}
