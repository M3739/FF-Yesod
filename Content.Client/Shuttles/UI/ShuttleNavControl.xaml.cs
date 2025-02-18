using System.Numerics;
using Content.Client.Station; // Frontier
using Content.Shared.Shuttles.BUIStates;
using Content.Shared.Shuttles.Components;
using Content.Shared.Shuttles.Systems;
using JetBrains.Annotations;
using Robust.Client.AutoGenerated;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.XAML;
using Robust.Shared.Input;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;

namespace Content.Client.Shuttles.UI;

[GenerateTypedNameReferences]
public sealed partial class ShuttleNavControl : BaseShuttleControl
{
    [Dependency] private readonly IMapManager _mapManager = default!;
    [Dependency] private readonly IUserInterfaceManager _uiManager = default!;
    private readonly StationSystem _station; // Frontier
    private readonly SharedShuttleSystem _shuttles;
    private readonly SharedTransformSystem _transform;

    /// <summary>
    /// Used to transform all of the radar objects. Typically is a shuttle console parented to a grid.
    /// </summary>
    private EntityCoordinates? _coordinates;

    /// <summary>
    /// Entity of controlling console
    /// </summary>
    private EntityUid? _consoleEntity;

    private Angle? _rotation;

    private Dictionary<NetEntity, List<DockingPortState>> _docks = new();

    public bool ShowIFF { get; set; } = true;
    public bool ShowIFFShuttles { get; set; } = true;
    public bool ShowDocks { get; set; } = true;
    public bool RotateWithEntity { get; set; } = true;

    public float MaximumIFFDistance { get; set; } = -1f; // Frontier
    public bool HideCoords { get; set; } = false; // Frontier

    private static Color _dockLabelColor = Color.White; // Frontier

    /// <summary>
    ///   If present, called for every IFF. Must determine if it should or should not be shown.
    /// </summary>
    public Func<EntityUid, MapGridComponent, IFFComponent?, bool>? IFFFilter { get; set; } = null;

    /// <summary>
    /// Raised if the user left-clicks on the radar control with the relevant entitycoordinates.
    /// </summary>
    public Action<EntityCoordinates>? OnRadarClick;

    private List<Entity<MapGridComponent>> _grids = new();

    public ShuttleNavControl() : base(64f, 256f, 256f)
    {
        RobustXamlLoader.Load(this);
        _shuttles = EntManager.System<SharedShuttleSystem>();
        _transform = EntManager.System<SharedTransformSystem>();
        _station = EntManager.System<StationSystem>(); // Frontier
    }

    public void SetMatrix(EntityCoordinates? coordinates, Angle? angle)
    {
        _coordinates = coordinates;
        _rotation = angle;
    }

    public void SetConsole(EntityUid? consoleEntity)
    {
        _consoleEntity = consoleEntity;
    }

    protected override void KeyBindUp(GUIBoundKeyEventArgs args)
    {
        base.KeyBindUp(args);

        if (_coordinates == null || _rotation == null || args.Function != EngineKeyFunctions.UIClick ||
            OnRadarClick == null)
        {
            return;
        }

        var a = InverseScalePosition(args.RelativePosition);
        var relativeWorldPos = a with { Y = -a.Y };
        relativeWorldPos = _rotation.Value.RotateVec(relativeWorldPos);
        var coords = _coordinates.Value.Offset(relativeWorldPos);
        OnRadarClick?.Invoke(coords);
    }

    /// <summary>
    /// Gets the entity coordinates of where the mouse position is, relative to the control.
    /// </summary>
    [PublicAPI]
    public EntityCoordinates GetMouseCoordinatesFromCenter()
    {
        if (_coordinates == null || _rotation == null)
        {
            return EntityCoordinates.Invalid;
        }

        var pos = _uiManager.MousePositionScaled.Position - GlobalPosition;
        var relativeWorldPos = _rotation.Value.RotateVec(pos);

        // I am not sure why the resulting point is 20 units under the mouse.
        return _coordinates.Value.Offset(relativeWorldPos);
    }

    public void UpdateState(NavInterfaceState state)
    {
        SetMatrix(EntManager.GetCoordinates(state.Coordinates), state.Angle);

        WorldMaxRange = state.MaxRange;

        if (WorldMaxRange < WorldRange)
        {
            ActualRadarRange = WorldMaxRange;
        }

        if (WorldMaxRange < WorldMinRange)
            WorldMinRange = WorldMaxRange;

        ActualRadarRange = Math.Clamp(ActualRadarRange, WorldMinRange, WorldMaxRange);

        RotateWithEntity = state.RotateWithEntity;

        // Frontier
        if (state.MaxIffRange != null)
            MaximumIFFDistance = state.MaxIffRange.Value;
        HideCoords = state.HideCoords;
        // End Frontier

        _docks = state.Docks;

        NfUpdateState(state); // Frontier Update State
    }

