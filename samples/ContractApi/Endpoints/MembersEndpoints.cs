using ContractApi.Contracts;
using Rivet;

namespace ContractApi.Endpoints;

public static class MembersEndpoints
{
    public static IEndpointRouteBuilder MapMembersEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Minimal API example mapped separately from the controller's /api/health action.
        endpoints.MapGet(
            MembersContract.MinimalRoutePrefix + MembersContract.Health.Route,
            () => MembersContract.Health.Success().ToResult()
        );

        return endpoints;
    }
}
