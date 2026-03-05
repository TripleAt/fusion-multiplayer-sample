using Fusion;
using UnityEngine;

namespace Project.Scripts.Photon
{
    /// <summary>
    /// ライフルの射撃を管理するコンポーネント。
    /// FirePoint（銃口）から弾丸を生成する。
    /// NetworkThirdPersonController から呼び出される。
    /// </summary>
    public sealed class Sample2WeaponController : MonoBehaviour
    {
        [Header("Weapon")]
        [Tooltip("弾丸プレハブ（NetworkObject付き）")]
        public NetworkPrefabRef BulletPrefab;

        [Tooltip("弾丸の発射位置（銃口）")]
        [SerializeField]
        public Transform FirePoint;

        /// <summary>
        /// 弾丸を生成する。
        /// FixedUpdateNetwork（サーバー権限）から呼び出されること。
        /// </summary>
        public void Fire(NetworkRunner runner, PlayerRef inputAuthority)
        {
            if (FirePoint == null) return;

            // 発射位置はFirePoint（銃口）、方向はキャラクターの正面
            var forward = transform.forward;
            runner.Spawn(
                BulletPrefab,
                FirePoint.position,
                Quaternion.LookRotation(forward),
                inputAuthority);
        }
    }
}
