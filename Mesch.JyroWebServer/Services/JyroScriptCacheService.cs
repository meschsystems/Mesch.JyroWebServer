using System.Collections.Concurrent;
using Mesch.Jyro;

namespace Mesch.JyroWebServer.Services;

/// <summary>
/// A FileSystemWatcher-based caching service for compiled Jyro programs.
/// Caches compiled CompiledProgram objects and automatically invalidates them when source files change,
/// providing efficient hot-reload functionality by avoiding repeated compilation overhead.
/// </summary>
public class JyroScriptCacheService : IDisposable
{
    private readonly ConcurrentDictionary<string, CompiledProgram> _cache = new();
    private readonly FileSystemWatcher _watcher;
    private readonly ILogger<JyroScriptCacheService> _logger;
    private readonly string _scriptsPath;

    public JyroScriptCacheService(string scriptsPath, ILogger<JyroScriptCacheService> logger)
    {
        _logger = logger;
        _scriptsPath = Path.GetFullPath(scriptsPath);

        // Set up file watcher for the scripts directory
        _watcher = new FileSystemWatcher(_scriptsPath)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
            Filter = "*.jyro",
            IncludeSubdirectories = true,
            EnableRaisingEvents = true
        };

        _watcher.Changed += OnScriptChanged;
        _watcher.Created += OnScriptChanged;
        _watcher.Deleted += OnScriptChanged;
        _watcher.Renamed += OnScriptRenamed;

        _logger.LogInformation("Jyro script cache initialized with FileSystemWatcher for: {ScriptsPath}", scriptsPath);
    }

    private void OnScriptChanged(object sender, FileSystemEventArgs e)
    {
        InvalidateByFilePath(e.FullPath);
    }

    private void OnScriptRenamed(object sender, RenamedEventArgs e)
    {
        InvalidateByFilePath(e.OldFullPath);
        InvalidateByFilePath(e.FullPath);
    }

    private void InvalidateByFilePath(string filePath)
    {
        var normalizedPath = Path.GetFullPath(filePath);

        // Find cache key that matches this file path
        var keyToRemove = _cache.Keys
            .FirstOrDefault(key => string.Equals(
                Path.GetFullPath(key),
                normalizedPath,
                StringComparison.OrdinalIgnoreCase));

        if (keyToRemove != null && _cache.TryRemove(keyToRemove, out _))
        {
            var relativePath = Path.GetRelativePath(_scriptsPath, filePath);
            _logger.LogInformation("Script cache invalidated: {ScriptPath}", relativePath);
        }
    }

    /// <summary>
    /// Tries to retrieve a cached compiled program for the specified script path.
    /// </summary>
    /// <param name="scriptPath">The absolute path to the script file.</param>
    /// <param name="compiledProgram">The cached compiled program if found.</param>
    /// <returns>True if the compiled program was found in cache, false otherwise.</returns>
    public bool TryGetCachedProgram(string scriptPath, out CompiledProgram? compiledProgram)
    {
        if (_cache.TryGetValue(scriptPath, out var program))
        {
            compiledProgram = program;
            var relativePath = Path.GetRelativePath(_scriptsPath, scriptPath);
            _logger.LogDebug("Script cache hit: {ScriptPath}", relativePath);
            return true;
        }

        compiledProgram = null;
        _logger.LogDebug("Script cache miss: {ScriptPath}", Path.GetRelativePath(_scriptsPath, scriptPath));
        return false;
    }

    /// <summary>
    /// Caches a compiled program for the specified script path.
    /// </summary>
    /// <param name="scriptPath">The absolute path to the script file.</param>
    /// <param name="compiledProgram">The compiled program to cache.</param>
    public void CacheProgram(string scriptPath, CompiledProgram compiledProgram)
    {
        _cache[scriptPath] = compiledProgram;
        var relativePath = Path.GetRelativePath(_scriptsPath, scriptPath);
        _logger.LogDebug("Script cached: {ScriptPath}", relativePath);
    }

    /// <summary>
    /// Removes a script from the cache.
    /// </summary>
    /// <param name="scriptPath">The absolute path to the script file.</param>
    public void Invalidate(string scriptPath)
    {
        if (_cache.TryRemove(scriptPath, out _))
        {
            var relativePath = Path.GetRelativePath(_scriptsPath, scriptPath);
            _logger.LogDebug("Script manually invalidated: {ScriptPath}", relativePath);
        }
    }

    /// <summary>
    /// Clears all cached scripts.
    /// </summary>
    public void ClearAll()
    {
        var count = _cache.Count;
        _cache.Clear();
        _logger.LogInformation("Script cache cleared: {Count} scripts removed", count);
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _cache.Clear();
    }
}
