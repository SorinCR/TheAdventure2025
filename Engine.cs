using System.Reflection;
using System.Text.Json;
using Silk.NET.Maths;
using TheAdventure.Models;
using TheAdventure.Models.Data;
using TheAdventure.Scripting;

namespace TheAdventure;

public class Engine
{
    private readonly GameRenderer _renderer;
    private readonly Input _input;
    private readonly ScriptEngine _scriptEngine = new();

    private readonly Dictionary<int, GameObject> _gameObjects = new();
    private readonly Dictionary<string, TileSet> _loadedTileSets = new();
    private readonly Dictionary<int, Tile> _tileIdMap = new();

    private Level _currentLevel = new();
    private PlayerObject? _player;

    private readonly int _gameOverTextureId;
    private readonly TextureData _gameOverTextureData;

    private DateTimeOffset _lastUpdate = DateTimeOffset.Now;

    private const int AttackBombDistance = 80;

    public Engine(GameRenderer renderer, Input input)
    {
        _renderer = renderer;
        _input = input;

        _gameOverTextureId = _renderer.LoadTexture(Path.Combine("Assets", "game_over.png"), out _gameOverTextureData);

        _input.OnMouseClick += (_, coords) => AddBomb(coords.x, coords.y);
    }

    public void SetupWorld()
    {
        _gameObjects.Clear();
        _loadedTileSets.Clear();
        _tileIdMap.Clear();

        _player = new(SpriteSheet.Load(_renderer, "Player.json", "Assets"), 100, 100);

        var levelContent = File.ReadAllText(Path.Combine("Assets", "terrain.tmj"));
        var level = JsonSerializer.Deserialize<Level>(levelContent);
        if (level == null)
        {
            throw new Exception("Failed to load level");
        }

        foreach (var tileSetRef in level.TileSets)
        {
            var tileSetContent = File.ReadAllText(Path.Combine("Assets", tileSetRef.Source));
            var tileSet = JsonSerializer.Deserialize<TileSet>(tileSetContent);
            if (tileSet == null)
            {
                throw new Exception("Failed to load tile set");
            }

            foreach (var tile in tileSet.Tiles)
            {
                tile.TextureId = _renderer.LoadTexture(Path.Combine("Assets", tile.Image), out _);
                _tileIdMap.Add(tile.Id!.Value, tile);
            }

            _loadedTileSets.Add(tileSet.Name, tileSet);
        }

        if (level.Width == null || level.Height == null)
        {
            throw new Exception("Invalid level dimensions");
        }

        if (level.TileWidth == null || level.TileHeight == null)
        {
            throw new Exception("Invalid tile dimensions");
        }

        _renderer.SetWorldBounds(new Rectangle<int>(0, 0, level.Width.Value * level.TileWidth.Value,
            level.Height.Value * level.TileHeight.Value));

        _currentLevel = level;

        _scriptEngine.LoadAll(Path.Combine("Assets", "Scripts"));
    }

    public void ProcessFrame()
    {
        var currentTime = DateTimeOffset.Now;
        var msSinceLastFrame = (currentTime - _lastUpdate).TotalMilliseconds;
        _lastUpdate = currentTime;

        if (_player == null)
        {
            return;
        }

        if (_player.State.State == PlayerObject.PlayerState.GameOver)
        {
            if (_input.IsKeyRPressed())
            {
                SetupWorld();
            }

            return;
        }

        double up = _input.IsUpPressed() ? 1.0 : 0.0;
        double down = _input.IsDownPressed() ? 1.0 : 0.0;
        double left = _input.IsLeftPressed() ? 1.0 : 0.0;
        double right = _input.IsRightPressed() ? 1.0 : 0.0;
        bool isAttacking = _input.IsKeyAPressed() && (up + down + left + right <= 1);
        bool addBomb = _input.IsKeyBPressed();

        bool spawnAttackBomb = false;

        _player.UpdatePosition(up, down, left, right, 48, 48, msSinceLastFrame);
        if (isAttacking)
        {
            //if (_player.State.State != PlayerObject.PlayerState.Attack)
            //{
            //    spawnAttackBomb = true;
            //}
            _player.Attack();
            DeflectNearbyBombs();
        }
        
        _scriptEngine.ExecuteAll(this);

        if (spawnAttackBomb)
        {
            var playerPos = _player.Position;
            var offset = _player.State.Direction switch
            {
                PlayerObject.PlayerStateDirection.Up => (X: 0, Y: -AttackBombDistance),
                PlayerObject.PlayerStateDirection.Down => (X: 0, Y: AttackBombDistance),
                PlayerObject.PlayerStateDirection.Left => (X: -AttackBombDistance, Y: 0),
                PlayerObject.PlayerStateDirection.Right => (X: AttackBombDistance, Y: 0),
                _ => (X: 0, Y: 0)
            };
            AddBomb(playerPos.X + offset.X, playerPos.Y + offset.Y, false);
        }


        if (addBomb)
        {
            AddBomb(_player.Position.X, _player.Position.Y, false);
        }
    }

