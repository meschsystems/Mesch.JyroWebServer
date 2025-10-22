using Mesch.Jyro;

namespace Mesch.JyroWebServer.Services;

public interface IJyroScriptService
{
    /// <summary>
    /// Executes a Jyro script from a file path with the provided input data.
    /// </summary>
    /// <param name="scriptPath">Absolute path to the .jyro script file</param>
    /// <param name="inputData">Input data object to be made available as 'Data' in the script</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Execution result containing output data, success status, and any error messages</returns>
    Task<JyroExecutionResult> ExecuteScriptAsync(
        string scriptPath,
        JyroValue inputData,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a Jyro script by name (without .jyro extension) from the configured scripts directory.
    /// </summary>
    /// <param name="scriptName">Name of the script file (without .jyro extension)</param>
    /// <param name="inputData">Input data object to be made available as 'Data' in the script</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Execution result containing output data, success status, and any error messages</returns>
    Task<JyroExecutionResult> ExecuteScriptByNameAsync(
        string scriptName,
        JyroValue inputData,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if a script exists by name.
    /// </summary>
    /// <param name="scriptName">Name of the script file (without .jyro extension)</param>
    /// <returns>True if the script file exists, otherwise false</returns>
    bool ScriptExists(string scriptName);

    /// <summary>
    /// Gets the full path for a script by name.
    /// </summary>
    /// <param name="scriptName">Name of the script file (without .jyro extension)</param>
    /// <returns>Full path to the script file</returns>
    string GetScriptPath(string scriptName);
}
