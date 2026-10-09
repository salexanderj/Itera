#version 460

// Debug lines (DebugLinePass): each line in its own colour, drawn over the picture.
layout(location = 0) in vec4 inColour;
layout(location = 0) out vec4 outColour;

void main()
{
    outColour = inColour;
}
