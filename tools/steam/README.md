# Steam test builds

`upload.sh` builds the game (`src/Game`) for Windows x64 and Linux x64 and uploads both to Steam as one build, set live
on the beta branch `testing`. Testers' Steam clients then download only the files that changed.

```bash
tools/steam/upload.sh --preview    # build, then list what would be uploaded; uploads nothing
tools/steam/upload.sh              # test (Debug and Release), build, upload, and set live on "testing"
tools/steam/upload.sh --no-test    # the same without "dotnet test"
tools/steam/upload.sh --no-live    # upload without setting it live anywhere
tools/steam/upload.sh --branch NAME  # set it live on beta branch NAME instead of "testing"
```

## What the game gives

- `Engine:Steam:AppId` in `src/Game/appsettings.json`: the app's ID. 0 (the starter's) leaves Steam off and stops the
  script.
- `Engine:Application:Name`: the name in each build's description.
- `src/Game/steam.env`, shell assignments the script sources (no secrets; commit it with the game):

  ```bash
  STEAM_WINDOWS_DEPOT=1234562   # the app's depot IDs (Steamworks: SteamPipe, Depots)
  STEAM_LINUX_DEPOT=1234563
  STEAM_BUILDER=builder-account  # the steamcmd account (see steamcmd below)
  STEAM_EXECUTABLE=MyGame        # optional: the executable's name in the depots (MyGame.exe, MyGame); default Game
  ```

The script refuses to upload with uncommitted changes, so every build is a commit. Its description on the Steamworks
builds page is `<Name> <commit> <date>`, and a tester's log header (`<Name> 1.0.0+<commit>`) names the same commit.
Everything it writes goes under `publish/steam/` (ignored by git): `content/<rid>/` (the published game), `scripts/`
(the build scripts it wrote), and `output/` (the test, publish, and steamcmd logs, and steamcmd's cache, which makes
later uploads faster; keep it).

Each configuration's tests run after its build, with the build servers shut down between the two: with SDK 10.0.112 a
test run started straight after a build has aborted at once with "Internal CLR error (0x80131506)" and no results. A
run that aborts that way runs once more; a failing test stops the upload.

## What goes into the depots

Each depot is `dotnet publish -c Release -r <rid> --self-contained` with composite ReadyToRun and a single file. The
install folder holds about 13 entries:

```
Game (Game.exe)         the game, its packages, and .NET in one file (named STEAM_EXECUTABLE when set)
libglfw.so.3            glfw3.dll           windows and input
libopenal.so            soft_oal.dll        audio
libshaderc_shared.so    shaderc_shared.dll  the shader compiler
libSkiaSharp.so         libSkiaSharp.dll    the UI's drawing
libsteam_api.so         steam_api64.dll     Steam
Game.pdb, Itera.Engine.pdb, Itera.Modding.pdb
appsettings.json
Fonts/, Models/, Shaders/, Textures/  (Sounds/ with sound files, and Mods/ when a player adds mods)
```

Single file (`-p:PublishSingleFile=true`) puts every managed assembly and the .NET runtime into the game's file. The
native libraries stay beside it: bundling them would mean extracting them to a temporary folder at the first start of
each version, and the engine loads `steam_api` from the game's folder. The game finds its files through
`AppContext.BaseDirectory`, which is the game's folder in a single-file build too. Mods load from `Mods/` beside it.
Each update downloads most of the game's file; composite ReadyToRun costs that anyway, as its compiled code changes
throughout with every build.

Composite ReadyToRun (`-p:PublishReadyToRun=true -p:PublishReadyToRunComposite=true`) compiles the game, its packages,
and .NET ahead of time into one file, so the game compiles less while it starts and the first time it runs a piece of
code. The JIT still replaces hot methods with its optimised versions, and it still compiles mods. Measured against a
publish without it: the ground at the start loaded in 0.5 s instead of 1.6 s, and the first 10 s dropped no fixed steps
instead of 5; world generation ran at the same speed once warm. The published game is about 27 MB larger, and
publishing takes about twice as long. Under it, tests that measure allocation in the first calls of a method see the
ahead-of-time code's allocations until the JIT replaces it.

Not yet checked: the Windows build running. Microsoft's ReadyToRun page does not list Windows as a target when
compiling on Linux; the Linux compiler package does include a Windows JIT, and the win-x64 publish succeeds.

Both leave out `steam_appid.txt` (Valve says not to ship it; Steam gives a game it starts its App ID itself), the
`.xml` API docs, `appsettings.Development.json`, and the other platforms' Steam libraries. The Windows depot also leaves
out `libSkiaSharp.pdb`, Skia's native debug symbols (99 MB). The game's own `.pdb` files stay, so crash lines carry file
and line numbers. Release, because Debug turns on the Vulkan validation layers.

## Steamworks settings

- The two depots in the Developer Comp package, the Beta Testing package the testers' keys come from, and the store
  package.
- Launch options for Windows (`Game.exe`) and Linux (`Game`), or the names `STEAM_EXECUTABLE` gives: "Play", and
  "Host a game through Steam" (`--Engine:Session:Mode=Host --Engine:Session:Transport=Steam`). Steam asks which one on
  Play.
- Linux runtime: Steam Linux Runtime 3.0 (sniper).
- Branch `testing`, no password. The Builds page shows no branch controls until the app has a build, so the first build
  goes up with `--no-live`; then make the branch there and set that build live on it and on the default branch.
- steamcmd cannot set a build live on the default branch. Set it there by hand on the builds page when a first install
  should get a newer build.

## steamcmd

steamcmd comes from apt (`/usr/games/steamcmd`, which runs `~/.local/share/Steam/steamcmd/steamcmd.sh`). It logs in as
`STEAM_BUILDER`, best a builder account with only "Edit App Metadata" and "Publish App Changes To Steam". The script
never passes a password: steamcmd keeps the login it saved when the account logged in by hand, in
`~/.steam/debian-installation/config/config.vdf` (the Steam client's config file, shared with it). Never commit or share
that file.

When the saved login expires, or Steam asks for a Steam Guard code again, the script stops with a message. Then log in
by hand once and upload again:

```bash
steamcmd +login builder-account     # password, then the Steam Guard code from the email; then "quit"
```

`STEAMCMD` chooses another steamcmd.

## For testers

1. Activate the key, and install the game from the Steam library.
2. Once: right-click the game > Properties > Game Versions & Betas, and pick `testing`. Steam then updates the game
   after each upload.
3. To join a game: open the friends list, right-click the host, and pick "Join Game". Steam starts the game and joins
   the host's lobby. To host: Play > "Host a game through Steam".
4. Logs: `%LOCALAPPDATA%\<DataFolder>\Logs` on Windows, `~/.local/share/<DataFolder>/Logs` on Linux
   (`Engine:Application:DataFolder`). Send the newest file after a problem.

The host and every client must run the same build: the world generator and the network protocol must match. Steam
updates a game only while it is closed, so everyone quits and starts again after an upload. Host from the Steam install
(or a Release build of the same commit), not from a Debug dev build.
