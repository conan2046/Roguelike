using System;
using System.Collections.Generic;
using UnityEngine;

namespace ProjectX.Migration
{
    public enum CocosModelFace
    {
        RightDown = 0,
        Down = 1,
        LeftDown = 2,
        Left = 3,
        LeftUp = 4,
        Up = 5,
        RightUp = 6,
        Right = 7
    }

    [ExecuteAlways]
    public sealed class CocosModelAniPlayer : MonoBehaviour
    {
        private static readonly string[] ProfessionPrefixes = { "Z", "B", "Y", "H", "K", "J" };
        private static readonly int[] WingZOrders = { 0, 0, 0, 2, 2, 2, 2, 2 };

        [SerializeField] private int professional = 1;
        [SerializeField] private int weaponEffectId = 1;
        [SerializeField] private int mountId = 1;
        [SerializeField] private int wingId = 1;
        [SerializeField] private int artifactId = 1;
        [SerializeField] private CocosModelFace face = CocosModelFace.Down;
        [SerializeField] private bool running;
        [SerializeField] private bool playAutomatically = true;
        [SerializeField] private float speed = 1f;
        [SerializeField] private CocosAniPlayer rideLayer;
        [SerializeField] private CocosAniPlayer wingLayer;
        [SerializeField] private CocosAniPlayer bodyLayer;
        [SerializeField] private CocosAniPlayer weaponLayer;
        [SerializeField] private CocosAniPlayer artifactLayer;
        [SerializeField] private bool loaded;
        [SerializeField] private string loadError;
        [SerializeField] private int synchronizedTicks;

        private float bodyWingElapsed;
        private float rideElapsed;
        private float artifactElapsed;

        public bool IsLoaded => loaded;
        public string LoadError => loadError;
        public int LoadedLayerCount => CountLoadedLayers();
        public int SynchronizedTicks => synchronizedTicks;
        public int BodyZOrder => 1;
        public int WeaponZOrder => 1;
        public int RideZOrder => 2;
        public int WingZOrder => WingZOrders[(int)face];
        public int ArtifactZOrder => 10;
        public Vector2 WingAttachmentOffset => Vector2.zero;
        public int Professional => professional;
        public int WeaponEffectId => weaponEffectId;
        public int MountId => mountId;
        public int WingId => wingId;
        public int ArtifactId => artifactId;
        public CocosModelFace Face => face;
        public bool IsRunning => running;
        public bool UsesStandResources
        {
            get
            {
                if (!running) return true;
                return mountId > 0 && CocosMountData.LoadAll().TryGetValue(mountId, out CocosMountData mount) && mount.SharesStandAndRun;
            }
        }

        public static string ResolveStateSuffix(bool isRunning, bool sharesStandAndRun)
        {
            return !isRunning || sharesStandAndRun ? "zd" : "pb";
        }
        public CocosAniPlayer BodyLayer => bodyLayer;
        public CocosAniPlayer WeaponLayer => weaponLayer;
        public CocosAniPlayer RideLayer => rideLayer;
        public CocosAniPlayer WingLayer => wingLayer;
        public CocosAniPlayer ArtifactLayer => artifactLayer;

        public bool Configure(
            int valueProfessional,
            int valueMountId,
            int valueWingId,
            int valueArtifactId,
            CocosModelFace valueFace = CocosModelFace.Down,
            bool valueRunning = false,
            int valueWeaponEffectId = 1)
        {
            professional = Mathf.Clamp(valueProfessional, 1, ProfessionPrefixes.Length);
            weaponEffectId = Math.Max(0, valueWeaponEffectId);
            mountId = Math.Max(0, valueMountId);
            wingId = Math.Max(0, valueWingId);
            artifactId = Math.Max(0, valueArtifactId);
            face = valueFace;
            running = valueRunning;
            return LoadNow();
        }

