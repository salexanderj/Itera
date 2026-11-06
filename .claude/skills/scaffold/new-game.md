# Adding to Itera: a new game

The `scaffold` skill's steps for starting a game from a clone of Itera.

## Setting up a clone

1. Clone Itera and keep it as a remote (`git remote rename origin itera`), to pull engine changes later.
2. Run `dotnet tool restore`, for csharpier, and call `tools/git/pre-push` from `.git/hooks/pre-push`
   (`tools/git/README.md`), so a push CI would fail on its format starts no run.
3. Install the C# language server: `dotnet tool install --global csharp-ls`. The repository's `.claude/settings.json`
   enables the csharp-lsp plugin, which runs it and gives Claude Code the LSP tool.
4. Install the plugin from the claude-plugins-official marketplace when Claude Code offers it. The plugin directory's
   copy has no server setup, so with it no LSP tool appears.

## Naming and growing the game

1. In `src/Game/appsettings.json` set `Engine:Application:Name`, `DataFolder` (two games on one machine need two), and
   `Engine:Window:Title`. For Steam set `Engine:Steam:AppId`, and a `steam.env` in `src/Game` for `tools/steam/upload.sh`.
2. Rewrite `GAME.md`, and grow `src/Game`: its world (`WorldGeneration/`), its systems (registered in
   `GameServiceCollectionExtensions` in run order), its options under `Game:`, and its tests in `tests/Game.Tests`.
   The folder, project, and namespace stay `Game`, so the tools and skills find it.
