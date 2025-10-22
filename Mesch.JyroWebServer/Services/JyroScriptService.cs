using Mesch.Jyro;
using Mesch.JyroWebServer.JyroHostFunctions;
using Microsoft.Extensions.Options;

namespace Mesch.JyroWebServer.Services;

public class JyroScriptService : IJyroScriptService
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<JyroScriptService> _logger;
    private readonly JyroExecutionOptions _executionOptions;
    private readonly string _scriptsDirectory;
    private readonly JyroScriptCacheService _cacheService;

    public JyroScriptService(
        ILoggerFactory loggerFactory,
        ILogger<JyroScriptService> logger,
        IOptions<JyroExecutionOptions> executionOptions,
        IWebHostEnvironment environment,
        JyroScriptCacheService cacheService)
    {
        _loggerFactory = loggerFactory;
        _logger = logger;
        _executionOptions = executionOptions.Value;
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

    public async Task<JyroExecutionResult> ExecuteScriptAsync(
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

            LinkedProgram? compiledProgram;

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
                var linkingResult = await Task.Run(() =>
                {
                    return JyroBuilder
                        .Create(_loggerFactory)
                        .WithScript(scriptContent)
                        .WithStandardLibrary()
                        .WithRestApi()
                        // Todo management functions
                        .WithFunction(new TodoFunctions.GetAllTodosFunction())
                        .WithFunction(new TodoFunctions.GetTodoFunction())
                        .WithFunction(new TodoFunctions.CreateTodoFunction())
                        .WithFunction(new TodoFunctions.CompleteTodoFunction())
                        .WithFunction(new TodoFunctions.UncompleteTodoFunction())
                        .WithFunction(new TodoFunctions.DeleteTodoFunction())
                        .Compile();
                }, cancellationToken);

                // Check compilation result
                if (!linkingResult.IsSuccessful || linkingResult.Program == null)
                {
                    _logger.LogError("Script compilation failed: {ScriptPath}", scriptPath);

                    // Return a result with compilation errors
                    return new JyroExecutionResult(
                        false,
                        JyroNull.Instance,
                        linkingResult.Messages,
                        new ExecutionMetadata(TimeSpan.Zero, 0, 0, 0, 0, DateTimeOffset.UtcNow));
                }

                compiledProgram = linkingResult.Program;

                // Cache the compiled program for future executions
                _cacheService.CacheProgram(scriptPath, compiledProgram);
            }

            // Execute the compiled program with the provided data
            var result = await Task.Run(() =>
            {
                return JyroBuilder
                    .Create(_loggerFactory)
                    .WithCompiledProgram(compiledProgram!)
                    .WithData(inputData)
                    .WithOptions(_executionOptions)
                    .Execute(cancellationToken);
            }, cancellationToken);

            if (result.IsSuccessful)
            {
                _logger.LogInformation(
                    "Script executed successfully in {ExecutionTime}ms: {ScriptPath}",
                    result.Metadata.ProcessingTime.TotalMilliseconds,
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
                    _logger.LogError(
                        "Jyro Error [{Code}] at {Line}:{Column}",
                        message.Code,
                        message.LineNumber,
                        message.ColumnPosition);
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

    public async Task<JyroExecutionResult> ExecuteScriptByNameAsync(
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

    private static JyroExecutionResult CreateErrorResult(string errorMessage)
    {
        // Create a basic error result with proper Message constructor
        return new JyroExecutionResult(
            false,
            JyroNull.Instance,
            [
                new Message(
                    MessageCode.RuntimeError,
                    0,
                    0,
                    MessageSeverity.Error,
                    ProcessingStage.Execution,
                    errorMessage)
            ],
            new ExecutionMetadata(TimeSpan.Zero, 0, 0, 0, 0, DateTimeOffset.UtcNow));
    }
}
