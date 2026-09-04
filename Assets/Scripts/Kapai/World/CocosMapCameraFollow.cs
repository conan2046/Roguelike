using UnityEngine;

namespace ProjectX.Migration
{
    public sealed class CocosMapCameraFollow : MonoBehaviour
    {
        [SerializeField] private Transform target;
        [SerializeField] private float damping = 8f;
        [SerializeField] private Vector3 offset = new Vector3(0f, 0f, -10f);
        [SerializeField] private bool clampToBounds;
        [SerializeField] private Rect worldBounds;

        public Transform Target => target;

        public void Configure(Transform valueTarget, float valueDamping, Vector3 valueOffset)
        {
            target = valueTarget;
            damping = Mathf.Max(0f, valueDamping);
            offset = valueOffset;
        }

        private void LateUpdate()
        {
            if (target == null) return;
            Vector3 destination = GetDestination();
            transform.position = damping <= 0f
                ? destination
                : Vector3.Lerp(transform.position, destination, 1f - Mathf.Exp(-damping * Time.deltaTime));
        }

        public void SnapNow()
        {
            if (target != null) transform.position = GetDestination();
        }

        public void ConfigureBounds(Rect valueWorldBounds)
        {
            worldBounds = valueWorldBounds;
            clampToBounds = valueWorldBounds.width > 0f && valueWorldBounds.height > 0f;
        }

        private Vector3 GetDestination()
        {
            Vector3 destination = target.position + offset;
            Camera cameraComponent = GetComponent<Camera>();
            if (!clampToBounds || cameraComponent == null || !cameraComponent.orthographic) return destination;
            float halfHeight = cameraComponent.orthographicSize;
            float halfWidth = halfHeight * cameraComponent.aspect;
            float minX = worldBounds.xMin + halfWidth;
            float maxX = worldBounds.xMax - halfWidth;
            float minY = worldBounds.yMin + halfHeight;
            float maxY = worldBounds.yMax - halfHeight;
            destination.x = minX <= maxX ? Mathf.Clamp(destination.x, minX, maxX) : worldBounds.center.x;
            destination.y = minY <= maxY ? Mathf.Clamp(destination.y, minY, maxY) : worldBounds.center.y;
            return destination;
        }
    }
}
