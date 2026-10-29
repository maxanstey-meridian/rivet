using ContractApi.Contracts;
using Rivet;

namespace ContractApi.Endpoints;

public static class MembersEndpoints
{
    public static IEndpointRouteBuilder MapMembersEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // The minimal-API side of the sample: the health check is mapped here, on the
        // contract's own route, and .Anonymous() agrees with a handler nobody guards.
        endpoints.MapGet(
            MembersContract.Health.Route,
            () => MembersContract.Health.Success().ToResult()
        );

        return endpoints;
    }
}
