using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Silk.NET.Shaderc;

namespace Itera.Engine.Rendering.Shaders;

/// <summary>
/// Compiles GLSL to SPIR-V for Vulkan 1.3 at runtime with Shaderc and caches the result on disk. Shader files live
/// under <see cref="SourceDirectory"/> (copied from <c>Rendering/Shaders/</c> in the engine project), a game's own under
/// <see cref="GameFolder"/> (<see cref="GameSourceDirectory"/>), and may use <c>#include "file"</c>; see
/// <see cref="ShaderSource"/>.
/// </summary>
public sealed unsafe partial class ShaderCompiler : IDisposable
{
    /// <summary>Change this when the cache file format or the key's contents change, so old entries are never used.</summary>
    private const int CacheFormat = 2;

    private const string CacheExtension = ".spv";

    /// <summary>
    /// A cache file nobody has loaded or written for this long is deleted by <see cref="PruneCache"/>. A file in use is
    /// never this old, because a load refreshes the file's write time once a day (<see cref="CacheRefreshAge"/>).
    /// </summary>
    public static readonly TimeSpan CacheMaxAge = TimeSpan.FromDays(30);

    /// <summary>A cache file loaded when its write time is older than this gets the time of now.</summary>
    private static readonly TimeSpan CacheRefreshAge = TimeSpan.FromDays(1);

    private const uint SpirvMagic = 0x07230203;
    private const int SpirvHeaderBytes = 20;
    private const OptimizationLevel Optimization = OptimizationLevel.Performance;

    /// <summary>
    /// SPIR-V for Vulkan 1.3 (SPIR-V 1.6), the minimum in <see cref="DeviceRequirements"/>. Shaderc defaults to
    /// Vulkan 1.0 when no target is set.
    /// </summary>
    private const EnvVersion TargetVersion = EnvVersion.Vulkan13;

    /// <summary>
    /// The folder of the shader tree that holds a game's own shaders (<c>Game/name.frag</c>; next to the executable,
    /// <c>Shaders/Game/</c>). The engine's shaders use no folder of this name.
    /// </summary>
    public const string GameFolder = "Game";

    private readonly ILogger<ShaderCompiler> _logger;
    private readonly string _cacheDirectory;
    private readonly string _sourceDirectory;
    private readonly string _gameSourceDirectory;
    private Shaderc? _shaderc;
    private Compiler* _compiler;
    private CompileOptions* _options;
    private int _pruneStarted;

    /// <param name="logger">Logger.</param>
    /// <param name="cacheDirectory">Folder for cached SPIR-V ("ShaderCache" in the application's data folder).</param>
    /// <param name="sourceDirectory">Folder that holds the shader files. Null uses "Shaders" next to the executable.</param>
    /// <param name="gameSourceDirectory">
    /// Folder that holds the game's own shaders, which paths under <see cref="GameFolder"/> name. Null uses
    /// "Shaders/Game" next to the executable, also when <paramref name="sourceDirectory"/> is another folder.
    /// </param>
    public ShaderCompiler(
        ILogger<ShaderCompiler> logger,
        string cacheDirectory,
        string? sourceDirectory = null,
        string? gameSourceDirectory = null
    )
    {
        _logger = logger;
        _cacheDirectory = cacheDirectory;
        _sourceDirectory = Path.GetFullPath(sourceDirectory ?? Path.Combine(AppContext.BaseDirectory, "Shaders"));
        _gameSourceDirectory = Path.GetFullPath(
            gameSourceDirectory ?? Path.Combine(AppContext.BaseDirectory, "Shaders", GameFolder)
        );
    }

    /// <summary>Folder the cache is written to.</summary>
    public string CacheDirectory => _cacheDirectory;

    /// <summary>Folder that shader paths passed to <see cref="Compile(string, string)"/> are relative to.</summary>
    public string SourceDirectory => _sourceDirectory;

    /// <summary>Folder that shader paths under <see cref="GameFolder"/> name: the game's own shaders.</summary>
    public string GameSourceDirectory => _gameSourceDirectory;

