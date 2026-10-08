using System;
using System.IO;
using Assets.Scripts.Game.Combat.ConstantAttacks;
using Assets.Scripts.Inventory__Items__Pickups.Weapons.Attacks;
using UnityEngine;

namespace MegabonkCoop
{
    // Visual copies of the "constant" weapons (aura ring, aegis shield) on the friend's model.
    //  sender  : once a second, looks for my own CombatAura / AegisAttack and reports: has it, radius ratio, shield active;
    //  receiver: instantiates the weapon's projectile prefab (scripts stripped) as a child of the friend's ghost.
    // EXPERIMENTAL: the exact prefab layout is not verified visually. Everything is guarded; on failure nothing is drawn.
    public static class ConstSync
    {
        // ---- sender
        static float nextSend;
        public static void SenderTick(float now)
        {
            if (!Plugin.SyncProjectiles.Value || now < nextSend || !Coop.Net.Connected) return;
            nextSend = now + 1f;
            byte flags = 0; float ratio = 1f;
            try
            {
                foreach (var a in UnityEngine.Object.FindObjectsOfType<CombatAura>())
                {
                    if (a == null || !a.gameObject.activeInHierarchy) continue;
                    flags |= 1;
                    ratio = a.defaultRadius > 0.01f ? a.radius / a.defaultRadius : 1f;
                    break;
                }
                var ae = UnityEngine.Object.FindObjectOfType<AegisAttack>();
                if (ae != null && ae.gameObject.activeInHierarchy) { flags |= 2; if (ae.isActive) flags |= 4; }
            }
            catch (Exception e) { if (!errLogged) { errLogged = true; Plugin.Info("[const] sender error: " + e.Message); } return; }
            float r = ratio; byte f = flags;
            Coop.Net.Send(Coop.Pack(Msg.Const, w => { w.Write(f); w.Write(r); }));
        }

        // ---- receiver state
        public static byte RemoteFlags; public static float RemoteRatio = 1f;
        static GameObject auraGo, aegisGo;
        static Vector3 auraBase = Vector3.one, aegisBase = Vector3.one;     // the prefab's own scale (the aura prefab is 5.5, not 1)
        static bool errLogged, auraFailed, aegisFailed;
        static GameObject holder;

        public static void Handle(BinaryReader br)
        {
            RemoteFlags = br.ReadByte();
            RemoteRatio = Mathf.Clamp(br.ReadSingle(), 0.1f, 6f);
        }

        public static void Reset()
        {
            if (auraGo != null) UnityEngine.Object.Destroy(auraGo);
            if (aegisGo != null) UnityEngine.Object.Destroy(aegisGo);
            auraGo = aegisGo = null; RemoteFlags = 0; auraFailed = aegisFailed = false;
        }

        static GameObject Build(EWeapon w, Transform parent, string tag)
        {
            var wd = DataManager.Instance.GetWeapon(w);
            if (wd == null || wd.attack == null) { Plugin.Info($"[const] no WeaponData/attack for {w}"); return null; }
            GameObject src = wd.attack;
            try { var wa = wd.attack.GetComponent<WeaponAttack>(); if (wa != null && wa.prefabProjectile != null) src = wa.prefabProjectile; } catch { }
            if (holder == null) { holder = new GameObject("CoopConstHolder"); holder.SetActive(false); UnityEngine.Object.DontDestroyOnLoad(holder); }
            var go = UnityEngine.Object.Instantiate(src, holder.transform);
            go.name = "Coop" + tag;
            foreach (var c in go.GetComponentsInChildren<Collider>(true)) UnityEngine.Object.Destroy(c);
            foreach (var r in go.GetComponentsInChildren<Rigidbody>(true)) UnityEngine.Object.Destroy(r);
            foreach (var m in go.GetComponentsInChildren<MonoBehaviour>(true)) UnityEngine.Object.Destroy(m);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = Vector3.zero;
            go.SetActive(true);
            int nr = 0, np = 0;
            try { nr = go.GetComponentsInChildren<Renderer>(true).Length; np = go.GetComponentsInChildren<ParticleSystem>(true).Length; } catch { }
            Plugin.Info($"[const] built {tag} from '{src.name}' renderers={nr} particleSystems={np} scale={go.transform.localScale}");
            return go;
        }

        // called every frame by ApplyRemoteVisual with the ghost root
        public static void Apply(Transform ghostRoot, bool show)
        {
            try
            {
                bool wantAura = show && (RemoteFlags & 1) != 0;
                if (wantAura && auraGo == null && ghostRoot != null && !auraFailed) { auraGo = Build(EWeapon.Aura, ghostRoot, "Aura"); if (auraGo == null) auraFailed = true; else auraBase = auraGo.transform.localScale; }
                if (auraGo != null)
                {
                    if (auraGo.activeSelf != wantAura) auraGo.SetActive(wantAura);
                    if (wantAura) auraGo.transform.localScale = auraBase * RemoteRatio;
                }
                bool wantAegis = show && (RemoteFlags & 2) != 0;
                if (wantAegis && aegisGo == null && ghostRoot != null && !aegisFailed) { aegisGo = Build(EWeapon.Aegis, ghostRoot, "Aegis"); if (aegisGo == null) aegisFailed = true; else aegisBase = aegisGo.transform.localScale; }
                if (aegisGo != null)
                {
                    bool on = wantAegis && (RemoteFlags & 4) != 0;
                    if (aegisGo.activeSelf != on) aegisGo.SetActive(on);
                }
            }
            catch (Exception e) { if (!errLogged) { errLogged = true; Plugin.Info("[const] apply error: " + e.Message); } }
        }
    }
}
