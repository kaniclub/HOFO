using Microsoft.Xna.Framework;

namespace HorseFollowsYou.Services;

// ----------------------------
// 非同期で経路探索を実行する
// ----------------------------
internal sealed class HorseAsyncPathService
{
    internal sealed record PathRequest(string HorseId, string LocationName, Point StartTile, Point GoalTile, long RequestedAtMs);

    internal sealed record PathResult(
        string HorseId,
        string LocationName,
        Point StartTile,
        Point GoalTile,
        long RequestedAtMs,
        long CompletedAtMs,
        List<Point>? Path,
        int Expanded);

    private readonly HorsePathfinder pathfinder;

    private PathRequest? pendingRequest;
    private System.Threading.Tasks.Task<PathResult>? pendingTask;

    public HorseAsyncPathService(HorsePathfinder pathfinder)
    {
        this.pathfinder = pathfinder;
    }

    public bool HasPendingRequest()
    {
        return this.pendingTask is not null && !this.pendingTask.IsCompleted;
    }

    public bool IsPendingFor(string horseId, string locationName, Point startTile, Point goalTile)
    {
        PathRequest? request = this.pendingRequest;
        if (request is null)
        {
            return false;
        }

        return string.Equals(request.HorseId, horseId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(request.LocationName, locationName, StringComparison.OrdinalIgnoreCase)
            && request.StartTile == startTile
            && request.GoalTile == goalTile;
    }

    public void Reset()
    {
        this.pendingRequest = null;
        this.pendingTask = null;
    }

    public bool TryQueue(HorsePathSnapshot snapshot, string horseId, string locationName, Point startTile, Point goalTile, long requestedAtMs)
    {
        if (this.pendingTask is not null && !this.pendingTask.IsCompleted)
        {
            return false;
        }

        PathRequest request = new(horseId, locationName, startTile, goalTile, requestedAtMs);
        this.pendingRequest = request;
        this.pendingTask = System.Threading.Tasks.Task.Run(() =>
        {
            List<Point>? path = this.pathfinder.BuildPath(snapshot, out int expanded);
            return new PathResult(
                request.HorseId,
                request.LocationName,
                request.StartTile,
                request.GoalTile,
                request.RequestedAtMs,
                Environment.TickCount64,
                path,
                expanded);
        });

        return true;
    }

    public bool TryTakeCompleted(out PathResult? result)
    {
        result = null;
        if (this.pendingTask is null || !this.pendingTask.IsCompleted)
        {
            return false;
        }

        result = this.pendingTask.Result;
        this.pendingTask = null;
        this.pendingRequest = null;
        return true;
    }
}
