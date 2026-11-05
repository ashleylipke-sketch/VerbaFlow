using VerbaFlow.Infrastructure.Services;

namespace VerbaFlow.Api;

/// <summary>The workload overview: administrators, and department admins for their departments.</summary>
public static class WorkloadEndpoints
{
    public static void MapWorkloadEndpoints(this RouteGroupBuilder api) =>
        api.MapGet("/workload", (HttpContext h, WorkloadService w, string? mode, int? days, Guid? department) =>
            w.OverviewAsync(h.Current(), mode, days, department));
}
