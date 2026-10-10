// The aimed block's highlight, one record per frame slot (the mesh sample's AimHighlightPass). Mirrors
// Itera.MeshSample.Rendering.AimHighlightData member for member; AimHighlightPassTests checks it.
#extension GL_EXT_scalar_block_layout : require

struct AimHighlightData
{
    mat4 transform;
    vec4 colour;
    float seconds;
    float edgeMetres;
};

layout(scalar, set = 0, binding = 0) restrict readonly buffer AimHighlights
{
    AimHighlightData highlights[];
};