    protected override void Draw(DrawingHandleScreen handle)
    {
        base.Draw(handle);

        DrawBacking(handle);
        DrawCircles(handle);

        // No data
        if (_coordinates == null || _rotation == null)
        {
            return;
        }

        var xformQuery = EntManager.GetEntityQuery<TransformComponent>();
        var fixturesQuery = EntManager.GetEntityQuery<FixturesComponent>();
        var bodyQuery = EntManager.GetEntityQuery<PhysicsComponent>();

        if (!xformQuery.TryGetComponent(_coordinates.Value.EntityId, out var xform)
            || xform.MapID == MapId.Nullspace)
        {
            return;
        }

        var mapPos = _transform.ToMapCoordinates(_coordinates.Value);
        var posMatrix = Matrix3Helpers.CreateTransform(_coordinates.Value.Position, _rotation.Value);
        var ourEntRot = RotateWithEntity ? _transform.GetWorldRotation(xform) : _rotation.Value;
        var ourEntMatrix = Matrix3Helpers.CreateTransform(_transform.GetWorldPosition(xform), ourEntRot);
        var shuttleToWorld = Matrix3x2.Multiply(posMatrix, ourEntMatrix);
        Matrix3x2.Invert(shuttleToWorld, out var worldToShuttle);
        var shuttleToView = Matrix3x2.CreateScale(new Vector2(MinimapScale, -MinimapScale)) * Matrix3x2.CreateTranslation(MidPointVector);

        // Frontier Corvax: north line drawing
        var rot = ourEntRot + _rotation.Value;
        DrawNorthLine(handle, rot);

        // Draw our grid in detail
        var ourGridId = xform.GridUid;
        if (EntManager.TryGetComponent<MapGridComponent>(ourGridId, out var ourGrid) &&
            fixturesQuery.HasComponent(ourGridId.Value))
        {
            var ourGridToWorld = _transform.GetWorldMatrix(ourGridId.Value);
            var ourGridToShuttle = Matrix3x2.Multiply(ourGridToWorld, worldToShuttle);
            var ourGridToView = ourGridToShuttle * shuttleToView;
            var color = _shuttles.GetIFFColor(ourGridId.Value, self: true);

            DrawGrid(handle, ourGridToView, (ourGridId.Value, ourGrid), color);
            DrawDocks(handle, ourGridId.Value, ourGridToView);
        }

        // Draw radar position on the station
        const float radarVertRadius = 2f;
        var radarPosVerts = new Vector2[]
        {
            ScalePosition(new Vector2(0f, -radarVertRadius)),
            ScalePosition(new Vector2(radarVertRadius / 2f, 0f)),
            ScalePosition(new Vector2(0f, radarVertRadius)),
            ScalePosition(new Vector2(radarVertRadius / -2f, 0f)),
        };

        handle.DrawPrimitives(DrawPrimitiveTopology.TriangleFan, radarPosVerts, Color.Lime);

        var viewBounds = new Box2Rotated(new Box2(-WorldRange, -WorldRange, WorldRange, WorldRange).Translated(mapPos.Position), rot, mapPos.Position);
        var viewAABB = viewBounds.CalcBoundingBox();

        _grids.Clear();
        _mapManager.FindGridsIntersecting(xform.MapID, new Box2(mapPos.Position - MaxRadarRangeVector, mapPos.Position + MaxRadarRangeVector), ref _grids, approx: true, includeMap: false);

        // Frontier - collect blip location data outside foreach - more changes ahead
        var blipDataList = new List<BlipData>();

        // Draw other grids... differently
        foreach (var grid in _grids)
        {
            var gUid = grid.Owner;
            if (gUid == ourGridId || !fixturesQuery.HasComponent(gUid))
                continue;

            var gridBody = bodyQuery.GetComponent(gUid);
            EntManager.TryGetComponent<IFFComponent>(gUid, out var iff);

            if (!_shuttles.CanDraw(gUid, gridBody, iff))
                continue;

            var curGridToWorld = _transform.GetWorldMatrix(gUid);
            var curGridToView = curGridToWorld * worldToShuttle * shuttleToView;

            var labelColor = _shuttles.GetIFFColor(grid, self: false, iff);
            var coordColor = new Color(labelColor.R * 0.8f, labelColor.G * 0.8f, labelColor.B * 0.8f, 0.5f);

            // Others default:
            // Color.FromHex("#FFC000FF")
            // Hostile default: Color.Firebrick
            var labelName = _shuttles.GetIFFLabel(grid, self: false, iff);

            var isPlayerShuttle = iff != null && (iff.Flags & IFFFlags.IsPlayerShuttle) != 0x0;
            var shouldDrawIFF = ShowIFF && labelName != null && (iff != null && (iff.Flags & IFFFlags.HideLabel) == 0x0);
            if (IFFFilter != null)
            {
                shouldDrawIFF &= IFFFilter(gUid, grid.Comp, iff);
            }
            if (isPlayerShuttle)
            {
                shouldDrawIFF &= ShowIFFShuttles;
            }

            //var mapCenter = curGridToWorld. * gridBody.LocalCenter;
            //shouldDrawIFF = NfCheckShouldDrawIffRangeCondition(shouldDrawIFF, mapCenter, curGridToWorld); // Frontier code
            // Frontier: range checks
            var gridMapPos = _transform.ToMapCoordinates(new EntityCoordinates(gUid, gridBody.LocalCenter)).Position;
            shouldDrawIFF = NfCheckShouldDrawIffRangeCondition(shouldDrawIFF, gridMapPos - mapPos.Position);
            // End Frontier

            if (shouldDrawIFF)
            {
                //var gridCentre = Vector2.Transform(gridBody.LocalCenter, curGridToView);
                //gridCentre.Y = -gridCentre.Y;

                // Frontier: IFF drawing functions
                // The actual position in the UI. We offset the matrix position to render it off by half its width
                // plus by the offset.
                //var uiPosition = ScalePosition(gridCentre) / UIScale;
                var uiPosition = Vector2.Transform(gridBody.LocalCenter, curGridToView) / UIScale;

                // Confines the UI position within the viewport.
                var uiXCentre = (int) Width / 2;
                var uiYCentre = (int) Height / 2;
                var uiXOffset = uiPosition.X - uiXCentre;
                var uiYOffset = uiPosition.Y - uiYCentre;
                var uiDistance = (int) Math.Sqrt(Math.Pow(uiXOffset, 2) + Math.Pow(uiYOffset, 2));
                var uiX = uiXCentre * uiXOffset / uiDistance;
                var uiY = uiYCentre * uiYOffset / uiDistance;

                var isOutsideRadarCircle = uiDistance > Math.Abs(uiX) && uiDistance > Math.Abs(uiY);
                if (isOutsideRadarCircle)
                {
                    // 0.95f for offsetting the icons slightly away from edge of radar so it doesnt clip.
                    uiX = uiXCentre * uiXOffset / uiDistance * 0.95f;
                    uiY = uiYCentre * uiYOffset / uiDistance * 0.95f;
                    uiPosition = new Vector2(
                        x: uiX + uiXCentre,
                        y: uiY + uiYCentre
                    );
                }

                var scaledMousePosition = GetMouseCoordinatesFromCenter().Position * UIScale;
                var isMouseOver = Vector2.Distance(scaledMousePosition, uiPosition * UIScale) < 30f;

                // Distant stations that are not player controlled ships
                var isDistantPOI = iff != null || (iff == null || (iff.Flags & IFFFlags.IsPlayerShuttle) == 0x0);

                var distance = Vector2.Distance(gridMapPos, mapPos.Position);

                if (!isOutsideRadarCircle || isDistantPOI || isMouseOver)
                {
                    // Shows decimal when distance is < 50m, otherwise pointless to show it.
                    var displayedDistance = distance < 50f ? $"{distance:0.0}" : distance < 1000 ? $"{distance:0}" : $"{distance / 1000:0.0}k";
                    var labelText = Loc.GetString("shuttle-console-iff-label", ("name", labelName)!, ("distance", displayedDistance));

                    var coordsText = $"({gridMapPos.X:0.0}, {gridMapPos.Y:0.0})";

                    // Calculate unscaled offsets.
                    var labelDimensions = handle.GetDimensions(Font, labelText, 1f);
                    var blipSize = RadarBlipSize * 0.7f;
                    var labelOffset = new Vector2()
                    {
                        X = uiPosition.X > Width / 2f
                            ? -labelDimensions.X - blipSize // right align the text to left of the blip
                            : blipSize, // left align the text to the right of the blip
                        Y = -labelDimensions.Y / 2f
                    };

                    handle.DrawString(Font, (uiPosition + labelOffset) * UIScale, labelText, UIScale, labelColor);
                    if (isMouseOver && !HideCoords)
                    {
                        var coordDimensions = handle.GetDimensions(Font, coordsText, 0.7f);
                        var coordOffset = new Vector2()
                        {
                            X = uiPosition.X > Width / 2f
                                ? -coordDimensions.X - blipSize / 0.7f // right align the text to left of the blip (0.7 needed for scale)
                                : blipSize, // left align the text to the right of the blip
                            Y = coordDimensions.Y / 2
                        };
                        handle.DrawString(Font, (uiPosition + coordOffset) * UIScale, coordsText, 0.7f * UIScale, coordColor);
                    }
                }

                NfAddBlipToList(blipDataList, isOutsideRadarCircle, uiPosition, uiXCentre, uiYCentre, labelColor); // Frontier code
                // End Frontier: IFF drawing functions
            }

            // Frontier Don't skip drawing blips if they're out of range.
            NfDrawBlips(handle, blipDataList);

            // Detailed view
            var gridAABB = curGridToWorld.TransformBox(grid.Comp.LocalAABB);

            // Skip drawing if it's out of range.
            if (!gridAABB.Intersects(viewAABB))
                continue;

            DrawGrid(handle, curGridToView, grid, labelColor);
            DrawDocks(handle, gUid, curGridToView);
        }

        // If we've set the controlling console, and it's on a different grid
        // to the shuttle itself, then draw an additional marker to help the
        // player determine where they are relative to the shuttle.
        if (_consoleEntity != null && xformQuery.TryGetComponent(_consoleEntity, out var consoleXform))
        {
            if (consoleXform.ParentUid != _coordinates.Value.EntityId)
            {
                var consolePositionWorld = _transform.GetWorldPosition((EntityUid)_consoleEntity);
                var p = Vector2.Transform(consolePositionWorld, worldToShuttle * shuttleToView);
                handle.DrawCircle(p, 5, Color.ToSrgb(Color.Cyan), true);
            }
        }

    }

