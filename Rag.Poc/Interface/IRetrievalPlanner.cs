namespace Rag.Poc.Interface;

/// <summary>
/// Provides the functionality to create the plan for chunks retrival
/// </summary>
public interface IRetrievalPlanner
{
    Task<RetrievalPlan> CreatePlanAsync(string question, CancellationToken cancellationToken = default);
}