        public bool LoadNow()
        {
            loaded = false;
            loadError = string.Empty;
            synchronizedTicks = 0;
            bodyWingElapsed = 0f;
            rideElapsed = 0f;
            artifactElapsed = 0f;
            try
            {
                EnsureLayers();
                Dictionary<int, CocosMountData> mounts = CocosMountData.LoadAll();
                CocosMountData mount = null;
                if (mountId > 0 && !mounts.TryGetValue(mountId, out mount))
                    throw new InvalidOperationException("Mount configuration id was not found: " + mountId);

                string prefix = ProfessionPrefixes[professional - 1];
                bool useStandResources = !running || (mount != null && mount.SharesStandAndRun);
                string stateSuffix = ResolveStateSuffix(running, mount != null && mount.SharesStandAndRun);

                ConfigureBody(prefix, mount, useStandResources, stateSuffix);
                ConfigureWeapon(prefix, mount, useStandResources, stateSuffix);
                ConfigureRide(mount, stateSuffix);
                ConfigureWing(prefix, mount, stateSuffix);
                ConfigureArtifact();
                ApplyFaceAndOrder();

                loaded = bodyLayer.IsLoaded &&
                         (weaponEffectId == 0 || weaponLayer.IsLoaded) &&
                         (mount == null || rideLayer.IsLoaded) &&
                         (wingId == 0 || wingLayer.IsLoaded) &&
                         (artifactId == 0 || artifactLayer.IsLoaded);
                if (!loaded)
                    throw new InvalidOperationException(BuildLayerError());
                return true;
            }
            catch (Exception exception)
            {
                loaded = false;
                loadError = exception.Message;
                Debug.LogError("Cocos model ANI load failed: " + exception, this);
                return false;
            }
        }

        public void SetFace(CocosModelFace value)
        {
            face = value;
            ApplyFaceAndOrder();
        }

        public void SetRunning(bool value)
        {
            if (running == value)
                return;
            running = value;
            LoadNow();
        }

        public void AdvanceSynchronizedFrame()
        {
            if (!loaded)
                return;
            Advance(bodyLayer);
            Advance(weaponLayer);
            Advance(rideLayer);
            Advance(wingLayer);
            Advance(artifactLayer);
            synchronizedTicks++;
        }

        private void Update()
        {
            if (!Application.isPlaying || !playAutomatically || !loaded || speed <= 0f)
                return;
            Tick(Time.unscaledDeltaTime * speed);
        }

        public void Tick(float deltaTime)
        {
            if (!loaded || deltaTime <= 0f)
                return;

            // Body, weapon and wing are entries of one ImodAnim. Wing is appended after body,
            // so when present its ANI supplies the shared _durTime exactly like ModelAni.
            CocosAniPlayer bodyWingClock = wingLayer != null && wingLayer.gameObject.activeSelf && wingLayer.IsLoaded
                ? wingLayer
                : weaponLayer != null && weaponLayer.gameObject.activeSelf && weaponLayer.IsLoaded
                    ? weaponLayer
                    : bodyLayer;
            bodyWingElapsed += deltaTime;
            int bodySafety = 0;
            while (bodyWingElapsed >= bodyWingClock.CurrentFrameDuration && bodySafety++ < 100)
            {
                bodyWingElapsed -= bodyWingClock.CurrentFrameDuration;
                Advance(bodyLayer);
                Advance(weaponLayer);
                Advance(wingLayer);
                synchronizedTicks++;
            }

            TickIndependentLayer(rideLayer, deltaTime, ref rideElapsed);
            TickIndependentLayer(artifactLayer, deltaTime, ref artifactElapsed);
        }

        private void EnsureLayers()
        {
            rideLayer = EnsureLayer(rideLayer, "RideLayer");
            wingLayer = EnsureLayer(wingLayer, "WingLayer");
            bodyLayer = EnsureLayer(bodyLayer, "BodyLayer");
            weaponLayer = EnsureLayer(weaponLayer, "WeaponLayer");
            artifactLayer = EnsureLayer(artifactLayer, "ArtifactLayer");
            foreach (CocosAniPlayer player in new[] { rideLayer, wingLayer, bodyLayer, weaponLayer, artifactLayer })
                player.SetPlaybackEnabled(false);
        }

