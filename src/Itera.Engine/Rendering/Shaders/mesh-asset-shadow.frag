#version 460

// A masked mesh asset's shadow (MeshAssetPass): no depth where the base colour's alpha (the material's factor times its
// texture, the vertex colour, and the tint, as mesh-asset.frag finds it) is below the material's cutoff, so leaves and
// fences cast the shapes they show. Opaque materials' shadows use depth-only.frag.
#include "mesh-asset-data.glsl"

layout(location = 0) in vec4 inUv;
layout(location = 1) in float inAlpha;
layout(location = 2) flat in uint inMaterial;

void main()
{
    MeshAssetMaterial material = assetMaterials[inMaterial];
    float alpha = material.baseColour.a * inAlpha * assetTexture(material.baseColourTexture, inUv).a;
    if (alpha < material.alphaCutoff)
    {
        discard;
    }
}
