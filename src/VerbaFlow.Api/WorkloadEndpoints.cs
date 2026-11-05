using VerbaFlow.Infrastructure.Services;

namespace VerbaFlow.Api;

/// <summary>The administrators' workload overview.</summary>
public static class WorkloadEndpoints
{
    public static void MapWorkloadEndpoints(this RouteGroupBuilder api) =>
        api.MapGet("/workload", (HttpContext h, WorkloadService w, string? mode, int? days) => w.OverviewAsync(h.Current(), mode, days));
}
