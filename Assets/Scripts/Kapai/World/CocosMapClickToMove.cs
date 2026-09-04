using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

namespace ProjectX.Migration
{
    public sealed class CocosMapClickToMove : MonoBehaviour
    {
        private static readonly Vector2Int[] Neighbours =
        {
            new Vector2Int(-1, -1), new Vector2Int(0, -1), new Vector2Int(1, -1),
            new Vector2Int(-1, 0),                              new Vector2Int(1, 0),
            new Vector2Int(-1, 1),  new Vector2Int(0, 1),  new Vector2Int(1, 1)
        };

        [SerializeField] private RectTransform mapRoot;
        [SerializeField] private RectTransform playerRoot;
        [SerializeField] private CocosMapCharacterMotor motor;
        [SerializeField] private CocosMapCollisionGrid collisionGrid;
        [SerializeField] private Camera worldCamera;
        [SerializeField] private RectTransform targetMarker;
        [SerializeField] private bool hasPath;
        [SerializeField] private bool lastPathSucceeded;
        [SerializeField] private int pathNodeCount;
        [SerializeField] private Vector2 requestedTarget;
        [SerializeField] private Vector2 effectiveTarget;

        private readonly List<Vector2> visualPath = new List<Vector2>();
        private readonly List<RaycastResult> uiRaycastResults = new List<RaycastResult>();
        private int waypointIndex;

        public bool HasPath => hasPath;
        public bool LastPathSucceeded => lastPathSucceeded;
        public int PathNodeCount => pathNodeCount;
        public Vector2 RequestedTarget => requestedTarget;
        public Vector2 EffectiveTarget => effectiveTarget;
        public bool TargetMarkerVisible => targetMarker != null && targetMarker.gameObject.activeSelf;
        public IReadOnlyList<Vector2> VisualPath => visualPath;

        public void Configure(RectTransform valueMapRoot, RectTransform valuePlayerRoot, CocosMapCharacterMotor valueMotor,
            CocosMapCollisionGrid valueGrid, Camera valueCamera, RectTransform valueMarker)
        {
            mapRoot = valueMapRoot;
            playerRoot = valuePlayerRoot;
            motor = valueMotor;
            collisionGrid = valueGrid;
            worldCamera = valueCamera;
            targetMarker = valueMarker;
            EnsureMarkerBelowPlayer();
            if (targetMarker != null) targetMarker.gameObject.SetActive(false);
        }

        private void Update()
        {
            if (Input.GetMouseButtonDown(0) && TryHandleScreenClick(Input.mousePosition)) return;

            if (Input.GetAxisRaw("Horizontal") != 0f || Input.GetAxisRaw("Vertical") != 0f)
            {
                if (hasPath) CancelPath(true);
                return;
            }
            TickPath(Time.deltaTime);
        }

        public bool TryHandleScreenClick(Vector2 screenPoint)
        {
            if (IsScreenPointBlockedByUi(screenPoint) || !TryScreenPointToCocos(screenPoint, out Vector2 cocosPosition))
                return false;
            RequestMoveToCocos(cocosPosition);
            TickPath(Time.deltaTime);
            return true;
        }

        public bool IsScreenPointBlockedByUi(Vector2 screenPoint)
        {
            EventSystem eventSystem = EventSystem.current;
            if (eventSystem == null) return false;

            uiRaycastResults.Clear();
            var pointer = new PointerEventData(eventSystem) { position = screenPoint };
            eventSystem.RaycastAll(pointer, uiRaycastResults);
            foreach (RaycastResult hit in uiRaycastResults)
            {
                if (hit.gameObject == null) continue;
                Transform hitTransform = hit.gameObject.transform;
                if (mapRoot != null && (hitTransform == mapRoot || hitTransform.IsChildOf(mapRoot))) continue;
                return true;
            }
            return false;
        }

        public bool TryScreenPointToCocos(Vector2 screenPoint, out Vector2 cocosPosition)
        {
            cocosPosition = default;
            Camera camera = worldCamera != null ? worldCamera : Camera.main;
            if (mapRoot == null || collisionGrid == null ||
                !RectTransformUtility.ScreenPointToLocalPointInRectangle(mapRoot, screenPoint, camera, out Vector2 local))
                return false;
            if (local.x < 0f || local.y < 0f || local.x >= mapRoot.rect.width || local.y >= mapRoot.rect.height)
                return false;
            cocosPosition = collisionGrid.VisualToCocos(local);
            return true;
        }

        public bool RequestMoveToCocos(Vector2 target)
        {
            CancelPath(false);
            requestedTarget = target;
            if (collisionGrid == null || playerRoot == null || motor == null || !collisionGrid.IsLoaded)
                return false;

            effectiveTarget = collisionGrid.FindNearestWalkableCocosPosition(target);
            Vector2 startCocos = collisionGrid.VisualToCocos(playerRoot.anchoredPosition);
            bool found = TryBuildPath(startCocos, effectiveTarget, visualPath);
            lastPathSucceeded = found;
            pathNodeCount = visualPath.Count;
            waypointIndex = 0;
            hasPath = found && visualPath.Count > 0;
            if (targetMarker != null)
            {
                EnsureMarkerBelowPlayer();
                targetMarker.anchoredPosition = collisionGrid.CocosToVisual(effectiveTarget);
                targetMarker.gameObject.SetActive(found);
            }
            motor.SetKeyboardInputEnabled(!hasPath);
            return found;
        }

