using UnityEngine;

namespace ProjectX.Migration
{
    public sealed class CocosMapCharacterMotor : MonoBehaviour
    {
        public const float CocosTileSize = 32f;

        [SerializeField] private CocosMainCharacterController character;
        [SerializeField] private RectTransform movementRoot;
        [SerializeField] private float movementSpeed = 220f;
        [SerializeField] private Vector2 movementBounds = new Vector2(900f, 550f);
        [SerializeField] private bool keyboardInputEnabled = true;
        [SerializeField] private CocosMapCollisionGrid collisionGrid;
        [SerializeField] private bool isMoving;
        [SerializeField] private Vector2 lastCocosDirection;

        public CocosMainCharacterController Character => character;
        public RectTransform MovementRoot => movementRoot;
        public float MovementSpeed => movementSpeed;
        public bool KeyboardInputEnabled => keyboardInputEnabled;
        public CocosMapCollisionGrid CollisionGrid => collisionGrid;
        public bool IsMoving => isMoving;
        public Vector2 LastCocosDirection => lastCocosDirection;

        public void Configure(CocosMainCharacterController valueCharacter, RectTransform valueRoot, float valueSpeed, Vector2 valueBounds, bool enableKeyboard)
        {
            character = valueCharacter;
            movementRoot = valueRoot != null ? valueRoot : transform as RectTransform;
            movementSpeed = Mathf.Max(0f, valueSpeed);
            movementBounds = new Vector2(Mathf.Abs(valueBounds.x), Mathf.Abs(valueBounds.y));
            keyboardInputEnabled = enableKeyboard;
        }

        public void SetKeyboardInputEnabled(bool value)
        {
            keyboardInputEnabled = value;
        }

        public void ConfigureCollision(CocosMapCollisionGrid value)
        {
            collisionGrid = value;
        }

        private void Awake()
        {
            if (movementRoot == null) movementRoot = transform as RectTransform;
            if (character == null) character = GetComponent<CocosMainCharacterController>();
        }

        private void Update()
        {
            if (!keyboardInputEnabled) return;
            Step(new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical")), Time.deltaTime);
        }

        public void Step(Vector2 input, float deltaTime)
        {
            Vector2 cocosDirection = QuantizeInput(input);
            bool nextMoving = cocosDirection != Vector2.zero && deltaTime > 0f && movementSpeed > 0f;
            if (!nextMoving)
            {
                if (isMoving && character != null) character.SetMoveState(false);
                isMoving = false;
                return;
            }

            CocosModelFace face = FaceFromDirection(cocosDirection);
            if (character != null)
            {
                if (!isMoving)
                {
                    character.SetDirection(face);
                    character.SetMoveState(true);
                }
                else if (character.Model.Face != face)
                {
                    character.SetDirection(face);
                }
            }

            isMoving = true;
            lastCocosDirection = cocosDirection;
            if (movementRoot != null)
            {
                Vector2 displacement = cocosDirection.normalized * movementSpeed * deltaTime;
                Vector2 next = collisionGrid != null
                    ? collisionGrid.ResolveMovement(movementRoot.anchoredPosition, displacement)
                    : movementRoot.anchoredPosition + displacement;
                movementRoot.anchoredPosition = collisionGrid != null
                    ? next
                    : new Vector2(Mathf.Clamp(next.x, -movementBounds.x, movementBounds.x), Mathf.Clamp(next.y, -movementBounds.y, movementBounds.y));
            }
        }

        public static Vector2 CalcMoveDirection(Vector2 source, Vector2 destination)
        {
            Vector2 direction = new Vector2(
                (int)(destination.x / CocosTileSize) - (int)(source.x / CocosTileSize),
                (int)(destination.y / CocosTileSize) - (int)(source.y / CocosTileSize));
            direction.x = Mathf.Clamp(direction.x, -1f, 1f);
            direction.y = Mathf.Clamp(direction.y, -1f, 1f);
            if (direction == Vector2.zero)
            {
                direction.x = destination.x == source.x ? 0f : destination.x < source.x ? -1f : 1f;
                direction.y = destination.y == source.y ? 0f : destination.y < source.y ? -1f : 1f;
            }
            return direction;
        }

        public static Vector2 QuantizeInput(Vector2 input)
        {
            return new Vector2(input.x == 0f ? 0f : input.x < 0f ? -1f : 1f, input.y == 0f ? 0f : input.y < 0f ? -1f : 1f);
        }

        public static CocosModelFace FaceFromDirection(Vector2 direction)
        {
            Vector2 value = QuantizeInput(direction);
            if (value == new Vector2(1f, -1f)) return CocosModelFace.RightDown;
            if (value == new Vector2(0f, -1f)) return CocosModelFace.Down;
            if (value == new Vector2(-1f, -1f)) return CocosModelFace.LeftDown;
            if (value == new Vector2(-1f, 0f)) return CocosModelFace.Left;
            if (value == new Vector2(-1f, 1f)) return CocosModelFace.LeftUp;
            if (value == new Vector2(0f, 1f)) return CocosModelFace.Up;
            if (value == new Vector2(1f, 1f)) return CocosModelFace.RightUp;
            return CocosModelFace.Right;
        }
    }
}
