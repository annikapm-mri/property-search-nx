namespace PropertySearch.Api.Agents.Base;

public class AgentResult<T>
{
    public bool Success { get; set; }
    public T? Data { get; set; }
    public string AgentName { get; set; } = "";
    public string Message { get; set; } = "";
    public TimeSpan ExecutionTime { get; set; }
    public Dictionary<string, object> Metadata { get; set; } = new();
    public List<string> Logs { get; set; } = new();

    public static AgentResult<T> Ok(
        string agentName, T data, string message = "",
        Dictionary<string, object>? metadata = null) => new()
    {
        Success       = true,
        AgentName     = agentName,
        Data          = data,
        Message       = message,
        Metadata      = metadata ?? new()
    };

    public static AgentResult<T> Fail(
        string agentName, string message) => new()
    {
        Success   = false,
        AgentName = agentName,
        Message   = message
    };
}