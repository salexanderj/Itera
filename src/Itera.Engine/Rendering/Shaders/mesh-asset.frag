#version 460

// The mesh assets' surfaces (MeshAssetPass): glTF's base colour (the material's factor times its texture, the vertex
// colour, and the draw's tint), compiled with MESH_ASSET_MASK for materials whose pixels below the alpha cutoff are not
// drawn, and with MESH_ASSET_BLEND for blended ones (drawn over what is behind by their alpha, premultiplied, the faces
// their draw shows alone: a two-sided one's back faces in one draw, then its front faces); the normal map tilting the normal the light meets (tangent space: x and y from the map, z rebuilt; both faces
// of a two-sided material lit from their own side); the occlusion as the hollows that hide a surface from the sky; the
// emission as the glow; then the light and the grade every surface of MeshPass takes (mesh-shade.glsl) with the
// openness of flat ground. Metalness and roughness are not drawn yet.
#include "view.glsl"
#include "sky.glsl"
#include "lighting.glsl"
#include "air.glsl"
#include "mesh-data.glsl"
#include "mesh-light.glsl"
#include "mesh-shade.glsl"
#include "mesh-asset-data.glsl"

layout(location = 0) in vec3 inOffset;
layout(location = 1) in vec3 inNormal;
layout(location = 2) in vec4 inTangent;
layout(location = 3) in vec4 inUv;
layout(location = 4) in vec4 inColour;
layout(location = 5) flat in uvec2 inMaterialTint;
#ifdef MESH_ASSET_BLEND
layout(location = 6) flat in uint inSides;
#endif
layout(location = 0) out vec4 outColor;

void main()
{
#ifdef MESH_ASSET_BLEND
    // The faces this draw does not show: the whole primitive faces one way, so its 2x2 groups go together.
    if ((inSides == MESH_ASSET_FRONT_ONLY && !gl_FrontFacing) || (inSides == MESH_ASSET_BACK_ONLY && gl_FrontFacing))
    {
        discard;
    }
#endif

    MeshAssetMaterial material = assetMaterials[inMaterialTint.x];
    // Every texture is read first, where all of a 2x2 group of pixels run (the mip level comes from their differences);
    // a reference to none reads white, alike for the whole draw.
    vec4 baseSample = assetTexture(material.baseColourTexture, inUv);
    vec4 normalSample = assetTexture(material.normalTexture, inUv);
    vec4 occlusionSample = assetTexture(material.occlusionTexture, inUv);
    vec4 emissionSample = assetTexture(material.emissionTexture, inUv);
    vec4 base = material.baseColour * inColour * baseSample;
    if (inMaterialTint.y != MESH_ASSET_NO_TINT)
    {
        base *= unpackUnorm4x8(inMaterialTint.y);
    }

#ifdef MESH_ASSET_MASK
    if (base.a < material.alphaCutoff)
    {
        discard;
    }
#endif

    // The back of a two-sided surface is lit from its own side: its normal, tangent, and bitangent turned round.
    float side = gl_FrontFacing ? 1.0 : -1.0;
    vec3 normal = normalize(inNormal) * side;
    vec3 lit = normal;
    if (assetHasTexture(material.normalTexture))
    {
        vec3 tangent = inTangent.xyz * side;
        tangent -= normal * dot(normal, tangent);
        float length2 = dot(tangent, tangent);
        if (length2 > 1e-12)
        {
            tangent *= inversesqrt(length2);
            vec3 bitangent = cross(normal, tangent) * inTangent.w * side;
            vec2 xy = (normalSample.rg * 2.0 - 1.0) * material.normalScale;
            float z = sqrt(max(1.0 - dot(xy, xy), 0.0));
            lit = normalize(tangent * xy.x + bitangent * xy.y + normal * z);
        }
    }

    float cavity = assetHasTexture(material.occlusionTexture)
        ? mix(1.0, occlusionSample.r, material.occlusionStrength)
        : 1.0;
    vec3 emission = material.emission * emissionSample.rgb;
    float glow = max(emission.r, max(emission.g, emission.b)) > 0.0 ? 1.0 : 0.0;
    // The edge light is the mesh assets' alone: figures and creatures stand out from the ground.
    vec3 shaded = shadeSurface(base.rgb, normal, lit, inOffset, OpennessFlat, cavity, emission, glow, 1.0);
#ifdef MESH_ASSET_BLEND
    float alpha = clamp(base.a, 0.0, 1.0);
    outColor = vec4(shaded * alpha, alpha);
#else
    outColor = vec4(shaded, 1.0);
#endif
}
