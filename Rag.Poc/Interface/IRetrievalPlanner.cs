namespace Rag.Poc.Interface;

public interface IRetrievalPlanner
{
    Task<RetrievalPlan> CreatePlanAsync(
        string question,
        CancellationToken cancellationToken = default);
}