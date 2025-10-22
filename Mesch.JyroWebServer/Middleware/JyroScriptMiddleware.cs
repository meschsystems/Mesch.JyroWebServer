using System.Text.Json;
using Mesch.Jyro;
using Mesch.JyroWebServer.Services;

namespace Mesch.JyroWebServer.Middleware;

/// <summary>
/// Middleware that intercepts API requests and executes corresponding Jyro scripts.
///
/// Script Lookup (method-specific only, no fallback):
/// - GET /api/v1/foo → Scripts/v1/GET_foo.jyro
/// - POST /api/v1/bookings/car/approve → Scripts/v1/bookings/car/POST_approve.jyro
///
/// If no matching method-specific script exists, the request passes through to controllers (which return 404 if no route matches).
///
/// Input properties available to scripts (all guaranteed to exist):
/// - Data.request.method: HTTP method (GET, POST, etc.)
/// - Data.request.path: Request path
/// - Data.request.queryString: Raw query string
/// - Data.request.query: Query parameters as object (empty object if none)
/// - Data.request.body: Request body parsed as JSON (empty object if none)
/// - Data.request.headers: Request headers, normalized to lowercase (empty object if none)
///   Example: Access "clientNumber" header as Data.request.headers.clientnumber
/// - Data.request.route: Route parameters (empty object if none)
/// - Data.request.remoteIp: Client IP address (for IP whitelisting/blacklisting)
/// - Data.request.remotePort: Client port number
/// - Data.request.localIp: Server IP address
/// - Data.request.localPort: Server port number
/// - Data.request.protocol: HTTP protocol version (e.g., "HTTP/1.1", "HTTP/2")
/// - Data.request.scheme: URI scheme (e.g., "http", "https")
/// - Data.request.host: Host header value (e.g., "example.com:443")
///
/// Special control properties:
/// - Data._payload: If set, only this property's value will be returned to the client (otherwise entire Data object).
/// - Data._statusCode: If set to a number, that HTTP status code will be returned (default: 200).
/// - Data._redirect: If set to a string URL, performs an HTTP redirect to that URL (supports 3xx status codes).
///   Example: Data._redirect = "/dynamic/success?id=123" with Data._statusCode = 302
/// </summary>
public class JyroScriptMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<JyroScriptMiddleware> _logger;
    private readonly string _apiPrefix;

    private readonly JsonSerializerOptions _jsonSerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public JyroScriptMiddleware(
        RequestDelegate next,
        ILogger<JyroScriptMiddleware> logger,
        IConfiguration configuration)
    {
        _next = next;
        _logger = logger;
        _apiPrefix = configuration["Jyro:ApiPrefix"] ?? "/api";
    }

    public async Task InvokeAsync(HttpContext context, IJyroScriptService jyroService)
    {
        // Only process requests that match the API prefix
        if (!context.Request.Path.StartsWithSegments(_apiPrefix))
        {
            await _next(context);
            return;
        }

        // Extract the resource path from the URL
        // For /api/v1/bookings/car/approve, pathAfterPrefix = "bookings/car/approve"
        var pathAfterPrefix = context.Request.Path.Value!
            .Substring(_apiPrefix.Length)
            .TrimStart('/');

        if (string.IsNullOrEmpty(pathAfterPrefix))
        {
            await _next(context);
            return;
        }

        var httpMethod = context.Request.Method.ToUpperInvariant();

        // Build method-specific script path (no fallback for predictable behavior)
        // For /api/v1/bookings/car/approve with POST:
        // - Looks for: Scripts/bookings/car/POST_approve.jyro
        var scriptDirectory = Path.GetDirectoryName(pathAfterPrefix) ?? "";
        var scriptFileName = Path.GetFileName(pathAfterPrefix);

        var scriptName = string.IsNullOrEmpty(scriptDirectory)
            ? $"{httpMethod}_{scriptFileName}"
            : Path.Combine(scriptDirectory, $"{httpMethod}_{scriptFileName}");

        // Check if the method-specific script exists
        if (!jyroService.ScriptExists(scriptName))
        {
            // No script found, continue to next middleware (controller routing)
            await _next(context);
            return;
        }

        _logger.LogInformation(
            "Jyro script found for endpoint: {Endpoint} -> {ScriptName}",
            context.Request.Path,
            scriptName);

        try
        {
            // Build input data from the HTTP request
            var inputData = await BuildInputDataAsync(context);

            // Execute the Jyro script
            var result = await jyroService.ExecuteScriptByNameAsync(
                scriptName,
                inputData,
                context.RequestAborted);

            // Handle the result
            await HandleResultAsync(context, result, scriptName);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Request cancelled for script: {ScriptName}", scriptName);
            context.Response.StatusCode = StatusCodes.Status499ClientClosedRequest;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing Jyro script: {ScriptName}", scriptName);
            await WriteErrorResponseAsync(
                context,
                StatusCodes.Status500InternalServerError,
                "Internal server error",
                $"Failed to execute script: {ex.Message}");
        }
    }

    private async Task<JyroValue> BuildInputDataAsync(HttpContext context)
    {
        var inputObject = new JyroObject();
        var requestObject = new JyroObject();

        // Always add basic request metadata
        requestObject.SetProperty("method", new JyroString(context.Request.Method));
        requestObject.SetProperty("path", new JyroString(context.Request.Path));
        requestObject.SetProperty("queryString", new JyroString(context.Request.QueryString.ToString()));

        // Add connection information (IP address, protocol, etc.)
        requestObject.SetProperty("remoteIp", new JyroString(context.Connection.RemoteIpAddress?.ToString() ?? ""));
        requestObject.SetProperty("remotePort", new JyroNumber(context.Connection.RemotePort));
        requestObject.SetProperty("localIp", new JyroString(context.Connection.LocalIpAddress?.ToString() ?? ""));
        requestObject.SetProperty("localPort", new JyroNumber(context.Connection.LocalPort));
        requestObject.SetProperty("protocol", new JyroString(context.Request.Protocol));
        requestObject.SetProperty("scheme", new JyroString(context.Request.Scheme));
        requestObject.SetProperty("host", new JyroString(context.Request.Host.ToString()));

        // Always add query parameters (empty object if none)
        var queryParams = new JyroObject();
        foreach (var param in context.Request.Query)
        {
            // If multiple values, create an array; otherwise a single string
            if (param.Value.Count > 1)
            {
                var arr = new JyroArray();
                foreach (var value in param.Value)
                {
                    arr.Add(new JyroString(value ?? ""));
                }
                queryParams.SetProperty(param.Key, arr);
            }
            else
            {
                queryParams.SetProperty(param.Key, new JyroString(param.Value.ToString()));
            }
        }
        requestObject.SetProperty("query", queryParams);

        // Always add route parameters (empty object if none)
        var routeParams = new JyroObject();
        foreach (var param in context.Request.RouteValues)
        {
            routeParams.SetProperty(
                param.Key,
                new JyroString(param.Value?.ToString() ?? ""));
        }
        requestObject.SetProperty("route", routeParams);

        // Always add headers (empty object if none)
        // Note: Header names are normalized to lowercase for predictable access in scripts
        // e.g., "Content-Type" or "clientNumber" both become accessible as lowercase
        var headers = new JyroObject();
        foreach (var header in context.Request.Headers)
        {
            // Normalize header key to lowercase for consistent access
            var normalizedKey = header.Key.ToLowerInvariant();
            headers.SetProperty(normalizedKey, new JyroString(header.Value.ToString()));
        }
        requestObject.SetProperty("headers", headers);

        // Always add body (empty object if none or failed to parse)
        JyroValue bodyData = new JyroObject();
        if (context.Request.ContentLength > 0)
        {
            try
            {
                context.Request.EnableBuffering();

                // Parse JSON bodies
                if (context.Request.ContentType?.Contains("application/json") == true)
                {
                    var bodyJson = await new StreamReader(context.Request.Body).ReadToEndAsync();
                    context.Request.Body.Position = 0;

                    if (!string.IsNullOrWhiteSpace(bodyJson))
                    {
                        bodyData = JyroValue.FromJson(bodyJson);
                    }
                }
                // Parse form-urlencoded bodies
                else if (context.Request.ContentType?.Contains("application/x-www-form-urlencoded") == true)
                {
                    var form = await context.Request.ReadFormAsync();
                    var formObject = new JyroObject();

                    foreach (var field in form)
                    {
                        formObject.SetProperty(field.Key, new JyroString(field.Value.ToString()));
                    }

                    bodyData = formObject;
                }
                // Parse multipart/form-data bodies
                else if (context.Request.ContentType?.Contains("multipart/form-data") == true)
                {
                    var form = await context.Request.ReadFormAsync();
                    var formObject = new JyroObject();

                    foreach (var field in form)
                    {
                        formObject.SetProperty(field.Key, new JyroString(field.Value.ToString()));
                    }

                    bodyData = formObject;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse request body, using empty object");
            }
        }
        requestObject.SetProperty("body", bodyData);

        // Set the request object as a property of Data
        inputObject.SetProperty("request", requestObject);

        return inputObject;
    }

    private async Task HandleResultAsync(
        HttpContext context,
        JyroExecutionResult result,
        string scriptName)
    {
        if (!result.IsSuccessful)
        {
            var errorMessages = result.Messages
                .Where(m => m.Severity == MessageSeverity.Error)
                .Select(m => new
                {
                    code = m.Code.ToString(),
                    line = m.LineNumber,
                    column = m.ColumnPosition,
                    arguments = m.Arguments
                })
                .ToList();

            _logger.LogError(
                "Jyro script execution failed: {ScriptName}. Error count: {ErrorCount}",
                scriptName,
                errorMessages.Count);

            await WriteErrorResponseAsync(
                context,
                StatusCodes.Status500InternalServerError,
                "Script execution failed",
                errorMessages);
            return;
        }

        // Success - check for special control properties
        // Note: Jyro identifiers must start with a letter (A-Z, a-z), so we use _payload not _payload
        int statusCode = StatusCodes.Status200OK;
        JyroValue responseData = result.Data;

        // If the result is an object, check for special control properties
        if (result.Data is JyroObject dataObject)
        {
            // Check for _redirect property to redirect to another page
            var redirectProperty = dataObject.GetProperty("_redirect");
            if (!redirectProperty.IsNull && redirectProperty is JyroString redirectString)
            {
                var redirectUrl = redirectString.Value;
                _logger.LogInformation(
                    "Redirecting from script {ScriptName} to: {RedirectUrl}",
                    scriptName,
                    redirectUrl);

                // Use status code if provided, otherwise default to 302 (temporary redirect)
                var redirectStatusCode = StatusCodes.Status302Found;
                var statusCodeProperty = dataObject.GetProperty("_statusCode");
                if (!statusCodeProperty.IsNull && statusCodeProperty is JyroNumber statusCodeNumber)
                {
                    var statusCodeValue = (int)statusCodeNumber.Value;
                    if (statusCodeValue >= 300 && statusCodeValue <= 399)
                    {
                        redirectStatusCode = statusCodeValue;
                    }
                }

                context.Response.StatusCode = redirectStatusCode;
                context.Response.Headers.Location = redirectUrl;
                return;
            }

            // Check for _statusCode property to control HTTP status code
            var statusCodeProperty2 = dataObject.GetProperty("_statusCode");
            if (!statusCodeProperty2.IsNull && statusCodeProperty2 is JyroNumber statusCodeNumber2)
            {
                var statusCodeValue = (int)statusCodeNumber2.Value;
                // Validate status code is in valid range (100-599)
                if (statusCodeValue >= 100 && statusCodeValue <= 599)
                {
                    statusCode = statusCodeValue;
                    _logger.LogInformation(
                        "Using custom status code {StatusCode} for script: {ScriptName}",
                        statusCode,
                        scriptName);
                }
                else
                {
                    _logger.LogWarning(
                        "Invalid status code {StatusCode} in script {ScriptName}, using 200",
                        statusCodeValue,
                        scriptName);
                }
            }

            // Check for _payload property to control response body
            var payloadProperty = dataObject.GetProperty("_payload");
            if (!payloadProperty.IsNull)
            {
                responseData = payloadProperty;
                _logger.LogInformation(
                    "Using _payload property for response in script: {ScriptName}",
                    scriptName);
            }
        }

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";

        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        var outputJson = responseData.ToJson(jsonOptions);
        await context.Response.WriteAsync(outputJson);

        _logger.LogInformation(
            "Jyro script executed successfully: {ScriptName} in {ExecutionTime}ms",
            scriptName,
            result.Metadata.ProcessingTime.TotalMilliseconds);
    }

    private async Task WriteErrorResponseAsync(
        HttpContext context,
        int statusCode,
        string error,
        object? details = null)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";

        var errorResponse = new
        {
            success = false,
            error,
            details
        };

        var json = JsonSerializer.Serialize(errorResponse, _jsonSerializerOptions);

        await context.Response.WriteAsync(json);
    }
}