        private CocosAniPlayer EnsureLayer(CocosAniPlayer current, string layerName)
        {
            if (current != null)
                return current;
            Transform child = transform.Find(layerName);
            if (child == null)
            {
                GameObject layer = new GameObject(layerName, typeof(RectTransform), typeof(CocosAniPlayer));
                RectTransform rect = layer.GetComponent<RectTransform>();
                rect.SetParent(transform, false);
                rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
                rect.sizeDelta = Vector2.zero;
                child = rect;
            }
            CocosAniPlayer result = child.GetComponent<CocosAniPlayer>();
            return result != null ? result : child.gameObject.AddComponent<CocosAniPlayer>();
        }

        private void ConfigureBody(string prefix, CocosMountData mount, bool stand, string suffix)
        {
            string ani;
            string png;
            if (mount == null)
            {
                string ending = stand ? "fd" : "pb";
                ani = png = "hero/" + prefix + "_0_" + ending;
            }
            else
            {
                ani = "Ride/" + prefix + "_0_" + mount.ani + suffix;
                png = "Ride/" + prefix + "_0_" + mount.png + suffix;
            }
            LoadLayer(bodyLayer, ani, png, FaceActionIndex());
        }

        private void ConfigureRide(CocosMountData mount, string suffix)
        {
            rideLayer.gameObject.SetActive(mount != null);
            if (mount == null)
                return;
            string path = "Ride/R_" + mount.ani + suffix;
            LoadLayer(rideLayer, path, path, FaceActionIndex());
        }

        private void ConfigureWeapon(string prefix, CocosMountData mount, bool stand, string suffix)
        {
            weaponLayer.gameObject.SetActive(weaponEffectId > 0);
            if (weaponEffectId <= 0) return;
            string ending = mount == null ? (stand ? "fd" : "pb") : suffix;
            string root = mount == null ? "hero/" : "Ride/";
            string basePath = root + prefix + "_" + weaponEffectId + "_d_";
            string ani = basePath + (mount == null ? string.Empty : mount.ani) + ending;
            string png = basePath + (mount == null ? string.Empty : mount.png) + ending;
            LoadLayer(weaponLayer, ani, png, FaceActionIndex());
        }

        private void ConfigureWing(string prefix, CocosMountData mount, string suffix)
        {
            wingLayer.gameObject.SetActive(wingId > 0);
            if (wingId <= 0)
                return;
            string mountAni = mount != null ? mount.ani : string.Empty;
            string ani = "Wings/" + prefix + "_" + wingId + "_" + mountAni + suffix;
            string png = "Wings/W_" + wingId + "_" + suffix;
            LoadLayer(wingLayer, ani, png, FaceActionIndex());
        }

        private void ConfigureArtifact()
        {
            artifactLayer.gameObject.SetActive(artifactId > 0);
            if (artifactId <= 0)
                return;
            string path = "shenqi/shenqi_move_" + artifactId;
            LoadLayer(artifactLayer, path, path, 0);
            RectTransform rect = artifactLayer.transform as RectTransform;
            rect.anchoredPosition = new Vector2(-30f, mountId > 0 ? 130f : 80f);
            rect.localScale = Vector3.one * 0.25f;
        }

        private static void LoadLayer(CocosAniPlayer layer, string ani, string png, int action)
        {
            if (!layer.ConfigureAndLoad(ani, png, action, true))
                throw new InvalidOperationException(layer.gameObject.name + ": " + layer.LoadError);
            layer.SetPlaybackEnabled(false);
        }

