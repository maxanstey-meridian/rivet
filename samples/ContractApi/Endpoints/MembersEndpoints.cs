using ContractApi.Contracts;
using ContractApi.Controllers;

namespace ContractApi.Endpoints;

public static class MembersEndpoints
{
    public static IEndpointRouteBuilder MapMembersEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Minimal API example mapped separately from the controller's /api/health action.
        endpoints.MapGet(
            MembersContract.MinimalRoutePrefix + MembersContract.Health.Route,
            async () => (await MembersContract.Health.Invoke(async () => { })).ToResult()
        );

        return endpoints;
    }
}