    public void RenderFrame()
    {
        _renderer.SetDrawColor(0, 0, 0, 255);
        _renderer.ClearScreen();

        var playerPosition = _player!.Position;
        _renderer.CameraLookAt(playerPosition.X, playerPosition.Y);

        RenderTerrain();
        RenderAllObjects();

        if (_player != null && _player.State.State == PlayerObject.PlayerState.GameOver)
        {
            var winSize = _renderer.WindowSize;
            var src = new Rectangle<int>(0, 0, _gameOverTextureData.Width, _gameOverTextureData.Height);
            var dst = new Rectangle<int>((winSize.Width - _gameOverTextureData.Width) / 2,
                (winSize.Height - _gameOverTextureData.Height) / 2,
                _gameOverTextureData.Width, _gameOverTextureData.Height);
            _renderer.RenderTextureScreen(_gameOverTextureId, src, dst);
        }

        _renderer.PresentFrame();
    }

    public void RenderAllObjects()
    {
        var toRemove = new List<int>();
        foreach (var gameObject in GetRenderables())
        {
            gameObject.Render(_renderer);
            if (gameObject is TemporaryGameObject { IsExpired: true } tempGameObject)
            {
                toRemove.Add(tempGameObject.Id);
            }
        }

        foreach (var id in toRemove)
        {
            _gameObjects.Remove(id, out var gameObject);

            if (_player == null || _player.State.State == PlayerObject.PlayerState.GameOver)
            {
                continue;
            }

            var tempGameObject = (TemporaryGameObject)gameObject!;
            var deltaX = Math.Abs(_player.Position.X - tempGameObject.Position.X);
            var deltaY = Math.Abs(_player.Position.Y - tempGameObject.Position.Y);
            if (deltaX < 32 && deltaY < 32)
            {
                _player.GameOver();
            }
        }

        _player?.Render(_renderer);
    }

    public void RenderTerrain()
    {
        foreach (var currentLayer in _currentLevel.Layers)
        {
            for (int i = 0; i < _currentLevel.Width; ++i)
            {
                for (int j = 0; j < _currentLevel.Height; ++j)
                {
                    int? dataIndex = j * currentLayer.Width + i;
                    if (dataIndex == null)
                    {
                        continue;
                    }

                    var currentTileId = currentLayer.Data[dataIndex.Value] - 1;
                    if (currentTileId == null)
                    {
                        continue;
                    }

                    var currentTile = _tileIdMap[currentTileId.Value];

                    var tileWidth = currentTile.ImageWidth ?? 0;
                    var tileHeight = currentTile.ImageHeight ?? 0;

                    var sourceRect = new Rectangle<int>(0, 0, tileWidth, tileHeight);
                    var destRect = new Rectangle<int>(i * tileWidth, j * tileHeight, tileWidth, tileHeight);
                    _renderer.RenderTexture(currentTile.TextureId, sourceRect, destRect);
                }
            }
        }
    }

    public IEnumerable<RenderableGameObject> GetRenderables()
    {
        foreach (var gameObject in _gameObjects.Values)
        {
            if (gameObject is RenderableGameObject renderableGameObject)
            {
                yield return renderableGameObject;
            }
        }
    }

    public (int X, int Y) GetPlayerPosition()
    {
        return _player!.Position;
    }

    public void AddBomb(int X, int Y, bool translateCoordinates = true)
    {
        var worldCoords = translateCoordinates ? _renderer.ToWorldCoordinates(X, Y) : new Vector2D<int>(X, Y);

        SpriteSheet spriteSheet = SpriteSheet.Load(_renderer, "BombExploding.json", "Assets");
        spriteSheet.ActivateAnimation("Explode");

        //TemporaryGameObject bomb = new(spriteSheet, 2.1, (worldCoords.X, worldCoords.Y));
        BombObject bomb = new(spriteSheet, 2.1, (worldCoords.X, worldCoords.Y));
        _gameObjects.Add(bomb.Id, bomb);
    }

    private void DeflectNearbyBombs()
    {
        if (_player == null)
        {
            return;
        }

        if (_player.State.State != PlayerObject.PlayerState.Attack)
        {
            return;
        }

        var bombs = _gameObjects.Values
            .OfType<BombObject>()
            .Where(b => !b.IsExpired && !b.Deflected)
            .ToList();

        foreach (var bomb in bombs)
        {
            var dx = bomb.Position.X - _player.Position.X;
            var dy = bomb.Position.Y - _player.Position.Y;

            if (dx * dx + dy * dy > 50 * 50)
            {
                continue;
            }

            bool inDir = _player.State.Direction switch
            {
                PlayerObject.PlayerStateDirection.Up => dy <= 0 && Math.Abs(dx) <= 40,
                PlayerObject.PlayerStateDirection.Down => dy >= 0 && Math.Abs(dx) <= 40,
                PlayerObject.PlayerStateDirection.Left => dx <= 0 && Math.Abs(dy) <= 40,
                PlayerObject.PlayerStateDirection.Right => dx >= 0 && Math.Abs(dy) <= 40,
                _ => false
            };

            if (inDir)
            {
                bomb.Deflect(_player.State.Direction);
            }
        }
    }
}