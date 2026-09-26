namespace Ultron.Services;

/// <summary>
/// One domain of tool capability (files, apps, system, browser, phone, memory...).
/// </summary>
/// <remarks>
/// A handler is the only place that talks to Windows, the filesystem or ADB. It
/// must never reference the UI: it receives a <see cref="ToolCall"/> and returns
/// a <see cref="ToolResult"/>, and anything it needs (a path policy, an approval
/// service, a logger) arrives through its constructor.
/// </remarks>
public interface IToolHandler
{
    /// <summary>Tool name as the model knows it, e.g. "file_processor".</summary>
    string Name { get; }

    Task<ToolResult> ExecuteAsync(ToolCall toolCall, CancellationToken cancellationToken);
}

/// <summary>
/// Adapts a plain function into a handler. Useful for one-off tools and for the
/// composition root while handlers are still being moved out of the UI.
/// </summary>
public sealed class DelegateToolHandler : IToolHandler
{
    private readonly Func<ToolCall, CancellationToken, Task<ToolResult>> _execute;
    private readonly Func<ToolCall, CancellationToken, ToolResult>? _executeSync;

    public DelegateToolHandler(string name, Func<ToolCall, CancellationToken, Task<ToolResult>> execute)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Tool name is required.", nameof(name));
        Name = name;
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
    }

    public DelegateToolHandler(string name, Func<ToolCall, CancellationToken, ToolResult> execute)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Tool name is required.", nameof(name));
        Name = name;
        ArgumentNullException.ThrowIfNull(execute);
        _executeSync = execute;
        _execute = (toolCall, _) => Task.FromResult(execute(toolCall, _));
    }

    public string Name { get; }

    public Task<ToolResult> ExecuteAsync(ToolCall toolCall, CancellationToken cancellationToken) =>
        _executeSync is not null
            ? Task.FromResult(_executeSync(toolCall, cancellationToken))
            : _execute(toolCall, cancellationToken);
}
