using Fusion;
using Fusion.Addons.SimpleKCC;
using UnityEngine;

namespace Project.Scripts.Photon
{
    /// <summary>
    /// プレイヤーのHP管理。被弾でHP減少、0で死亡→リスポーン。
    /// </summary>
    [RequireComponent(typeof(Sample2NetworkThirdPersonController))]
    public sealed class Sample2PlayerHealth : NetworkBehaviour
    {
        [Header("Health")]
        [SerializeField] private int maxHp = 100;
        [SerializeField] private float respawnDelay = 3f;

        [Header("References")]
        [SerializeField] private SimpleKCC kcc;
        [SerializeField] private Sample2NetworkThirdPersonController controller;

        [Header("Visual")]
        [SerializeField] private GameObject visual;

        [Networked] public int Hp { get; private set; }
        [Networked] private TickTimer RespawnTimer { get; set; }
        [Networked] public NetworkBool IsDead { get; private set; }

        public int MaxHp => maxHp;

        private Vector3 _spawnPosition;
        private bool _lastVisualState = true;

        public override void Spawned()
        {
            Hp = maxHp;
            IsDead = false;
            _spawnPosition = transform.position;
            _lastVisualState = true;
            SetControlEnabled(true);

            if (controller == null)
            {
                //Debug.LogError($"{nameof(Sample2PlayerHealth)} requires a {nameof(Sample2NetworkThirdPersonController)} reference.", this);
            }
        }

        public override void FixedUpdateNetwork()
        {
            if (!HasStateAuthority) return;
            if (!IsDead) return;
            if (!RespawnTimer.Expired(Runner)) return;

            Respawn();
        }

        /// <summary>
        /// ダメージを受ける。StateAuthority（サーバー）でのみ呼ぶこと。
        /// </summary>
        public void TakeDamage(int damage)
        {
            if (!HasStateAuthority) return;
            if (IsDead) return;

            Hp = Mathf.Max(0, Hp - damage);
            if (Hp > 0)
            {
                return;
            }

            Die();
        }

        private void Die()
        {
            IsDead = true;
            RespawnTimer = TickTimer.CreateFromSeconds(Runner, respawnDelay);
            SetControlEnabled(false);
        }

        private void Respawn()
        {
            Hp = maxHp;
            IsDead = false;
            SetControlEnabled(true);

            var offset = new Vector3(Random.Range(-5f, 5f), 0f, Random.Range(-5f, 5f));
            var respawnPos = _spawnPosition + offset;

            if (kcc != null)
            {
                kcc.SetPosition(respawnPos);
                return;
            }

            transform.position = respawnPos;
        }

        public override void Render()
        {
            if (visual == null) return;

            var shouldBeActive = !IsDead;
            if (_lastVisualState == shouldBeActive)
            {
                return;
            }
            visual.SetActive(shouldBeActive);
            _lastVisualState = shouldBeActive;
        }

        private void OnGUI()
        {
            if (!HasInputAuthority) return;

            const float barWidth = 200f;
            const float barHeight = 20f;
            const float x = 10f;
            var y = Screen.height - 40f;

            var hpRatio = MaxHp > 0 ? (float)Hp / MaxHp : 0f;

            GUI.color = Color.black;
            GUI.DrawTexture(new Rect(x, y, barWidth, barHeight), Texture2D.whiteTexture);

            GUI.color = hpRatio > 0.3f ? Color.green : Color.red;
            GUI.DrawTexture(new Rect(x + 2, y + 2, (barWidth - 4f) * hpRatio, barHeight - 4f), Texture2D.whiteTexture);

            GUI.color = Color.white;
            var style = new GUIStyle(GUI.skin.label)
            {
                fontSize = 14,
                fontStyle = FontStyle.Bold
            };
            GUI.Label(new Rect(x + 5f, y, barWidth, barHeight), $"HP: {Hp} / {MaxHp}", style);

            if (!IsDead) return;

            var deathStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 40,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter
            };
            GUI.color = Color.red;
            GUI.Label(
                new Rect(0f, Screen.height / 2f - 30f, Screen.width, 60f),
                "DEAD - Respawning...",
                deathStyle);
        }

        private void SetControlEnabled(bool isEnabled)
        {
            if (controller == null) return;

            controller.SetControlEnabled(isEnabled);
        }
    }
}
