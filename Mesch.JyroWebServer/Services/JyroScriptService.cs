using Mesch.Jyro;
using Mesch.JyroWebServer.JyroHostFunctions;
using Microsoft.FSharp.Core;

namespace Mesch.JyroWebServer.Services;

public class JyroScriptService : IJyroScriptService
{
    private readonly ILogger<JyroScriptService> _logger;
    private readonly JyroExecutionOptions _executionOptions;
    private readonly string _scriptsDirectory;
    private readonly JyroScriptCacheService _cacheService;

    public JyroScriptService(
        ILogger<JyroScriptService> logger,
        JyroExecutionOptions executionOptions,
        IWebHostEnvironment environment,
        JyroScriptCacheService cacheService)
    {
        _logger = logger;
        _executionOptions = executionOptions;
        _cacheService = cacheService;

        // Scripts directory is under the API project root
        _scriptsDirectory = Path.Combine(environment.ContentRootPath, "Scripts");

        // Ensure scripts directory exists
        if (!Directory.Exists(_scriptsDirectory))
        {
            Directory.CreateDirectory(_scriptsDirectory);
            _logger.LogInformation("Created Jyro scripts directory at: {ScriptsDirectory}", _scriptsDirectory);
        }
    }

    public async Task<JyroResult<JyroValue>> ExecuteScriptAsync(
        string scriptPath,
        JyroValue inputData,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Executing Jyro script: {ScriptPath}", scriptPath);

            // Validate script file exists
            if (!File.Exists(scriptPath))
            {
                _logger.LogError("Script file not found: {ScriptPath}", scriptPath);
                return CreateErrorResult($"Script file not found: {scriptPath}");
            }

            CompiledProgram? compiledProgram;

            // Try to get cached compiled program
            if (!_cacheService.TryGetCachedProgram(scriptPath, out compiledProgram))
            {
                // Cache miss - compile the script
                var scriptContent = await File.ReadAllTextAsync(scriptPath, cancellationToken);

                if (string.IsNullOrWhiteSpace(scriptContent))
                {
                    _logger.LogError("Script file is empty: {ScriptPath}", scriptPath);
                    return CreateErrorResult($"Script file is empty: {scriptPath}");
                }

                // Compile the script once
                var compileResult = await Task.Run(() =>
                {
                    return new JyroBuilder()
                        .WithSource(scriptContent)
                        // Todo management functions
                        .AddFunction(new TodoFunctions.GetAllTodosFunction())
                        .AddFunction(new TodoFunctions.GetTodoFunction())
                        .AddFunction(new TodoFunctions.CreateTodoFunction())
                        .AddFunction(new TodoFunctions.CompleteTodoFunction())
                        .AddFunction(new TodoFunctions.UncompleteTodoFunction())
                        .AddFunction(new TodoFunctions.DeleteTodoFunction())
                        .Compile();
                }, cancellationToken);

                // Check compilation result
                if (!compileResult.IsSuccess)
                {
                    _logger.LogError("Script compilation failed: {ScriptPath}", scriptPath);
                    return JyroResult<JyroValue>.Failure<JyroValue>(compileResult.Messages);
                }

                compiledProgram = compileResult.Value.Value;

                // Cache the compiled program for future executions
                _cacheService.CacheProgram(scriptPath, compiledProgram);
            }

            // Execute the compiled program with the provided data
            var result = await Task.Run(() =>
            {
                var limiter = new JyroResourceLimiter(_executionOptions, cancellationToken);
                var ctx = new JyroExecutionContext(limiter, cancellationToken);
                return Compiler.execute(compiledProgram!, inputData, ctx);
            }, cancellationToken);

            if (result.IsSuccess)
            {
                _logger.LogInformation(
                    "Script executed successfully: {ScriptPath}",
                    scriptPath);
            }
            else
            {
                _logger.LogWarning(
                    "Script execution completed with errors: {ScriptPath}. Errors: {ErrorCount}",
                    scriptPath,
                    result.Messages.Count(m => m.Severity == MessageSeverity.Error));

                foreach (var message in result.Messages.Where(m => m.Severity == MessageSeverity.Error))
                {
                    var line = message.Location != null ? message.Location.Value.Line : 0;
                    var col = message.Location != null ? message.Location.Value.Column : 0;
                    _logger.LogError(
                        "Jyro Error [{Code}] at {Line}:{Column}",
                        message.Code,
                        line,
                        col);
                }
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Script execution was cancelled: {ScriptPath}", scriptPath);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error executing script: {ScriptPath}", scriptPath);
            return CreateErrorResult($"Unexpected error: {ex.Message}");
        }
    }

    public async Task<JyroResult<JyroValue>> ExecuteScriptByNameAsync(
        string scriptName,
        JyroValue inputData,
        CancellationToken cancellationToken = default)
    {
        var scriptPath = GetScriptPath(scriptName);
        return await ExecuteScriptAsync(scriptPath, inputData, cancellationToken);
    }

    public bool ScriptExists(string scriptName)
    {
        var scriptPath = GetScriptPath(scriptName);
        return File.Exists(scriptPath);
    }

    public string GetScriptPath(string scriptName)
    {
        // Remove .jyro extension if provided
        if (scriptName.EndsWith(".jyro", StringComparison.OrdinalIgnoreCase))
        {
            scriptName = scriptName[..^5];
        }

        // Sanitize script name to prevent directory traversal attacks
        // Allow forward slashes for nested paths, but reject any path traversal attempts
        if (scriptName.Contains("..") || scriptName.Contains('~'))
        {
            throw new ArgumentException("Invalid script name: path traversal attempts are not allowed", nameof(scriptName));
        }

        // Normalize path separators and combine with scripts directory
        scriptName = scriptName.Replace('\\', '/');
        var scriptPath = Path.Combine(_scriptsDirectory, $"{scriptName}.jyro");

        // Ensure the resulting path is still within the scripts directory (defense in depth)
        var fullScriptPath = Path.GetFullPath(scriptPath);
        var fullScriptsDirectory = Path.GetFullPath(_scriptsDirectory);
        if (!fullScriptPath.StartsWith(fullScriptsDirectory, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Invalid script name: must be within scripts directory", nameof(scriptName));
        }

        return scriptPath;
    }

    private static JyroResult<JyroValue> CreateErrorResult(string errorMessage)
    {
        return JyroResult<JyroValue>.Failure<JyroValue>(
            DiagnosticMessage.Error(
                MessageCode.RuntimeError, errorMessage,
                FSharpOption<object[]>.None, FSharpOption<SourceLocation>.None));
    }
}
