using System.Text.Json;
using Mesch.Jyro;
using Mesch.JyroWebServer.Services;
using RazorLight;

namespace Mesch.JyroWebServer.Middleware;

/// <summary>
/// Middleware that intercepts requests to /dynamic, executes corresponding Jyro scripts,
/// and renders Razor templates as HTML pages.
///
/// Template Lookup:
/// For endpoint /dynamic/bookings/car/approval:
/// 1. Looks for Scripts/bookings/car/GET_approval.jyro (or POST_, etc.)
/// 2. Falls back to Scripts/bookings/car/approval.jyro if method-specific not found
/// 3. Executes the script and captures Data._payload
/// 4. Renders Templates/bookings/car/approval.cshtml with Data._payload as the model
///
/// If no template exists, returns 404.
/// The Razor template receives Data._payload as its model (converted to Dictionary).
/// </summary>
public class RazorTemplateMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<RazorTemplateMiddleware> _logger;
    private readonly string _dynamicPrefix;
    private readonly string _templatesPath;
    private readonly string _scriptsPath;
    private readonly RazorLightEngine _razorEngine;

    private readonly JsonSerializerOptions _jsonSerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public RazorTemplateMiddleware(
        RequestDelegate next,
        ILogger<RazorTemplateMiddleware> logger,
        IConfiguration configuration,
        IWebHostEnvironment environment,
        ILoggerFactory loggerFactory)
    {
        _next = next;
        _logger = logger;
        _dynamicPrefix = configuration["Jyro:DynamicPrefix"] ?? "/dynamic";
        _templatesPath = Path.Combine(environment.ContentRootPath, "Templates");
        _scriptsPath = configuration["Jyro:ScriptsDirectory"]
            ?? Path.Combine(environment.ContentRootPath, "Scripts");

        // Initialize RazorLight engine with file system project
        // DisableEncoding() allows templates to be recompiled on each request for hot-reload
        // Using default caching - templates are re-read/compiled when changed
        _razorEngine = new RazorLightEngineBuilder()
            .UseFileSystemProject(_templatesPath)
            .DisableEncoding()
            .Build();
    }

    public async Task InvokeAsync(HttpContext context, IJyroScriptService jyroService)
    {
        // Only process requests that match the /dynamic prefix
        if (!context.Request.Path.StartsWithSegments(_dynamicPrefix))
        {
            await _next(context);
            return;
        }

        // Extract the page path after /dynamic prefix
        // Example: /dynamic/bookings/car/approval -> bookings/car/approval
        var pathAfterPrefix = context.Request.Path.Value!
            .Substring(_dynamicPrefix.Length)
            .TrimStart('/');

        if (string.IsNullOrEmpty(pathAfterPrefix))
        {
            await _next(context);
            return;
        }

        var httpMethod = context.Request.Method.ToUpperInvariant();

        // Try automatic index resolution for directory paths
        // /dynamic/todo → /dynamic/todo/index
        var templateRelativePath = $"{pathAfterPrefix}.cshtml";
        var templateFullPath = Path.Combine(_templatesPath, templateRelativePath);

        if (!File.Exists(templateFullPath))
        {
            // Try with /index appended
            var indexPath = Path.Combine(pathAfterPrefix, "index");
            var indexTemplatePath = Path.Combine(_templatesPath, $"{indexPath}.cshtml");

            if (File.Exists(indexTemplatePath))
            {
                // Redirect to /index for clean URLs
                context.Response.Redirect($"{context.Request.Path}/index{context.Request.QueryString}", permanent: false);
                return;
            }
        }

        // For POST requests without templates, check if script exists and execute it
        // This allows form submissions that only need to execute logic and redirect
        if (!File.Exists(templateFullPath) && httpMethod != "GET")
        {
            var postScriptDirectory = Path.GetDirectoryName(pathAfterPrefix) ?? "";
            var postScriptFileName = Path.GetFileName(pathAfterPrefix);
            var postMethodSpecificScriptPath = Path.Combine(postScriptDirectory, $"{httpMethod}_{postScriptFileName}");

            if (ScriptExistsOnDisk(postMethodSpecificScriptPath))
            {
                // Execute the script without rendering a template
                var inputData = await BuildInputDataAsync(context);
                var scriptFullPath = Path.Combine(_scriptsPath, $"{postMethodSpecificScriptPath}.jyro");

                try
                {
                    var result = await jyroService.ExecuteScriptAsync(
                        scriptFullPath,
                        inputData,
                        context.RequestAborted);

                    if (!result.IsSuccessful)
                    {
                        var errorMessages = result.Messages
                            .Where(m => m.Severity == MessageSeverity.Error)
                            .Select(m => m.Code.ToString())
                            .ToList();

                        await WriteErrorResponseAsync(
                            context,
                            StatusCodes.Status500InternalServerError,
                            "Script execution failed",
                            errorMessages);
                        return;
                    }

                    // Check for redirect
                    if (result.Data is JyroObject dataObject)
                    {
                        var redirectProperty = dataObject.GetProperty("_redirect");
                        if (!redirectProperty.IsNull && redirectProperty is JyroString redirectString)
                        {
                            var statusCodeProperty = dataObject.GetProperty("_statusCode");
                            var statusCode = StatusCodes.Status302Found;

                            if (!statusCodeProperty.IsNull && statusCodeProperty is JyroNumber statusCodeNumber)
                            {
                                var statusCodeValue = (int)statusCodeNumber.Value;
                                if (statusCodeValue >= 300 && statusCodeValue <= 399)
                                {
                                    statusCode = statusCodeValue;
                                }
                            }

                            context.Response.StatusCode = statusCode;
                            context.Response.Headers.Location = redirectString.Value;
                            return;
                        }
                    }

                    // No redirect, return JSON
                    context.Response.StatusCode = StatusCodes.Status200OK;
                    context.Response.ContentType = "application/json";
                    var jsonOptions = new System.Text.Json.JsonSerializerOptions
                    {
                        WriteIndented = true,
                        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
                    };
                    var outputJson = result.Data.ToJson(jsonOptions);
                    await context.Response.WriteAsync(outputJson);
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error executing script: {ScriptPath}", postMethodSpecificScriptPath);
                    await WriteErrorResponseAsync(
                        context,
                        StatusCodes.Status500InternalServerError,
                        "Script execution failed",
                        ex.Message);
                    return;
                }
            }
        }

        // No template found and not a POST-only endpoint
        if (!File.Exists(templateFullPath))
        {
            _logger.LogWarning(
                "Dynamic page not found: {Path} (template does not exist: {TemplatePath})",
                context.Request.Path,
                templateFullPath);

            context.Response.StatusCode = StatusCodes.Status404NotFound;
            context.Response.ContentType = "text/html";
            await context.Response.WriteAsync($@"<!DOCTYPE html>
<html>
<head>
    <title>404 - Page Not Found</title>
    <style>
        body {{
            font-family: Arial, sans-serif;
            display: flex;
            justify-content: center;
            align-items: center;
            min-height: 100vh;
            margin: 0;
            background: #f5f5f5;
        }}
        .error-container {{
            text-align: center;
            background: white;
            padding: 40px;
            border-radius: 8px;
            box-shadow: 0 2px 8px rgba(0,0,0,0.1);
        }}
        h1 {{
            color: #f44336;
            font-size: 72px;
            margin: 0;
        }}
        h2 {{
            color: #333;
            margin: 10px 0;
        }}
        p {{
            color: #666;
        }}
        .path {{
            font-family: monospace;
            background: #f5f5f5;
            padding: 5px 10px;
            border-radius: 4px;
        }}
    </style>
</head>
<body>
    <div class=""error-container"">
        <h1>404</h1>
        <h2>Dynamic Page Not Found</h2>
        <p>The requested page does not exist:</p>
        <p class=""path"">{context.Request.Path}</p>
    </div>
</body>
</html>");
            return;
        }

        // Build script paths for method-specific and generic scripts
        // Example: Scripts/bookings/car/GET_approval.jyro or Scripts/bookings/car/approval.jyro
        var scriptDirectory = Path.GetDirectoryName(pathAfterPrefix) ?? "";
        var scriptFileName = Path.GetFileName(pathAfterPrefix);

        var methodSpecificScriptPath = Path.Combine(scriptDirectory, $"{httpMethod}_{scriptFileName}");
        var genericScriptPath = pathAfterPrefix;

        // Try method-specific script first, then generic
        string scriptPath;
        if (ScriptExistsOnDisk(methodSpecificScriptPath))
        {
            scriptPath = methodSpecificScriptPath;
        }
        else if (ScriptExistsOnDisk(genericScriptPath))
        {
            scriptPath = genericScriptPath;
        }
        else
        {
            // No script found - return error
            _logger.LogError(
                "No Jyro script found for dynamic page: {Path}. Expected: {MethodScript} or {GenericScript}",
                context.Request.Path,
                methodSpecificScriptPath,
                genericScriptPath);
            await WriteErrorResponseAsync(
                context,
                StatusCodes.Status500InternalServerError,
                "Configuration error",
                $"No Jyro script found for dynamic page. Expected script at: Scripts/{genericScriptPath}.jyro");
            return;
        }

        _logger.LogInformation(
            "Dynamic page found: {Endpoint} -> Template: {TemplatePath}, Script: {ScriptPath}",
            context.Request.Path,
            templateRelativePath,
            scriptPath);

        try
        {
            // Build input data from the HTTP request
            var inputData = await BuildInputDataAsync(context);

            // Execute the Jyro script using full path
            var scriptFullPath = Path.Combine(_scriptsPath, $"{scriptPath}.jyro");
            var result = await jyroService.ExecuteScriptAsync(
                scriptFullPath,
                inputData,
                context.RequestAborted);

            // Handle the result - render the template
            await HandleResultAsync(context, result, templateRelativePath, scriptPath);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Request cancelled for dynamic page: {ScriptPath}", scriptPath);
            context.Response.StatusCode = StatusCodes.Status499ClientClosedRequest;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing Jyro script or rendering template: {ScriptPath}", scriptPath);
            await WriteErrorResponseAsync(
                context,
                StatusCodes.Status500InternalServerError,
                "Internal server error",
                $"Failed to execute script or render template: {ex.Message}");
        }
    }

    private bool ScriptExistsOnDisk(string relativePath)
    {
        var fullPath = Path.Combine(_scriptsPath, $"{relativePath}.jyro");
        return File.Exists(fullPath);
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
        var headers = new JyroObject();
        foreach (var header in context.Request.Headers)
        {
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
        string templateRelativePath,
        string scriptPath)
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
                "Jyro script execution failed: {ScriptPath}. Error count: {ErrorCount}",
                scriptPath,
                errorMessages.Count);

            await WriteErrorResponseAsync(
                context,
                StatusCodes.Status500InternalServerError,
                "Script execution failed",
                errorMessages);
            return;
        }

        // Extract status code and payload
        int statusCode = StatusCodes.Status200OK;
        JyroValue responseData = result.Data;

        if (result.Data is JyroObject dataObject)
        {
            // Check for _statusCode property
            var statusCodeProperty = dataObject.GetProperty("_statusCode");
            if (!statusCodeProperty.IsNull && statusCodeProperty is JyroNumber statusCodeNumber)
            {
                var statusCodeValue = (int)statusCodeNumber.Value;
                if (statusCodeValue >= 100 && statusCodeValue <= 599)
                {
                    statusCode = statusCodeValue;
                    _logger.LogInformation(
                        "Using custom status code {StatusCode} for dynamic page: {ScriptPath}",
                        statusCode,
                        scriptPath);
                }
            }

            // Check for _payload property
            var payloadProperty = dataObject.GetProperty("_payload");
            if (!payloadProperty.IsNull)
            {
                responseData = payloadProperty;
                _logger.LogInformation(
                    "Using _payload property for response in script: {ScriptPath}",
                    scriptPath);
            }
        }

        // Render the Razor template (we already verified it exists)
        try
        {
            // Convert JyroValue to a dynamic object for Razor template
            var model = ConvertJyroValueToDynamic(responseData);

            // Render the Razor template using relative path
            var html = await _razorEngine.CompileRenderAsync(templateRelativePath, model);

            context.Response.StatusCode = statusCode;
            context.Response.ContentType = "text/html";
            await context.Response.WriteAsync(html);

            _logger.LogInformation(
                "Dynamic page rendered successfully: {TemplatePath} in {ExecutionTime}ms",
                templateRelativePath,
                result.Metadata.ProcessingTime.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to render Razor template: {TemplatePath}", templateRelativePath);
            await WriteErrorResponseAsync(
                context,
                StatusCodes.Status500InternalServerError,
                "Template rendering failed",
                ex.Message);
        }
    }

    private static object? ConvertJyroValueToDynamic(JyroValue value)
    {
        // Convert JyroValue to a native C# object that Razor can use
        if (value.IsNull)
        {
            return null;
        }

        if (value is JyroString str)
        {
            return str.Value;
        }

        if (value is JyroNumber num)
        {
            return num.Value;
        }

        if (value is JyroBoolean boolean)
        {
            return boolean.Value;
        }

        if (value is JyroArray arr)
        {
            var list = new List<object?>();
            foreach (var item in arr)
            {
                list.Add(ConvertJyroValueToDynamic(item));
            }
            return list;
        }

        if (value is JyroObject obj)
        {
            var dict = new Dictionary<string, object?>();
            foreach (var prop in obj)
            {
                dict[prop.Key] = ConvertJyroValueToDynamic(prop.Value);
            }
            return dict;
        }

        return null;
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
