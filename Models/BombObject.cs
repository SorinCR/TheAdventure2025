using Silk.NET.SDL;

namespace TheAdventure.Models;

public class BombObject : TemporaryGameObject
{
    public bool Deflected { get; private set; }

    public BombObject(SpriteSheet spriteSheet, double ttl, (int X, int Y) position)
        : base(spriteSheet, ttl, position)
    {
    }

    public void Deflect(PlayerObject.PlayerStateDirection direction)
    {
        if (Deflected) return;

        const int pushDistance = 150;
        var dir = direction switch
        {
            PlayerObject.PlayerStateDirection.Left => (-1, 0),
            PlayerObject.PlayerStateDirection.Right => (1, 0),
            PlayerObject.PlayerStateDirection.Up => (0, -1),
            PlayerObject.PlayerStateDirection.Down => (0, 1),
            _ => (0, 0)
        };

        Position = (Position.X + dir.Item1 * pushDistance, Position.Y + dir.Item2 * pushDistance);
        Deflected = true;
    }
}