    /// <summary>
    /// Everything other than the source that changes the compiler's output: the cache format, the Shaderc binding
    /// version (which pins the native library), the target, and the optimisation level.
    /// </summary>
    private static string CompilerIdentity { get; } =
        $"{CacheFormat}|{typeof(Shaderc).Assembly.GetName().Version}|{TargetEnv.Vulkan}:{TargetVersion}|{Optimization}";

    /// <summary>Loads the native library and creates the compiler.</summary>
    public void Initialize()
    {
        _shaderc = Shaderc.GetApi();
        _compiler = _shaderc.CompilerInitialize();
        _options = _shaderc.CompileOptionsInitialize();
        _shaderc.CompileOptionsSetOptimizationLevel(_options, Optimization);
        _shaderc.CompileOptionsSetTargetEnv(_options, TargetEnv.Vulkan, (uint)TargetVersion);

        try
        {
            Directory.CreateDirectory(_cacheDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogCacheUnavailable(_cacheDirectory, ex.Message);
        }

        LogInitialized(_sourceDirectory, _cacheDirectory);
    }

    /// <summary>
    /// Compiles the shader file at <paramref name="relativePath"/> under <see cref="SourceDirectory"/> (a game's under
    /// <see cref="GameFolder"/>, from <see cref="GameSourceDirectory"/>). The stage comes
    /// from the extension: <c>.vert</c>, <c>.frag</c>, or <c>.comp</c>. Throws <see cref="ShaderCompilationException"/>
    /// on a compiler error, a missing file, or a bad include.
    /// </summary>
    public byte[] Compile(string relativePath, string entryPoint = "main") => Compile(relativePath, [], entryPoint);

    /// <summary>
    /// Compiles the shader file at <paramref name="relativePath"/>, as <see cref="Compile(string, string)"/> does, with
    /// each name of <paramref name="defines"/> defined (<c>#define NAME</c>) right after its <c>#version</c> line, for
    /// a variant of one file.
    /// </summary>
    public byte[] Compile(string relativePath, IReadOnlyList<string> defines, string entryPoint = "main")
    {
        var name = relativePath.Replace(Path.DirectorySeparatorChar, '/');
        var kind = KindFromExtension(name);
        var source = ShaderSource.Expand(name, new ShaderRoots(_sourceDirectory, _gameSourceDirectory));
        if (defines.Count > 0)
        {
            var version = source.IndexOf("#version", StringComparison.Ordinal);
            var lineEnd = version < 0 ? -1 : source.IndexOf('\n', version);
            if (lineEnd < 0)
            {
                throw new ShaderCompilationException($"{name} has no #version line to put defines after.");
            }

            var lines = string.Concat(defines.Select(define => "#define " + define + "\n"));
            source = source.Insert(lineEnd + 1, lines);
        }

        return CompileSource(source, kind, name, entryPoint);
    }

    /// <summary>
    /// Compiles GLSL source text. <c>#include</c> is not expanded here; use <see cref="Compile(string, string)"/> for
    /// files. Throws <see cref="ShaderCompilationException"/> on error.
    /// </summary>
    /// <param name="source">GLSL source text.</param>
    /// <param name="kind">Shader stage.</param>
    /// <param name="name">Name used in error messages and logs.</param>
    /// <param name="entryPoint">Entry function name.</param>
    public byte[] CompileSource(string source, ShaderKind kind, string name, string entryPoint = "main")
    {
        if (_shaderc is null)
        {
            throw new InvalidOperationException("The shader compiler has not been initialised.");
        }

        var cachePath = CachePathFor(source, kind, entryPoint);
        if (cachePath is not null && TryReadCache(cachePath, name) is { } cached)
        {
            return cached;
        }

        LogCacheMiss(name);

        var sourceBytes = Encoding.UTF8.GetBytes(source);
        CompilationResult* result;
        fixed (byte* sourcePtr = sourceBytes)
        {
            result = _shaderc.CompileIntoSpv(
                _compiler,
                sourcePtr,
                (nuint)sourceBytes.Length,
                kind,
                name,
                entryPoint,
                _options
            );
        }

        try
        {
            var status = _shaderc.ResultGetCompilationStatus(result);
            var message = Marshal.PtrToStringUTF8((nint)_shaderc.ResultGetErrorMessage(result)) ?? string.Empty;
            if (status != CompilationStatus.Success)
            {
                throw new ShaderCompilationException(name, status, message);
            }

            var warnings = (int)_shaderc.ResultGetNumWarnings(result);
            if (warnings > 0)
            {
                LogCompilerWarnings(name, warnings, message.TrimEnd());
            }

            var length = (int)_shaderc.ResultGetLength(result);
            var bytes = new byte[length];
            new ReadOnlySpan<byte>(_shaderc.ResultGetBytes(result), length).CopyTo(bytes);

            if (cachePath is not null)
            {
                WriteCache(cachePath, bytes);
            }

            LogCompiled(name, kind, length);
            return bytes;
        }
        finally
        {
            _shaderc.ResultRelease(result);
        }
    }

    /// <summary>
    /// Starts <see cref="PruneCache"/> on a thread-pool task, the first time it is called in the run; later calls do
    /// nothing. Call it once start-up is done, so the pruning never delays the window.
    /// </summary>
    public void StartPruning()
    {
        if (Interlocked.Exchange(ref _pruneStarted, 1) == 0)
        {
            _ = Task.Run(PruneCache);
        }
    }

    /// <summary>
    /// Deletes the <c>.spv</c> files of the cache folder, and no other file, whose last write time is older than
    /// <see cref="CacheMaxAge"/>. A file that cannot be deleted stays. Returns how many it deleted. Any thread.
    /// </summary>
    public int PruneCache()
    {
        var deleted = 0;
        try
        {
            var oldest = DateTime.UtcNow - CacheMaxAge;
            foreach (var path in Directory.EnumerateFiles(_cacheDirectory))
            {
                if (!string.Equals(Path.GetExtension(path), CacheExtension, StringComparison.Ordinal))
                {
                    continue;
                }

                try
                {
                    if (File.GetLastWriteTimeUtc(path) < oldest)
                    {
                        File.Delete(path);
                        deleted++;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    LogPruneSkipped(path, ex.Message);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogPruneSkipped(_cacheDirectory, ex.Message);
        }

        LogPruned(deleted, _cacheDirectory);
        return deleted;
    }

    public void Dispose()
    {
        if (_shaderc is null)
        {
            return;
        }

        if (_options is not null)
        {
            _shaderc.CompileOptionsRelease(_options);
            _options = null;
        }

        if (_compiler is not null)
        {
            _shaderc.CompilerRelease(_compiler);
            _compiler = null;
        }

        // The native library stays loaded for the life of the process; unloading it at exit is unsafe.
        _shaderc = null;
    }

    /// <summary>True when <paramref name="bytes"/> can be a SPIR-V module: long enough, whole words, and the magic number.</summary>
    internal static bool LooksLikeSpirv(ReadOnlySpan<byte> bytes)
    {
        return bytes.Length >= SpirvHeaderBytes
            && bytes.Length % 4 == 0
            && BitConverter.ToUInt32(bytes[..4]) == SpirvMagic;
    }

    private static ShaderKind KindFromExtension(string path)
    {
        return Path.GetExtension(path) switch
        {
            ".vert" => ShaderKind.VertexShader,
            ".frag" => ShaderKind.FragmentShader,
            ".comp" => ShaderKind.ComputeShader,
            var extension => throw new ShaderCompilationException(
                $"Shader '{path}': cannot tell the stage from the extension '{extension}'. Use .vert, .frag, or .comp; "
                    + ".glsl files are for #include only."
            ),
        };
    }

    private string? CachePathFor(string source, ShaderKind kind, string entryPoint)
    {
        if (!Directory.Exists(_cacheDirectory))
        {
            return null;
        }

        var key = $"{CompilerIdentity}\0{kind}\0{entryPoint}\0{source}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        return Path.Combine(_cacheDirectory, hash + CacheExtension);
    }

    /// <summary>Returns the cached SPIR-V, or null when there is none or the file is damaged (it is then deleted).</summary>
    private byte[]? TryReadCache(string cachePath, string name)
    {
        byte[] cached;
        try
        {
            if (!File.Exists(cachePath))
            {
                return null;
            }

            cached = File.ReadAllBytes(cachePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogCacheUnavailable(cachePath, ex.Message);
            return null;
        }

        if (LooksLikeSpirv(cached))
        {
            RefreshWriteTime(cachePath);
            LogCacheHit(name, cachePath);
            return cached;
        }

        LogCacheDamaged(cachePath, cached.Length);
        try
        {
            File.Delete(cachePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogCacheUnavailable(cachePath, ex.Message);
        }

        return null;
    }

    /// <summary>
    /// Sets the write time of a cache file in use to now when it is more than a day old, so <see cref="PruneCache"/>
    /// keeps it. Last access times are not used: many mounts do not keep them.
    /// </summary>
    private void RefreshWriteTime(string cachePath)
    {
        try
        {
            var now = DateTime.UtcNow;
            if (now - File.GetLastWriteTimeUtc(cachePath) > CacheRefreshAge)
            {
                File.SetLastWriteTimeUtc(cachePath, now);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogCacheUnavailable(cachePath, ex.Message);
        }
    }

    /// <summary>
    /// Writes to a temporary file and renames it over the cache entry. A crash or a second game instance writing the
    /// same entry never leaves a partial file under the final name.
    /// </summary>
    private void WriteCache(string cachePath, byte[] bytes)
    {
        var temporaryPath = $"{cachePath}.{Environment.ProcessId}.tmp";
        try
        {
            File.WriteAllBytes(temporaryPath, bytes);
            File.Move(temporaryPath, cachePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogCacheUnavailable(cachePath, ex.Message);
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception deleteError) when (deleteError is IOException or UnauthorizedAccessException)
            {
                // The temporary file stays behind; it never matches a cache key.
            }
        }
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Shader compiler ready: sources in {SourceDirectory}, cache at {CacheDirectory}"
    )]
    private partial void LogInitialized(string sourceDirectory, string cacheDirectory);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Compiled shader {Name} ({Kind}): {Bytes} bytes of SPIR-V")]
    private partial void LogCompiled(string name, ShaderKind kind, int bytes);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Shader {Name} compiled with {Count} warning(s):\n{Warnings}")]
    private partial void LogCompilerWarnings(string name, int count, string warnings);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Shader cache unavailable at {Path}: {Reason}")]
    private partial void LogCacheUnavailable(string path, string reason);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Shader cache file {Path} is damaged ({Bytes} bytes, not SPIR-V); deleting it and compiling again"
    )]
    private partial void LogCacheDamaged(string path, int bytes);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Shader {Name} loaded from cache {Path}")]
    private partial void LogCacheHit(string name, string path);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Shader {Name} not in cache; compiling")]
    private partial void LogCacheMiss(string name);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Shader cache {Path} could not be pruned: {Reason}")]
    private partial void LogPruneSkipped(string path, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Pruned {Count} unused file(s) from the shader cache {Path}")]
    private partial void LogPruned(int count, string path);
}

/// <summary>Shaderc reported an error for a shader, or the shader's files could not be read or expanded.</summary>
public sealed class ShaderCompilationException : Exception
{
    public ShaderCompilationException(string shaderName, CompilationStatus status, string compilerOutput)
        : base($"Shader '{shaderName}' failed to compile ({status}):{Environment.NewLine}{compilerOutput}")
    {
        ShaderName = shaderName;
        Status = status;
        CompilerOutput = compilerOutput;
    }

    public ShaderCompilationException() { }

    public ShaderCompilationException(string message)
        : base(message) { }

    public ShaderCompilationException(string message, Exception innerException)
        : base(message, innerException) { }

    public string ShaderName { get; } = string.Empty;

    public CompilationStatus Status { get; }

    public string CompilerOutput { get; } = string.Empty;
}
