using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Characters;

namespace HorseFollowsYou.Services;

// ----------------------------
// 馬の追従目標を解決する
// - 追従先タイルの決定
// - 通行可否の判定
// - 座標変換
// ----------------------------
internal sealed class HorseFollowTargetResolver
{
    private readonly System.Func<ModConfig> getConfig;

    // ----------------------------
    // 追従目標解決クラスを初期化する
    // ----------------------------
    public HorseFollowTargetResolver(System.Func<ModConfig> getConfig)
    {
        this.getConfig = getConfig;
    }

    // ----------------------------
    // 馬の目標タイルを決める
    // ----------------------------
    public Point? ResolveFollowTargetTile(Horse horse, Farmer player, Point lastPlayerMoveDirection)
    {
        Point playerTile = player.TilePoint;
        Point back = this.GetPreferredBackDirection(player, lastPlayerMoveDirection);
        Point left = new(-back.Y, back.X);
        Point right = new(back.Y, -back.X);

        foreach (Point candidate in this.GetTargetCandidates(playerTile, back, left, right))
        {
            if (candidate == playerTile)
            {
                continue;
            }

            if (!this.IsInsideMap(player.currentLocation, candidate))
            {
                continue;
            }

            if (this.CanOccupyTile(player.currentLocation, horse, candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    // ----------------------------
    // 候補タイルを順番に返す
    // ----------------------------
    private IEnumerable<Point> GetTargetCandidates(Point playerTile, Point back, Point left, Point right)
    {
        yield return this.Offset(playerTile, back, 2);
        yield return this.Offset(playerTile, back, 1);
        yield return this.Offset(this.Offset(playerTile, back, 2), left, 1);
        yield return this.Offset(this.Offset(playerTile, back, 2), right, 1);
        yield return this.Offset(this.Offset(playerTile, back, 1), left, 1);
        yield return this.Offset(this.Offset(playerTile, back, 1), right, 1);
        yield return this.Offset(playerTile, left, 1);
        yield return this.Offset(playerTile, right, 1);
    }

    // ----------------------------
    // プレイヤー後方の向きを返す
    // ----------------------------
    private Point GetPreferredBackDirection(Farmer player, Point lastPlayerMoveDirection)
    {
        if (lastPlayerMoveDirection != Point.Zero)
        {
            return new Point(-lastPlayerMoveDirection.X, -lastPlayerMoveDirection.Y);
        }

        return player.FacingDirection switch
        {
            0 => new Point(0, 1),
            1 => new Point(-1, 0),
            2 => new Point(0, -1),
            3 => new Point(1, 0),
            _ => new Point(0, 1),
        };
    }

    // ----------------------------
    // 座標をずらした位置を返す
    // ----------------------------
    private Point Offset(Point origin, Point direction, int distance)
    {
        return new Point(origin.X + direction.X * distance, origin.Y + direction.Y * distance);
    }

    // ----------------------------
    // そのタイルに入れるか確認する
    // ----------------------------
    public bool CanOccupyTile(StardewValley.GameLocation location, Horse horse, Point tile)
    {
        return this.CanOccupyRawPosition(location, horse, this.GetRawPositionForTile(horse, tile));
    }

    // ----------------------------
    // その位置に入れるか確認する
    // ----------------------------
    public bool CanOccupyRawPosition(StardewValley.GameLocation location, Horse horse, Vector2 rawPosition)
    {
        Rectangle rect = this.GetCollisionRect(rawPosition);

        Rectangle bbox = this.ClampRectToMap(location, rect);
        if (bbox.Width <= 0 || bbox.Height <= 0)
        {
            return false;
        }

        foreach (Point tile in this.GetOccupiedTiles(bbox))
        {
            if (!this.IsInsideMap(location, tile))
            {
                return false;
            }

            Vector2 tileVector = new(tile.X, tile.Y);
            if (!location.isTilePassable(tileVector))
            {
                return false;
            }

            if (location.objects.TryGetValue(tileVector, out StardewValley.Object? obj) && !obj.isPassable())
            {
                return false;
            }

            if (location.terrainFeatures.TryGetValue(tileVector, out StardewValley.TerrainFeatures.TerrainFeature? feature) && !feature.isPassable())
            {
                return false;
            }

            if (location.getBuildingAt(tileVector) is not null)
            {
                return false;
            }
        }

        foreach (var character in location.characters)
        {
            if (ReferenceEquals(character, horse))
            {
                continue;
            }

            if (rect.Intersects(character.GetBoundingBox()))
            {
                return false;
            }
        }

        if (!this.getConfig().IgnorePlayerCollision)
        {
            foreach (Farmer farmer in location.farmers)
            {
                if (rect.Intersects(farmer.GetBoundingBox()))
                {
                    return false;
                }
            }
        }

        return true;
    }

    // ----------------------------
    // 当たり判定を作る
    // ----------------------------
    private Rectangle GetCollisionRect(Vector2 rawPosition)
    {
        int width = this.GetCollisionWidth();
        int offsetX = this.GetCollisionOffsetX(width);
        return new Rectangle((int)rawPosition.X + offsetX, (int)rawPosition.Y + 16, width, 32);
    }

    // ----------------------------
    // 経路計算用の当たり判定幅を返す
    // ----------------------------
    private int GetCollisionWidth()
    {
        return this.getConfig().UseNarrowHitbox ? 32 : 48;
    }

    // ----------------------------
    // 当たり判定の X オフセットを返す
    // ----------------------------
    private int GetCollisionOffsetX(int width)
    {
        return 8 + ((48 - width) / 2);
    }

    // ----------------------------
    // マップ内に収まる矩形へ変換する
    // ----------------------------
    private Rectangle ClampRectToMap(StardewValley.GameLocation location, Rectangle rect)
    {
        int mapWidth = location.Map.Layers[0].LayerWidth * 64;
        int mapHeight = location.Map.Layers[0].LayerHeight * 64;
        if (rect.Left < 0 || rect.Top < 0 || rect.Right > mapWidth || rect.Bottom > mapHeight)
        {
            return Rectangle.Empty;
        }

        return rect;
    }

    // ----------------------------
    // 矩形が触れるタイルを列挙する
    // ----------------------------
    private IEnumerable<Point> GetOccupiedTiles(Rectangle rect)
    {
        int left = rect.Left / 64;
        int right = (rect.Right - 1) / 64;
        int top = rect.Top / 64;
        int bottom = (rect.Bottom - 1) / 64;

        for (int y = top; y <= bottom; y++)
        {
            for (int x = left; x <= right; x++)
            {
                yield return new Point(x, y);
            }
        }
    }

    // ----------------------------
    // タイルの生座標を返す
    // ----------------------------
    public Vector2 GetRawPositionForTile(Horse horse, Point tile)
    {
        int visualWidth = horse.GetBoundingBox().Width;
        return new Vector2(tile.X * 64f + 32f - visualWidth / 2f, tile.Y * 64f + 4f);
    }

    // ----------------------------
    // 非同期探索用のスナップショットを作る
    // ----------------------------
    public HorsePathSnapshot? CreatePathSnapshot(GameLocation location, Horse horse, Point start, Point goal)
    {
        if (!this.IsInsideMap(location, start) || !this.IsInsideMap(location, goal))
        {
            return null;
        }

        int searchPadding = this.GetSnapshotPadding(start, goal);
        int minX = System.Math.Max(0, System.Math.Min(start.X, goal.X) - searchPadding);
        int minY = System.Math.Max(0, System.Math.Min(start.Y, goal.Y) - searchPadding);
        int maxX = System.Math.Min(location.Map.Layers[0].LayerWidth - 1, System.Math.Max(start.X, goal.X) + searchPadding);
        int maxY = System.Math.Min(location.Map.Layers[0].LayerHeight - 1, System.Math.Max(start.Y, goal.Y) + searchPadding);

        int width = maxX - minX + 1;
        int height = maxY - minY + 1;
        bool[] occupiableTiles = new bool[width * height];

        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                Point tile = new(x, y);
                bool occupiable = tile == start || tile == goal || this.CanOccupyTile(location, horse, tile);
                occupiableTiles[((y - minY) * width) + (x - minX)] = occupiable;
            }
        }

        return new HorsePathSnapshot(minX, minY, width, height, start, goal, occupiableTiles);
    }

    // ----------------------------
    // スナップショット化する範囲の余白を返す
    // ----------------------------
    private int GetSnapshotPadding(Point start, Point goal)
    {
        int manhattanDistance = System.Math.Abs(start.X - goal.X) + System.Math.Abs(start.Y - goal.Y);
        return System.Math.Clamp(manhattanDistance + 8, 12, 48);
    }

    // ----------------------------
    // タイルがマップ内か確認する
    // ----------------------------
    public bool IsInsideMap(StardewValley.GameLocation location, Point tile)
    {
        return tile.X >= 0
            && tile.Y >= 0
            && tile.X < location.Map.Layers[0].LayerWidth
            && tile.Y < location.Map.Layers[0].LayerHeight;
    }
}
