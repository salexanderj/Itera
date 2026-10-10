#version 460
#extension GL_EXT_scalar_block_layout : require

// The UI (UiPass): Skia's pixels, one per screen pixel, drawn over the picture, with the picture darkened under them by
// ui.dim (a fade to black). Each pixel is four bytes, blue lowest (Skia's BGRA 8888), premultiplied by alpha in sRGB.
// Under the pixels, the image views the HUD shows (RenderViews.Show): each panel's rectangle shows its view's image,
// darkened by the dim as the picture is. The pipeline blends premultiplied colour over the image.
layout(location = 0) in vec2 inNdc;
layout(location = 0) out vec4 outColour;

layout(set = 0, binding = 0, scalar) readonly buffer UiPixels
{
    uint pixels[];
};

// The image views' shown images (RenderViewPass), a stand-in where there is none.
layout(set = 0, binding = 1) uniform sampler2D views[4];

layout(push_constant, scalar) uniform UiConstants
{
    uint width;
    uint height;
    float dim;
    uint pixels;
    // The panels: how many, each one's view (a byte each, lowest first), and each one's rectangle in pixels (x and y
    // from the top-left, width, height).
    uint panels;
    uint panelViews;
    vec4 panel0;
    vec4 panel1;
    vec4 panel2;
    vec4 panel3;
}
ui;

vec4 panelRect(uint i)
{
    return i == 0u ? ui.panel0 : i == 1u ? ui.panel1 : i == 2u ? ui.panel2 : ui.panel3;
}

vec3 srgbToLinear(vec3 c)
{
    return mix(c / 12.92, pow((c + 0.055) / 1.055, vec3(2.4)), greaterThan(c, vec3(0.04045)));
}

void main()
{
    // gl_FragCoord counts from the framebuffer's top-left corner whatever the viewport, as Skia's rows do.
    uvec2 p = uvec2(gl_FragCoord.xy);
    vec3 colour = vec3(0.0);
    float alpha = 0.0;
    if (ui.pixels != 0u && p.x < ui.width && p.y < ui.height)
    {
        vec4 bgra = unpackUnorm4x8(pixels[p.y * ui.width + p.x]);
        alpha = bgra.a;
        if (alpha > 0.0)
        {
            // Skia premultiplied in sRGB, so the colour is divided by alpha before it becomes linear, and multiplied again
            // after: the swapchain image is sRGB, so blending happens in linear light.
            colour = srgbToLinear(clamp(bgra.bgr / alpha, 0.0, 1.0)) * alpha;
        }
    }

    // A panel under the UI covers the picture: its view's image (read as linear light from its sRGB texels), dimmed.
    for (uint i = 0u; i < min(ui.panels, 4u); i++)
    {
        vec4 rect = panelRect(i);
        vec2 at = (gl_FragCoord.xy - rect.xy) / rect.zw;
        if (all(greaterThanEqual(at, vec2(0.0))) && all(lessThan(at, vec2(1.0))))
        {
            uint view = (ui.panelViews >> (8u * i)) & 3u;
            vec3 shown = textureLod(views[view], at, 0.0).rgb * (1.0 - ui.dim);
            outColour = vec4(colour + (1.0 - alpha) * shown, 1.0);
            return;
        }
    }

    // The UI over black at the dim's alpha over the picture: premultiplied, black adds no colour.
    float covered = alpha + (1.0 - alpha) * ui.dim;
    if (covered <= 0.0)
    {
        discard;
    }

    outColour = vec4(colour, covered);
}
