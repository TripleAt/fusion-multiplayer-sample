using Fusion;
using UnityEngine;

namespace Project.Scripts.Photon
{
    /// <summary>
    /// ネットワーク同期する弾丸。
    /// スポーン後、前方に直進し、一定時間で自動消滅する。
    /// FixedUpdateNetwork でシミュレーション位置を更新し、
    /// Render で毎フレーム補間して滑らかに描画する。
    /// </summary>
    public sealed class Sample2Bullet : NetworkBehaviour
    {
        [Header("Bullet Settings")]
        public float Speed = 50f;
        public float LifeTime = 3f;
        public int Damage = 25;

        [Networked] private TickTimer LifeTimer { get; set; }
        [Networked] private Vector3Compressed Direction { get; set; }
        [Networked] private Vector3Compressed SimPosition { get; set; }

        public override void Spawned()
        {
            Direction = transform.forward;
            SimPosition = transform.position;

            if (HasStateAuthority)
            {
                LifeTimer = TickTimer.CreateFromSeconds(Runner, LifeTime);
            }
        }

        public override void FixedUpdateNetwork()
        {
            // ティック毎のシミュレーション位置更新（ロールバック・再シミュレーション対応）
            SimPosition = (Vector3)SimPosition + (Vector3)Direction * Speed * Runner.DeltaTime;

            if (!HasStateAuthority) return;

            if (LifeTimer.Expired(Runner))
            {
                Runner.Despawn(Object);
            }
        }

        public override void Render()
        {
            // LocalAlpha: 0〜1 でティック間のレンダー位置を示す
            // SimPosition（最後のティック位置）からアルファ分だけ外挿
            var alpha = Runner.LocalAlpha;
            transform.position = (Vector3)SimPosition + (Vector3)Direction * Speed * alpha * Runner.DeltaTime;
        }

        private void OnTriggerEnter(Collider other)
        {
            if (!HasStateAuthority) return;

            // プレイヤー判定はタグではなく HP コンポーネント基準にする。
            var health = other.GetComponentInParent<Sample2PlayerHealth>();
            if (health != null)
            {
                var hitNetObj = health.Object;
                if (hitNetObj == null) return;

                // 自分の弾は自分に当たらない
                if (hitNetObj.InputAuthority == Object.InputAuthority) return;

                // ダメージを与える
                health.TakeDamage(Damage);

                Runner.Despawn(Object);
                return;
            }

            // 地形等に当たった場合も消滅
            Runner.Despawn(Object);
        }
    }
}
