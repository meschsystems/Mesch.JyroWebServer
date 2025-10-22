using System.Collections.Concurrent;
using Microsoft.Extensions.Primitives;
using RazorLight;
using RazorLight.Caching;

namespace Mesch.JyroWebServer.Services;

/// <summary>
/// A FileSystemWatcher-based caching provider for RazorLight that caches compiled templates
/// and automatically invalidates them when the source files change.
/// This enables efficient hot-reload functionality - templates are cached for performance,
/// but automatically recompiled when modified.
/// </summary>
public class HotReloadCacheProvider : ICachingProvider
{
    private readonly ConcurrentDictionary<string, Func<ITemplatePage>> _cache = new();
    private readonly ConcurrentDictionary<string, string> _keyToFilePath = new();
    private readonly FileSystemWatcher _watcher;
    private readonly ILogger<HotReloadCacheProvider> _logger;
    private readonly string _templatesPath;

    public HotReloadCacheProvider(string templatesPath, ILogger<HotReloadCacheProvider> logger)
    {
        _logger = logger;
        _templatesPath = Path.GetFullPath(templatesPath);

        // Set up file watcher for the templates directory
        _watcher = new FileSystemWatcher(_templatesPath)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
            Filter = "*.cshtml",
            IncludeSubdirectories = true,
            EnableRaisingEvents = true
        };

        _watcher.Changed += OnTemplateChanged;
        _watcher.Created += OnTemplateChanged;
        _watcher.Deleted += OnTemplateChanged;
        _watcher.Renamed += OnTemplateRenamed;

        _logger.LogInformation("Hot-reload cache provider initialized with FileSystemWatcher for: {TemplatesPath}", templatesPath);
    }

    private void OnTemplateChanged(object sender, FileSystemEventArgs e)
    {
        InvalidateByFilePath(e.FullPath);
    }

    private void OnTemplateRenamed(object sender, RenamedEventArgs e)
    {
        InvalidateByFilePath(e.OldFullPath);
        InvalidateByFilePath(e.FullPath);
    }

    private void InvalidateByFilePath(string filePath)
    {
        // Find all cache keys that map to this file path
        var keysToRemove = _keyToFilePath
            .Where(kvp => string.Equals(kvp.Value, filePath, StringComparison.OrdinalIgnoreCase))
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in keysToRemove)
        {
            if (_cache.TryRemove(key, out _))
            {
                _keyToFilePath.TryRemove(key, out _);
                var relativePath = Path.GetRelativePath(_templatesPath, filePath);
                _logger.LogInformation("Template cache invalidated: {TemplatePath}", relativePath);
            }
        }
    }

    private string NormalizeKey(string key)
    {
        if (string.IsNullOrEmpty(key))
            return string.Empty;

        return key.Replace('\\', '/').TrimStart('/');
    }

    private string GetFilePathForKey(string key)
    {
        var normalizedKey = NormalizeKey(key);
        var filePath = Path.Combine(_templatesPath, normalizedKey);
        return Path.GetFullPath(filePath);
    }

    public void CacheTemplate(string key, Func<ITemplatePage> compiledTemplateFactory, IChangeToken expirationToken)
    {
        var normalizedKey = NormalizeKey(key);
        var filePath = GetFilePathForKey(key);

        _cache[normalizedKey] = compiledTemplateFactory;
        _keyToFilePath[normalizedKey] = filePath;

        _logger.LogDebug("Template cached: {TemplateKey} -> {FilePath}", normalizedKey, filePath);
    }

    public TemplateCacheLookupResult RetrieveTemplate(string key)
    {
        var normalizedKey = NormalizeKey(key);

        if (_cache.TryGetValue(normalizedKey, out var templateFactory))
        {
            _logger.LogDebug("Template cache hit: {TemplateKey}", normalizedKey);
            var item = new TemplateCacheItem(normalizedKey, templateFactory);
            return new TemplateCacheLookupResult(item);
        }

        _logger.LogDebug("Template cache miss: {TemplateKey}", normalizedKey);
        // Return empty result to indicate miss
        return new TemplateCacheLookupResult(default);
    }

    public bool Contains(string key)
    {
        var normalizedKey = NormalizeKey(key);
        return _cache.ContainsKey(normalizedKey);
    }

    public void Remove(string key)
    {
        var normalizedKey = NormalizeKey(key);
        if (_cache.TryRemove(normalizedKey, out _))
        {
            _keyToFilePath.TryRemove(normalizedKey, out _);
            _logger.LogDebug("Template removed from cache: {TemplateKey}", normalizedKey);
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _cache.Clear();
        _keyToFilePath.Clear();
    }
}
