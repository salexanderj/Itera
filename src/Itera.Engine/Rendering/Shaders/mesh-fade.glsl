// The share of its pixels a model draw shows (MeshModelDraw.Shown: a range of thresholds from 0 to 1 in 65,536ths, the
// start low and the steps above the end high; 0 for all, all ones for none): a pixel is drawn where its threshold, the screen's interleaved
// gradient noise (Jimenez 2014: no grid shows), lies in it, so two draws of one object fading into each other share its
// pixels between them (a voxel object's model levels, and its coarsest fading out over its far copy, which stays drawn
// whole). Compared in whole steps, so two draws meeting at a share leave no pixel to both or neither. mesh.frag's and
// mesh-wire.frag's model variants.
bool meshShows(uint shown)
{
    uint threshold = uint(65536.0 * fract(52.9829189 * fract(dot(gl_FragCoord.xy, vec2(0.06711056, 0.00583715)))));
    return threshold >= (shown & 0xFFFFu) && threshold < 65536u - (shown >> 16);
}
