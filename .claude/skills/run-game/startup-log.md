# The start-up log, exit codes, and log files

## The run's start

A successful run logs the run's steps first, in this order:

- `Network: ready; no socket until a session starts`.
- `Steam: off ...` for a game with no App ID; a game with one logs `Steam: signed in as ...` or a Warning.
- The window, the Vulkan instance, one line per physical device, the selected device, and the swapchain.
- The input devices, the shader compiler and its smoke test, the audio device, and `UI: Skia CPU surface ...`.
- `Loaded mod itera.samplemod`, or `Mods: off ...` with `Engine:Mods:Enabled` false.
- `Mesh assets: N model(s), ... read in N ms`: the models the game declared, with how many textures came from their
  compressed textures. A file that cannot be read stops the run with exit code 1. Compressed textures that do not match
  their image give a Warning, and a model whose textures were all decoded an Information note.
- `Mesh assets on the GPU: N vertices, ... draws N MB a frame slot`, and with a skinned model
  `... N skinned vertices; posed instances skinned into V vertices ...`.
- For each image view a game declared: `Image view NAME: WxH texels, one sample a pixel, drawn at most every N frame(s)`.
- For each pass a game registered: `Render pass of the game's: NAME at the STAGE stage`.
- `Audio: thread started ...`, `Render thread started`, and `Engine ready ... entering main loop`.

## The match's start

- `Match 1: offline, seed N`, or `host, seed N`, `host through Steam, ...`, or `join ADDRESS`.
- `Session: offline, seed N`, or `hosting on UDP port ...`, `hosting through Steam ...`, or
  `joined ADDRESS in slot N, seed N; downloaded ...`. With a save name, a `Save: starting from NAME ...` or
  `Save: NAME does not exist yet ...` line comes before it.
- Physics, then `Meshes: N material texture set(s) ...`, then `Voxels: N worker thread(s), budget ..., generator NAME vN`.
  On a client: `Session: loaded N edit(s) of the host's world`. From a save: `Save: loaded N edit(s) of NAME`.
- The ECS world, the game's own lines, `Loading: waiting for N task(s)`, and `Loading: done after N s (...)`.

## The end, and exit codes

- Quit in the menu, the close button, Ctrl+C (SIGINT), SIGTERM, or SIGHUP end the run with exit code 0. The shutdown
  lines come in reverse order, the match's first, and end with `Shutdown complete after N s; exit code 0`.
- The console's `quit CODE` gives CODE. A startup script that failed with `Engine:Console:ExitOnFailure` gives 1.
- A second signal more than 0.5 s after the first ends the process at once (exit code 130 for SIGINT).
- An exception in initialisation or the loop is logged at Critical, shutdown still runs, and the exit code is 1. In a
  match started from the menu, it ends only the match.
- An invalid option (for example `--Engine:FixedStepHz=0`) or an unknown key under `Engine` or `Game` (for example
  `--Engine:Renderer:VSync`; `UnknownOptionKeys`) stops the game before the window opens. The exit code is 1, and the
  message names the key.

## The log file

Every run writes `~/.local/share/<DataFolder>/Logs/<datafolder>-<date>-<time>-<pid>.log` (`LocalApplicationData` on
other systems) with Debug detail. A `DataFolder` that is a full path holds its own `Logs` folder, and the files take its
last folder's name. Read the newest file there first when a run misbehaves. A log holds:

- a session header and the start's timings;
- every 10 s, a frame stats line: frames, frame times (median, 95th and 99th percentiles), fixed steps, bytes allocated
  per frame on the main and render threads, garbage collections, and GPU time;
- Debug lines with each GPU pass (`GPU passes over ...`, with a game's passes and the image views among them), the
  fixed steps' CPU time by part, the eight slowest fixed systems (`SystemTimes`), and the frame's CPU times
  (`Frame CPU over ...`: the request's wait, the build, the recording, the present, and the fence wait with acquire);
- a voxel stats line (columns, chunks, bricks, memory against the budget, loads, unloads, and job times);
- a meshes line (blocks meshed and pending, uploads, the mesh heap, triangles drawn, and the main thread's time);
- a `Particles:` line while any live;
- at each match's end, the size of its edit log (`Voxels: the match ends with N edit(s) in its log, ...`): the edits
  since its last reset, the KB a save or a join sends of them, the lifted pieces, and the index's entries;
- the shutdown steps.

The console shows Information and above. Lines from the render thread carry its thread's id (for example `[T21]`)
instead of `[T1]`.

`LogRetention` keeps the newest `Engine:Logging:RetainedFiles` files, the new one included, and deletes any older than
`RetainedDays` (0: no age limit); a file rolls over past `FileSizeLimitMegabytes`. Warnings to know, each reported once:
the frame's models past what `ModelInstances` holds (`BuildFrame`), and mesh assets past theirs. The voxel world's
memory past its budget warns at most once a minute. A Debug line says when no frame was presented for 0.5 s.
