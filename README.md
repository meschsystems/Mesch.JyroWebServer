# Jyro Web Server

Technology demonstration project showcasing a 100% Jyro script-driven request handling architecture. ASP.NET is used as the web-host platform.

REST endpoint responses are handled by [Jyro](https://www.jyro.dev/) scripts that return JSON objects with these responses then interpreted by a Razor web page. Together, these two technologies can provide a complete REST API/Web UI frontend that is fully configurable and hot-reloadable.

This is not intended for production use, but does show how Jyro can be integrated into host applications in various ways.

This project showcases:
- **Zero controllers** - all endpoints handled by Jyro scripts
- **Dynamic HTML pages** at `/dynamic/*` with Razor templates
- **JSON API** - the same scripts are also accessible at `/api/*` returning JSON
- **Hot-reload** - edit scripts/templates without restarting
- **Host functions** - Custom C# functions (`TodoFunctions`) callable from Jyro scripts
- **Separation of concerns** - Scripts handle logic, Razor handles presentation

## Quick Start

1. **Run the application:**
   ```bash
   dotnet run
   ```

2. **Access the UI:**
   - Todo List UI: `https://localhost:5001/dynamic/todo/index`

3. **Try the JSON API:**
   ```bash
   # Get all todos (returns JSON from the same script that powers the UI)
   curl https://localhost:5001/api/todo/index
   ```

## Project Structure

```
Mesch.JyroWebServer/
├── JyroHostFunctions/
│   └── TodoFunctions.cs          # C# host functions for todo CRUD
│
├── Middleware/
│   ├── JyroScriptMiddleware.cs   # Handles /api/* → JSON responses
│   └── RazorTemplateMiddleware.cs # Handles /dynamic/* → HTML pages
│
├── Scripts/
│   └── todo/                      # Jyro scripts (/dynamic/todo/* and /api/todo/*)
│       ├── GET_index.jyro         # Main todo list data
│       ├── POST_create.jyro       # Create todo (form submission)
│       ├── POST_toggle.jyro       # Toggle completion (form submission)
│       └── POST_delete.jyro       # Delete todo (form submission)
│
├── Services/
│   ├── JyroScriptService.cs      # Script compilation and execution
│   └── JyroScriptCacheService.cs # Hot-reload via FileSystemWatcher
│
└── Templates/
    └── todo/
        └── index.cshtml           # Main UI template (Razor)
```

## How It Works

### 1. Host Functions (C#)
Host functions in `JyroHostFunctions/TodoFunctions.cs` provide core functionality:
- `GetAllTodos()` - Returns all todos
- `GetTodo(id)` - Gets a single todo
- `CreateTodo(title)` - Creates a new todo
- `CompleteTodo(id)` - Marks todo as complete
- `UncompleteTodo(id)` - Marks todo as incomplete
- `DeleteTodo(id)` - Deletes a todo

### 2. Jyro Scripts
Scripts in `Scripts/` handle HTTP requests and call host functions.

The middleware looks for specific custom keys in the Jyro Data context:

- `Data._payload`: If set, only this property's value will be returned to the client (otherwise entire Data object).
- `Data._statusCode`: If set to a number, that HTTP status code will be returned (default: 200).
- `Data._redirect`: If set to a string URL, performs an HTTP redirect to that URL (supports 3xx status codes).

Middleware could be extended to detect any number of custom keys for custom functionality.

**Example - GET /dynamic/todo/index** (`Scripts/todo/GET_index.jyro`):
```jyro
# Get all todos
Data.todos = GetAllTodos()

# Calculate statistics
Data.totalCount = 0
Data.completedCount = 0
Data.pendingCount = 0

foreach todo in Data.todos do
    Data.totalCount++
    if todo.isComplete == true then
        Data.completedCount++
    else
        Data.pendingCount++
    end
end

Data._payload = {
    "todos": Data.todos,
    "stats": {
        "total": Data.totalCount,
        "completed": Data.completedCount,
        "pending": Data.pendingCount
    }
}
```

### 3. Dynamic Pages
Scripts in `Scripts/todo/` prepare data and redirect:

**Example - Form Handler** (`Scripts/todo/POST_create.jyro`):
```jyro
# Handle form submission
Data.title = Data.request.body.title

if Data.title == null or Data.title == "" then
    Data._redirect = "/dynamic/todo/index?error=Title is required"
    Data._statusCode = 302
    return
end

# Create todo via host function
Data.newTodo = CreateTodo(Data.title)

# Redirect back to list
Data._redirect = "/dynamic/todo/index?success=Todo created"
Data._statusCode = 302
```

### 4. Razor Templates
Templates in `Templates/` render HTML using data from scripts:

```cshtml
@inherits RazorLight.TemplatePage<dynamic>

<h1>Todo List</h1>
<p>Total: @Model["stats"]["total"]</p>

@foreach (var todo in Model["todos"])
{
    <div>@todo["title"]</div>
}
```

## Features

### Endpoints
- `GET /dynamic/todo/index` - Todo list UI (HTML)
- `GET /api/todo/index` - Todo list data (JSON)
- `POST /dynamic/todo/create` - Create todo (form submission, redirects)
- `POST /dynamic/todo/toggle` - Toggle completion (form submission, redirects)
- `POST /dynamic/todo/delete` - Delete todo (form submission, redirects)

### Dynamic UI
- Real-time statistics (total, pending, completed)
- Add new todos
- Toggle completion status (checkbox)
- Delete todos with confirmation
- Responsive layout
- Empty state message

## Hot Reload

Edit any `.jyro` script or `.cshtml` template and **refresh** - no restart needed!

### How It Works

- **Jyro Scripts** - Script content is cached in memory, but a `FileSystemWatcher` automatically invalidates the cache when files change
- **Razor Templates** - Compiled templates are cached for performance, but a `FileSystemWatcher` automatically invalidates the cache when files change

The hot-reload system provides the best of both worlds:
- **Fast**: Scripts are read once and cached; templates are compiled once and cached
- **Responsive**: File changes trigger automatic cache invalidation via FileSystemWatcher
- **Efficient**: Only changed files are reloaded/recompiled

### Try It

1. **Modify a script:**
   - Open `Scripts/todo/GET_index.jyro`
   - Add a new stat or modify the data
   - Save the file
   - Refresh the browser - changes appear immediately!

2. **Modify a template:**
   - Open `Templates/todo/index.cshtml`
   - Change text, styles, or layout
   - Save the file (FileSystemWatcher detects the change and invalidates cache)
   - Refresh the browser - template is recompiled and changes appear!

## Architecture

This project demonstrates various integration opportunities available when using Jyro.

If this architecture works for your project, you might consider the next steps:

1. **Add authentication** - Create a `CheckAuth()` host function
2. **Add persistence** - Replace in-memory storage with database
3. **Add validation** - More complex validation in scripts
4. **Add filtering** - Filter todos by status, date, etc.
5. **Add search** - Full-text search across todos
6. **Add categories** - Organize todos into categories
7. **Multi-user** - User-specific todo lists

## Learn More

- [Jyro Homepage](https://www.jyro.dev/) 
- [Jyro Playpen](https://playpen.jyro.dev/)
- [Jyro Documentation](https://docs.mesch.cloud/jyro/)
- [Jyro GitHub Repository](https://github.com/meschsystems/Mesch.Jyro)
- [Jyro Nuget Package](https://www.nuget.org/packages/Mesch.Jyro/)
- [Jyro Visual Studio Code Syntax Highlighting](https://marketplace.visualstudio.com/items?itemName=meschsystems.jyro)

## License

Jyro and associated tooling (including this project) is copyright © 2025-2026 Mesch Systems and is released under the MIT License.