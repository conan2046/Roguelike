using UnityEngine;

namespace ProjectX.Migration
{
    public sealed class CocosMainCharacterController : MonoBehaviour
    {
        [SerializeField] private CocosModelAniPlayer model;
        [SerializeField] private CocosMainCharacterData character = new CocosMainCharacterData();
        [SerializeField] private bool autoDirectionDemo;
        [SerializeField] private float directionInterval = 0.45f;
        [SerializeField] private int turnsBeforeStateToggle = 8;

        private float directionElapsed;
        private int turnCount;

        public CocosModelAniPlayer Model => model;
        public CocosMainCharacterData Character => character;

        public bool Configure(CocosMainCharacterData value, CocosModelFace face = CocosModelFace.Down, bool running = false)
        {
            character = value;
            if (model == null) model = GetComponentInChildren<CocosModelAniPlayer>(true);
            return model != null && character != null && model.Configure(
                character.professional,
                character.mountId,
                character.wingId,
                character.artifactId,
                face,
                running,
                character.weaponEffectId);
        }

        public void SetMoveState(bool running)
        {
            if (model != null) model.SetRunning(running);
        }

        public void SetDirection(CocosModelFace face)
        {
            if (model != null) model.SetFace(face);
        }

        private void Update()
        {
            if (!autoDirectionDemo || model == null || !model.IsLoaded || directionInterval <= 0f) return;
            directionElapsed += Time.unscaledDeltaTime;
            while (directionElapsed >= directionInterval)
            {
                directionElapsed -= directionInterval;
                SetDirection((CocosModelFace)(((int)model.Face + 1) % 8));
                turnCount++;
                if (turnsBeforeStateToggle > 0 && turnCount % turnsBeforeStateToggle == 0)
                    SetMoveState(!model.IsRunning);
            }
        }
    }
}