        public void TickPath(float deltaTime)
        {
            if (!hasPath || deltaTime <= 0f || waypointIndex >= visualPath.Count) return;
            Vector2 current = playerRoot.anchoredPosition;
            Vector2 target = visualPath[waypointIndex];
            Vector2 delta = target - current;
            float distance = delta.magnitude;
            if (distance <= 0.5f)
            {
                playerRoot.anchoredPosition = target;
                waypointIndex++;
                if (waypointIndex >= visualPath.Count) FinishPath();
                return;
            }

            float stepTime = Mathf.Min(deltaTime, distance / Mathf.Max(0.001f, motor.MovementSpeed));
            motor.Step(delta, stepTime);
            if (Vector2.Distance(playerRoot.anchoredPosition, target) <= 0.5f)
            {
                playerRoot.anchoredPosition = target;
                waypointIndex++;
                if (waypointIndex >= visualPath.Count) FinishPath();
            }
        }

        public void CancelPath(bool hideMarker)
        {
            hasPath = false;
            visualPath.Clear();
            waypointIndex = 0;
            pathNodeCount = 0;
            if (motor != null)
            {
                motor.Step(Vector2.zero, 0f);
                motor.SetKeyboardInputEnabled(true);
            }
            if (hideMarker && targetMarker != null) targetMarker.gameObject.SetActive(false);
        }

        public bool TryBuildPath(Vector2 startCocos, Vector2 targetCocos, List<Vector2> outputVisualPath)
        {
            outputVisualPath.Clear();
            int width = collisionGrid.GridWidth;
            int height = collisionGrid.GridHeight;
            Vector2Int start = ToTile(startCocos, width, height);
            Vector2Int goal = ToTile(targetCocos, width, height);
            if (!collisionGrid.CanWalkTile(start.x, start.y) || !collisionGrid.CanWalkTile(goal.x, goal.y)) return false;
            if (start == goal)
            {
                outputVisualPath.Add(collisionGrid.CocosToVisual(TileCenter(goal)));
                return true;
            }

            int total = width * height;
            int[] g = new int[total];
            int[] parent = new int[total];
            bool[] open = new bool[total];
            bool[] closed = new bool[total];
            for (int i = 0; i < total; i++) { g[i] = int.MaxValue; parent[i] = -1; }
            int startIndex = Index(start.x, start.y, width);
            int goalIndex = Index(goal.x, goal.y, width);
            g[startIndex] = 0;
            open[startIndex] = true;

            while (true)
            {
                int current = -1;
                int bestF = int.MaxValue;
                for (int i = 0; i < total; i++)
                {
                    if (!open[i]) continue;
                    int x = i % width;
                    int y = i / width;
                    int f = g[i] + 10 * (Mathf.Abs(x - goal.x) + Mathf.Abs(y - goal.y));
                    if (f < bestF) { bestF = f; current = i; }
                }
                if (current < 0) return false;
                if (current == goalIndex) break;
                open[current] = false;
                closed[current] = true;
                int currentX = current % width;
                int currentY = current / width;

                foreach (Vector2Int direction in Neighbours)
                {
                    int nextX = currentX + direction.x;
                    int nextY = currentY + direction.y;
                    if (!collisionGrid.CanWalkTile(nextX, nextY)) continue;
                    if (direction.x != 0 && direction.y != 0 &&
                        (!collisionGrid.CanWalkTile(currentX + direction.x, currentY) ||
                         !collisionGrid.CanWalkTile(currentX, currentY + direction.y))) continue;
                    int next = Index(nextX, nextY, width);
                    if (closed[next]) continue;
                    int candidate = g[current] + (direction.x != 0 && direction.y != 0 ? 14 : 10);
                    if (!open[next] || candidate < g[next])
                    {
                        g[next] = candidate;
                        parent[next] = current;
                        open[next] = true;
                    }
                }
            }

            List<Vector2> reversed = new List<Vector2>();
            for (int node = goalIndex; node != startIndex; node = parent[node])
            {
                if (node < 0) return false;
                reversed.Add(collisionGrid.CocosToVisual(TileCenter(new Vector2Int(node % width, node / width))));
            }
            for (int i = reversed.Count - 1; i >= 0; i--) outputVisualPath.Add(reversed[i]);
            return outputVisualPath.Count > 0;
        }

        private void FinishPath()
        {
            hasPath = false;
            motor.Step(Vector2.zero, 0f);
            motor.SetKeyboardInputEnabled(true);
            if (targetMarker != null) targetMarker.gameObject.SetActive(false);
        }

        private void EnsureMarkerBelowPlayer()
        {
            if (targetMarker == null || playerRoot == null || targetMarker.parent != playerRoot.parent) return;
            int playerIndex = playerRoot.GetSiblingIndex();
            if (targetMarker.GetSiblingIndex() > playerIndex)
                targetMarker.SetSiblingIndex(playerIndex);
        }

        private static int Index(int x, int y, int width) => y * width + x;

        private static Vector2Int ToTile(Vector2 value, int width, int height)
        {
            return new Vector2Int(
                Mathf.Clamp((int)(value.x / CocosMapCharacterMotor.CocosTileSize), 0, width - 1),
                Mathf.Clamp((int)(value.y / CocosMapCharacterMotor.CocosTileSize), 0, height - 1));
        }

        private static Vector2 TileCenter(Vector2Int tile)
        {
            return new Vector2(
                (tile.x + 0.5f) * CocosMapCharacterMotor.CocosTileSize,
                (tile.y + 0.5f) * CocosMapCharacterMotor.CocosTileSize);
        }
    }
}
