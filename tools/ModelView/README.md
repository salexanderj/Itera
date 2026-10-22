# ModelView

A neutral stage for checking a model as the engine draws it: no voxel world, a plain grey ground with a line every
10 cm and a darker one every metre, the engine's own sky and look, and the models named by `Engine:Assets:Extra`. It
looks the same for every game, so a model is judged on its own. To see a model in a game's own world and light, run the
game with the same option instead. A development tool, not game code; `tools/models/build.py views` runs it.

It starts offscreen, straight into a match, with no sound, Steam, mods, loading screen, or dynamic render scaling
(`appsettings.json`), and draws at 1024 by 1024. A run with two screenshots takes under 2 s.

```bash
dotnet build tools/ModelView
tools/ModelView/bin/Debug/net10.0/ModelView --Engine:Application:DataFolder=IteraModelView-agent \
  --Engine:Console:ExitOnFailure=true \
  --Engine:Assets:Extra:0:Name=crate --Engine:Assets:Extra:0:Path=$PWD/crate.glb \
  "--Engine:Console:Run=wait loaded; sun 135 40; meshes place crate at 0 0 0 0; viewpoint 0,1,4,0,-10; wait frames 5; screenshot noui /tmp/front.png; quit"
```

- The ground lies at y = 0 round the world's middle, 30 m each way. The camera stands 6 m in front of it; `viewpoint`
  draws from anywhere else.
- `meshes place NAME at X Y Z YAW [SCALE] [CLIP [SECONDS]]` puts a model there (YAW 0 faces its front, glTF's +z,
  towards +z), playing a clip or held at a time of it.
- `viewpoint x,y,z,yaw,pitch` draws from that place (degrees; yaw 0 looks along -z), and `lens ortho HEIGHT` gives an
  orthographic view of HEIGHT metres.
- `sun AZIMUTH ELEVATION` sets the light, and `wait frames 5` lets a new view settle before `screenshot noui PATH`.
- `--Engine:Window:Mode=Window` opens a window to look round by hand.

Its log goes to the data folder given (`~/.local/share/IteraModelView-agent/Logs/` above). Its tests are
`tests/Itera.ModelView.Tests`.
