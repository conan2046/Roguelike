using System;

namespace Roguelike.Features.Combat.Runtime
{
    public sealed partial class CombatRuntimeRunner
    {
        private CombatTribulationUi tribulationUi;
        private CombatTribulationMarkers tribulationMarkers;
        public Action ReturnToLobby { get; set; }
        public UnityEngine.GameObject TribulationUiRoot => tribulationUi?.Root;
    }
}
