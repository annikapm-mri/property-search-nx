namespace PropertySearch.Api.Agents.Base;
/// <summary>
/// Base contract that every agent in the system must implement.
/// See .github/agents/ for markdown definitions of each agent's behavior.
/// </summary>
// Every agent implements this contract
public interface IAgent<TInput, TOutput>
{
    string AgentName { get; }
    string AgentDescription { get; }
    Task<AgentResult<TOutput>> ExecuteAsync(TInput input, CancellationToken ct = default);
}