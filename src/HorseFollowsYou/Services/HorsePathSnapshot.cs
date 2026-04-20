using Microsoft.Xna.Framework;

namespace HorseFollowsYou.Services;

// ----------------------------
// 非同期経路探索用のスナップショット
// - メインスレッドで通行可否を固定化する
// - ワーカースレッドではこの情報だけを見る
// ----------------------------
internal sealed class HorsePathSnapshot
{
    private readonly bool[] occupiableTiles;

    public int MinX { get; }
    public int MinY { get; }
    public int Width { get; }
    public int Height { get; }
    public Point Start { get; }
    public Point Goal { get; }

    public HorsePathSnapshot(int minX, int minY, int width, int height, Point start, Point goal, bool[] occupiableTiles)
    {
        this.MinX = minX;
        this.MinY = minY;
        this.Width = width;
        this.Height = height;
        this.Start = start;
        this.Goal = goal;
        this.occupiableTiles = occupiableTiles;
    }

    public bool IsInsideBounds(Point tile)
    {
        return tile.X >= this.MinX
            && tile.Y >= this.MinY
            && tile.X < this.MinX + this.Width
            && tile.Y < this.MinY + this.Height;
    }

    public bool CanOccupy(Point tile)
    {
        if (!this.IsInsideBounds(tile))
        {
            return false;
        }

        int localX = tile.X - this.MinX;
        int localY = tile.Y - this.MinY;
        return this.occupiableTiles[(localY * this.Width) + localX];
    }
}