    private void DrawDocks(DrawingHandleScreen handle, EntityUid uid, Matrix3x2 gridToView)
    {
        if (!ShowDocks)
            return;

        const float DockScale = 0.6f;
        var nent = EntManager.GetNetEntity(uid);

        const float sqrt2 = 1.41421356f;
        const float dockRadius = DockScale * sqrt2;
        // Worst-case bounds used to cull a dock:
        Box2 viewBounds = new Box2(-dockRadius, -dockRadius, PixelSize.X + dockRadius, PixelSize.Y + dockRadius); // Frontier: Size<PixelSize
        if (_docks.TryGetValue(nent, out var docks))
        {
            foreach (var state in docks)
            {
                var position = state.Coordinates.Position;

                var positionInView = Vector2.Transform(position, gridToView);
                if (!viewBounds.Contains(positionInView))
                {
                    continue;
                }

                //var color = Color.ToSrgb(Color.Magenta); // Frontier
                var color = Color.ToSrgb(state.HighlightedRadarColor); // Frontier

                var verts = new[]
                {
                    Vector2.Transform(position + new Vector2(-DockScale, -DockScale), gridToView),
                    Vector2.Transform(position + new Vector2(DockScale, -DockScale), gridToView),
                    Vector2.Transform(position + new Vector2(DockScale, DockScale), gridToView),
                    Vector2.Transform(position + new Vector2(-DockScale, DockScale), gridToView),
                };

                handle.DrawPrimitives(DrawPrimitiveTopology.TriangleFan, verts, color.WithAlpha(0.8f));
                handle.DrawPrimitives(DrawPrimitiveTopology.LineStrip, verts, color);
            }

            // Frontier: draw dock labels (done last to appear on top of all docks, still fights with other grids)
            var labeled = new HashSet<string>();
            foreach (var state in docks)
            {
                if (state.LabelName == null || labeled.Contains(state.LabelName))
                    continue;

                var position = state.Coordinates.Position;
                var uiPosition = Vector2.Transform(position, gridToView);

                if (!viewBounds.Contains(uiPosition))
                    continue;

                labeled.Add(state.LabelName);
                var labelDimensions = handle.GetDimensions(Font, state.LabelName, 1.0f);
                handle.DrawString(Font, (uiPosition / UIScale - labelDimensions / 2) * UIScale, state.LabelName, UIScale * 1.0f, _dockLabelColor);
            }
            // End Frontier
        }
    }

    private Vector2 InverseScalePosition(Vector2 value)
    {
        return (value - MidPointVector) / MinimapScale;
    }

    public class BlipData
    {
        public bool IsOutsideRadarCircle { get; set; }
        public Vector2 UiPosition { get; set; }
        public Vector2 VectorToPosition { get; set; }
        public Color Color { get; set; }
    }

    private const int RadarBlipSize = 15;
    private const int RadarFontSize = 10;

}
