# Steam's native libraries

Valve's `steam_api` redistributables from the Steamworks SDK 1.60, as shipped in the Steamworks.NET 2024.8.0
Standalone release (https://github.com/rlabrecque/Steamworks.NET/releases/tag/2024.8.0), which matches the
Steamworks.NET 2024.8.0 NuGet package (the package has no native libraries). Change them only together with that
package's version in `Directory.Packages.props`.

- `libsteam_api.so`: Linux x64.
- `libsteam_api.dylib`: macOS, universal (x64 and arm64).
- `steam_api64.dll`: Windows x64.

All three are copied next to the game; each platform's loader finds its own. There is no Steam library for Linux or
Windows on arm64: there the game runs without Steam. On an arm64 Mac the universal library runs with the package's
Unix x64 managed build marked for arm64, which a target in `Directory.Build.targets` copies in for platforms with no
build of their own.
