using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Characters;

namespace HorseFollowsYou.Services;

// ----------------------------
// 馬の経路を探索する
// - BFS による経路探索
// - 隣接タイルの列挙
// - 探索結果からの経路復元
// ----------------------------
internal sealed class HorsePathfinder
{
    private readonly HorseFollowTargetResolver targetResolver;

    // ----------------------------
    // 経路探索クラスを初期化する
    // ----------------------------
    public HorsePathfinder(HorseFollowTargetResolver targetResolver)
    {
        this.targetResolver = targetResolver;
    }

    // ----------------------------
    // 経路を探索する
    // ----------------------------
    public List<Point>? BuildPath(GameLocation location, Horse horse, Point start, Point goal)
    {
        if (start == goal)
        {
            return new List<Point>();
        }

        Queue<Point> open = new();
        Dictionary<Point, Point?> cameFrom = new();
        open.Enqueue(start);
        cameFrom[start] = null;

        int expanded = 0;
        const int maxExpanded = 2500;

        while (open.Count > 0 && expanded < maxExpanded)
        {
            Point current = open.Dequeue();
            expanded++;

            foreach (Point next in this.GetNeighbors(current))
            {
                if (cameFrom.ContainsKey(next))
                {
                    continue;
                }

                if (!this.targetResolver.IsInsideMap(location, next))
                {
                    continue;
                }

                if (next != goal && !this.targetResolver.CanOccupyTile(location, horse, next))
                {
                    continue;
                }

                cameFrom[next] = current;
                if (next == goal)
                {
                    return this.ReconstructPath(cameFrom, goal);
                }

                open.Enqueue(next);
            }
        }

        return null;
    }

    // ----------------------------
    // 隣接4方向を返す
    // ----------------------------
    private IEnumerable<Point> GetNeighbors(Point point)
    {
        yield return new Point(point.X, point.Y - 1);
        yield return new Point(point.X + 1, point.Y);
        yield return new Point(point.X, point.Y + 1);
        yield return new Point(point.X - 1, point.Y);
    }

    // ----------------------------
    // 探索結果から経路を作る
    // ----------------------------
    private List<Point> ReconstructPath(Dictionary<Point, Point?> cameFrom, Point end)
    {
        List<Point> reversed = new();
        Point? current = end;
        while (current is not null && cameFrom.TryGetValue(current.Value, out Point? previous))
        {
            reversed.Add(current.Value);
            current = previous;
        }

        reversed.Reverse();
        if (reversed.Count > 0)
        {
            reversed.RemoveAt(0);
        }

        return reversed;
    }
}