        private void ApplyFaceAndOrder()
        {
            int action = FaceActionIndex();
            SetAction(bodyLayer, action);
            SetAction(weaponLayer, action);
            SetAction(rideLayer, action);
            SetAction(wingLayer, action);
            SetAction(artifactLayer, 0);

            bool flip = FaceFlipped();
            ApplyFlip(bodyLayer, flip, 1f);
            ApplyFlip(weaponLayer, flip, 1f);
            ApplyFlip(rideLayer, flip, 1f);
            ApplyFlip(wingLayer, flip, 1f);
            ApplyFlip(artifactLayer, false, 0.25f);
            // ModelAni adds body and wing to one ImodAnim with Offset(0,0).
            // Ride is also created at (0,0); only the artifact has an explicit offset.
            WriteOffset(bodyLayer, Vector2.zero);
            WriteOffset(weaponLayer, Vector2.zero);
            WriteOffset(rideLayer, Vector2.zero);
            WriteOffset(wingLayer, Vector2.zero);
            WriteOffset(artifactLayer, new Vector2(-30f, mountId > 0 ? 130f : 80f));

            rideLayer.transform.SetSiblingIndex(0);
            if (WingZOrder <= BodyZOrder)
            {
                wingLayer.transform.SetSiblingIndex(1);
                bodyLayer.transform.SetSiblingIndex(2);
                weaponLayer.transform.SetSiblingIndex(3);
            }
            else
            {
                bodyLayer.transform.SetSiblingIndex(1);
                weaponLayer.transform.SetSiblingIndex(2);
                wingLayer.transform.SetSiblingIndex(3);
            }
            artifactLayer.transform.SetSiblingIndex(4);
        }

        private static void WriteOffset(CocosAniPlayer player, Vector2 value)
        {
            RectTransform rect = player != null ? player.transform as RectTransform : null;
            if (rect != null) rect.anchoredPosition = value;
        }

        private int FaceActionIndex()
        {
            switch (face)
            {
                case CocosModelFace.Down: return 1;
                case CocosModelFace.Up: return 2;
                case CocosModelFace.LeftUp:
                case CocosModelFace.RightUp: return 3;
                case CocosModelFace.Left:
                case CocosModelFace.Right: return 4;
                default: return 0;
            }
        }

        private bool FaceFlipped()
        {
            return face == CocosModelFace.LeftDown ||
                   face == CocosModelFace.Left ||
                   face == CocosModelFace.LeftUp;
        }

        private static void SetAction(CocosAniPlayer player, int action)
        {
            if (player != null && player.gameObject.activeSelf && player.IsLoaded)
                player.SetAction(action);
        }

        private static void ApplyFlip(CocosAniPlayer player, bool flip, float scale)
        {
            if (player == null)
                return;
            player.transform.localScale = new Vector3(flip ? -scale : scale, scale, 1f);
        }

        private static void Advance(CocosAniPlayer player)
        {
            if (player != null && player.gameObject.activeSelf && player.IsLoaded)
                player.AdvanceOneFrame();
        }

        private static void TickIndependentLayer(CocosAniPlayer player, float deltaTime, ref float layerElapsed)
        {
            if (player == null || !player.gameObject.activeSelf || !player.IsLoaded)
                return;
            layerElapsed += deltaTime;
            int safety = 0;
            while (layerElapsed >= player.CurrentFrameDuration && safety++ < 100)
            {
                layerElapsed -= player.CurrentFrameDuration;
                player.AdvanceOneFrame();
            }
        }

        private int CountLoadedLayers()
        {
            int count = 0;
            foreach (CocosAniPlayer layer in new[] { bodyLayer, weaponLayer, rideLayer, wingLayer, artifactLayer })
            {
                if (layer != null && layer.gameObject.activeSelf && layer.IsLoaded)
                    count++;
            }
            return count;
        }

        private string BuildLayerError()
        {
            List<string> errors = new List<string>();
            foreach (CocosAniPlayer layer in new[] { bodyLayer, weaponLayer, rideLayer, wingLayer, artifactLayer })
            {
                if (layer != null && layer.gameObject.activeSelf && !layer.IsLoaded)
                    errors.Add(layer.gameObject.name + "=" + layer.LoadError);
            }
            return string.Join("; ", errors);
        }
    }
}
