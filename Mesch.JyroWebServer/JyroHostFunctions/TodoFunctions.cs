using Mesch.Jyro;
using System.Collections.Concurrent;

namespace Mesch.JyroWebServer.JyroHostFunctions;

/// <summary>
/// In-memory todo list management functions for Jyro scripts.
/// Provides CRUD operations on todo items stored in memory.
/// </summary>
public static class TodoFunctions
{
    private static readonly ConcurrentDictionary<int, TodoItem> _todos = new();
    private static int _nextId = 1;

    private class TodoItem
    {
        public int Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public bool IsComplete { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
    }

    /// <summary>
    /// Gets all todo items.
    /// Usage: GetAllTodos()
    /// Returns: Array of todo objects
    /// </summary>
    public sealed class GetAllTodosFunction : JyroFunctionBase
    {
        public GetAllTodosFunction()
            : base(FunctionSignatures.Variadic("GetAllTodos", ParameterType.Any, ParameterType.Array, 0))
        {
        }

        public override JyroValue Execute(IReadOnlyList<JyroValue> arguments, JyroExecutionContext executionContext)
        {
            var todos = _todos.Values.OrderBy(t => t.Id).ToList();
            var result = new JyroArray();

            foreach (var todo in todos)
            {
                result.Add(TodoItemToJyroObject(todo));
            }

            return result;
        }
    }

    /// <summary>
    /// Gets a single todo item by ID.
    /// Usage: GetTodo(id)
    /// Returns: Todo object or null if not found
    /// </summary>
    public sealed class GetTodoFunction : JyroFunctionBase
    {
        public GetTodoFunction()
            : base(FunctionSignatures.Unary("GetTodo", ParameterType.Number, ParameterType.Object))
        {
        }

        public override JyroValue Execute(IReadOnlyList<JyroValue> arguments, JyroExecutionContext executionContext)
        {
            if (arguments[0] is not JyroNumber idNumber)
            {
                return JyroNull.Instance;
            }

            var id = (int)idNumber.Value;
            return _todos.TryGetValue(id, out var todo)
                ? TodoItemToJyroObject(todo)
                : JyroNull.Instance;
        }
    }

    /// <summary>
    /// Creates a new todo item.
    /// Usage: CreateTodo(title)
    /// Returns: Created todo object
    /// </summary>
    public sealed class CreateTodoFunction : JyroFunctionBase
    {
        public CreateTodoFunction()
            : base(FunctionSignatures.Unary("CreateTodo", ParameterType.String, ParameterType.Object))
        {
        }

        public override JyroValue Execute(IReadOnlyList<JyroValue> arguments, JyroExecutionContext executionContext)
        {
            if (arguments[0] is not JyroString titleString)
            {
                throw new ArgumentException("CreateTodo requires a string title argument");
            }

            var todo = new TodoItem
            {
                Id = Interlocked.Increment(ref _nextId),
                Title = titleString.Value,
                IsComplete = false,
                CreatedAt = DateTime.UtcNow
            };

            _todos[todo.Id] = todo;
            return TodoItemToJyroObject(todo);
        }
    }

    /// <summary>
    /// Marks a todo item as complete.
    /// Usage: CompleteTodo(id)
    /// Returns: Updated todo object or null if not found
    /// </summary>
    public sealed class CompleteTodoFunction : JyroFunctionBase
    {
        public CompleteTodoFunction()
            : base(FunctionSignatures.Unary("CompleteTodo", ParameterType.Number, ParameterType.Object))
        {
        }

        public override JyroValue Execute(IReadOnlyList<JyroValue> arguments, JyroExecutionContext executionContext)
        {
            if (arguments[0] is not JyroNumber idNumber)
            {
                return JyroNull.Instance;
            }

            var id = (int)idNumber.Value;
            if (_todos.TryGetValue(id, out var todo))
            {
                todo.IsComplete = true;
                todo.CompletedAt = DateTime.UtcNow;
                return TodoItemToJyroObject(todo);
            }

            return JyroNull.Instance;
        }
    }

    /// <summary>
    /// Marks a todo item as incomplete.
    /// Usage: UncompleteTodo(id)
    /// Returns: Updated todo object or null if not found
    /// </summary>
    public sealed class UncompleteTodoFunction : JyroFunctionBase
    {
        public UncompleteTodoFunction()
            : base(FunctionSignatures.Unary("UncompleteTodo", ParameterType.Number, ParameterType.Object))
        {
        }

        public override JyroValue Execute(IReadOnlyList<JyroValue> arguments, JyroExecutionContext executionContext)
        {
            if (arguments[0] is not JyroNumber idNumber)
            {
                return JyroNull.Instance;
            }

            var id = (int)idNumber.Value;
            if (_todos.TryGetValue(id, out var todo))
            {
                todo.IsComplete = false;
                todo.CompletedAt = null;
                return TodoItemToJyroObject(todo);
            }

            return JyroNull.Instance;
        }
    }

    /// <summary>
    /// Deletes a todo item.
    /// Usage: DeleteTodo(id)
    /// Returns: true if deleted, false if not found
    /// </summary>
    public sealed class DeleteTodoFunction : JyroFunctionBase
    {
        public DeleteTodoFunction()
            : base(FunctionSignatures.Unary("DeleteTodo", ParameterType.Number, ParameterType.Boolean))
        {
        }

        public override JyroValue Execute(IReadOnlyList<JyroValue> arguments, JyroExecutionContext executionContext)
        {
            if (arguments[0] is not JyroNumber idNumber)
            {
                return JyroBoolean.False;
            }

            var id = (int)idNumber.Value;
            return _todos.TryRemove(id, out _) ? JyroBoolean.True : JyroBoolean.False;
        }
    }

    private static JyroObject TodoItemToJyroObject(TodoItem todo)
    {
        var obj = new JyroObject();
        obj.SetProperty("id", new JyroNumber(todo.Id));
        obj.SetProperty("title", new JyroString(todo.Title));
        obj.SetProperty("isComplete", todo.IsComplete ? JyroBoolean.True : JyroBoolean.False);
        obj.SetProperty("createdAt", new JyroString(todo.CreatedAt.ToString("o")));
        obj.SetProperty("completedAt", todo.CompletedAt.HasValue
            ? new JyroString(todo.CompletedAt.Value.ToString("o"))
            : JyroNull.Instance);
        return obj;
    }
}
