using Cysharp.Threading.Tasks;
using Fusion;
using Fusion.Addons.SimpleKCC;
using Project.Animation;
using StarterAssets;
using Unity.Cinemachine;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Project.Scripts.Photon
{
    /// <summary>
    /// Sample2: Fusion + SimpleKCC によるネットワーク対応三人称キャラクターコントローラー。
    /// 上半身ブレンド（ライフルアイドル/発射）に対応。
    /// Sample1 の設計に準拠し、IBeforeTick による入力管理、[Networked] _moveVelocity による
    /// 補間、コヨーテタイム等を実装。
    /// </summary>
    public sealed class Sample2NetworkThirdPersonController : NetworkBehaviour, IBeforeTick
    {
        [Header("SimpleKCC")] [SerializeField] private SimpleKCC kcc;

        [Header("Player")] [Tooltip("Move speed of the character in m/s")]
        public float MoveSpeed = 2.0f;

        [Tooltip("Sprint speed of the character in m/s")]
        public float SprintSpeed = 5.335f;

        [Tooltip("How fast the character turns to face movement direction")] [Range(0.0f, 0.3f)]
        public float RotationSmoothTime = 0.12f;

        [Tooltip("How fast the character reaches target speed")]
        public float Acceleration = 10f;

        [Tooltip("How fast the character stops")]
        public float Deceleration = 15f;

        public AudioClip LandingAudioClip;
        public AudioClip[] FootstepAudioClips;
        [Range(0, 1)] public float FootstepAudioVolume = 0.5f;

        [Space(10)] [Tooltip("The height the player can jump")]
        public float JumpHeight = 1.2f;

        [Tooltip("The character uses its own gravity value. The engine default is -9.81f")]
        public float Gravity = -15.0f;

        [Space(10)] [Tooltip("Time required to pass before being able to jump again. Set to 0f to instantly jump again")]
        public float JumpTimeout = 0.50f;

        [Tooltip("Time required to pass before entering the fall state. Useful for walking down stairs")]
        public float FallTimeout = 0.15f;

        [Tooltip("How far in degrees can you move the camera up")]
        public float TopClamp = 70.0f;

        [Tooltip("How far in degrees can you move the camera down")]
        public float BottomClamp = -30.0f;

        [Tooltip("Additional degrees to override the camera. Useful for fine tuning camera position when locked")]
        public float CameraAngleOverride = 0.0f;

        [Tooltip("For locking the camera position on all axis")]
        public bool LockCameraPosition = false;

        [Header("Camera")] [SerializeField] private Transform _playerCameraRoot;

        [Header("Animation Clips")]
        [SerializeField] private Animator targetAnimator;
        public AnimationClip IdleClip;
        public AnimationClip WalkClip;
        public AnimationClip RunClip;
        public AnimationClip JumpClip;
        public AnimationClip FallClip;
        public AnimationClip LandClip;

        [Header("Sample2 Upper Body")]
        public AvatarMask UpperBodyMask;
        public AvatarMask FiringUpperBodyMask;
        public AnimationClip RifleIdleClip;
        public AnimationClip FiringRifleClip;
        public bool EnableUpperBodyPose = true;
        public bool DebugFire;

        [Header("Weapon")]
        [Tooltip("WeaponController コンポーネント（プレイヤーにアタッチ）")]
        public Sample2WeaponController WeaponController;

        [Header("References")]
        [SerializeField] private PlayerInput playerInput;
        [SerializeField] private StarterAssetsInputs localInput;

        // --- Networked state (ロールバック・再シミュレーション対応) ---
        [Networked] private FloatCompressed JumpTimeoutDelta { get; set; }
        [Networked] private Vector3Compressed _moveVelocity { get; set; }
        [Networked] private NetworkBool PreviousFirePressed { get; set; }
        [Networked] private ushort FireAnimationSequence { get; set; }

        // --- Input / Simulation state (BeforeTick / FixedUpdateNetwork で毎ティック再計算されるため [Networked] 不要) ---
        private Sample2NetworkInputData _currentInput;
        private float _coyoteTime;

        // --- Local state (ビジュアル・カメラ用、ネットワーク同期不要) ---
        private float _cinemachineTargetYaw;
        private float _cinemachineTargetPitch;
        private Sample2CharacterAnim _characterAnim;
        private bool _previousDebugFire;
        private int _lastProcessedFireAnimationSequence;
        private bool _canControl = true;

        private const float Threshold = 0.01f;
        private const float CoyoteTimeDuration = 0.15f;

        private bool IsCurrentDeviceMouse => playerInput != null && playerInput.currentControlScheme == "KeyboardMouse";

        public override void Spawned()
        {
            JumpTimeoutDelta = JumpTimeout;
            _moveVelocity = Vector3.zero;
            _currentInput = default;
            _lastProcessedFireAnimationSequence = FireAnimationSequence;
            _canControl = true;
            kcc.SetGravity(Gravity);

            var kccCollider = transform.Find("KCCCollider");
            if (kccCollider != null)
            {
                kccCollider.gameObject.tag = "Player";
            }

            if (HasInputAuthority)
            {
                if (_playerCameraRoot != null)
                {
                    _cinemachineTargetYaw = _playerCameraRoot.rotation.eulerAngles.y;

                    var cinemachineCamera = FindFirstObjectByType<CinemachineCamera>();
                    if (cinemachineCamera != null)
                    {
                        cinemachineCamera.Follow = _playerCameraRoot;
                    }
                }

                InitializeAnimationAsync().Forget();
                return;
            }

            if (playerInput != null) playerInput.enabled = false;
            if (localInput != null) localInput.enabled = false;

            InitializeAnimationAsync().Forget();
        }

        /// <summary>
        /// FixedUpdate の前に毎ティック呼ばれる。
        /// GetInput が失敗した場合は前回の入力が再利用される。
        /// </summary>
        void IBeforeTick.BeforeTick()
        {
            if (Object == null) return;

            if (Object.InputAuthority == PlayerRef.None)
            {
                return;
            }

            if (GetInput(out Sample2NetworkInputData input))
            {
                _currentInput = input;
            }
        }

        /// <summary>
        /// Fusion シミュレーションループ。
        /// _currentInput は BeforeTick で確定済み。
        /// </summary>
        public override void FixedUpdateNetwork()
        {
            if (!HasStateAuthority && !HasInputAuthority) return;
            if (!_canControl) return;

            var moveDir = _currentInput.MoveDirection;
            var isSprinting = _currentInput.Buttons.IsSet(Sample2ButtonFlag.Sprint);
            var jumpPressed = _currentInput.Buttons.IsSet(Sample2ButtonFlag.Jump);
            var firePressed = _currentInput.Buttons.IsSet(Sample2ButtonFlag.Fire);

            // 発射アニメーションシーケンス管理
            if (firePressed && !PreviousFirePressed)
            {
                FireAnimationSequence++;

                // サーバー権限で弾丸を発射
                if (HasStateAuthority && WeaponController != null)
                {
                    WeaponController.Fire(Runner, Object.InputAuthority);
                }
            }
            PreviousFirePressed = firePressed;

            var desiredMoveVelocity = CalcKinematicVelocity(moveDir, isSprinting);

            if (kcc.ProjectOnGround(desiredMoveVelocity, out var projectedVelocity))
            {
                desiredMoveVelocity = Vector3.Normalize(projectedVelocity) * desiredMoveVelocity.magnitude;
            }

            var acceleration = desiredMoveVelocity == Vector3.zero ? Deceleration : Acceleration;
            _moveVelocity = Vector3.Lerp(_moveVelocity, desiredMoveVelocity, acceleration * Runner.DeltaTime);

            UpdateCharacterRotation(moveDir);
            var jump = CalculateJumpImpulse(jumpPressed);
            kcc.Move(_moveVelocity, jump);
        }

        private Vector3 CalcKinematicVelocity(Vector3 moveDir, bool isSprinting)
        {
            var inputMagnitude = Mathf.Clamp01(moveDir.magnitude);
            if (inputMagnitude < Threshold) return Vector3.zero;

            var speed = isSprinting ? SprintSpeed : MoveSpeed;
            return moveDir.normalized * (speed * inputMagnitude);
        }

        private void UpdateCharacterRotation(Vector3 moveDir)
        {
            if (moveDir.magnitude < Threshold) return;

            var targetYaw = Mathf.Atan2(moveDir.x, moveDir.z) * Mathf.Rad2Deg;
            var currentYaw = kcc.GetLookRotation(false, true).y;
            var t = 1f - Mathf.Exp(-Runner.DeltaTime / Mathf.Max(RotationSmoothTime, 0.001f));
            var smoothYaw = Mathf.LerpAngle(currentYaw, targetYaw, t);
            kcc.SetLookRotation(0f, smoothYaw);
        }

        /// <summary>
        /// 描画フレームごとに呼ばれる。アニメーション更新を行う。
        /// </summary>
        public override void Render()
        {
            // アニメーションは全プレイヤーで更新
            UpdateAnimationState();
        }

        /// <summary>
        /// カメラ制御は LateUpdate で行う（元の ThirdPersonController と同じタイミング）。
        /// CinemachineBrain が LateUpdate でカメラ位置を計算するため、
        /// その前に PlayerCameraRoot の回転を確定させる必要がある。
        /// </summary>
        private void LateUpdate()
        {
            if (!HasInputAuthority) return;
            CameraRotation();
        }

        /// <summary>
        /// ジャンプ衝力を計算し、タイムアウトを更新する。
        /// 壁接触時に IsGrounded が一瞬 false になる問題を
        /// コヨーテタイムで吸収する。
        /// </summary>
        private float CalculateJumpImpulse(bool jumpPressed)
        {
            _coyoteTime = kcc.IsGrounded ? CoyoteTimeDuration : Mathf.Max(0f, _coyoteTime - Runner.DeltaTime);

            if (_coyoteTime <= 0f) return 0f;

            JumpTimeoutDelta = Mathf.Max(0f, JumpTimeoutDelta - Runner.DeltaTime);

            if (!jumpPressed || JumpTimeoutDelta > 0f) return 0f;

            JumpTimeoutDelta = JumpTimeout;
            _coyoteTime = 0f;
            return Mathf.Sqrt(JumpHeight * -2f * Gravity);
        }

        private void CameraRotation()
        {
            if (localInput == null) return;

            if (localInput.look.sqrMagnitude >= Threshold && !LockCameraPosition)
            {
                // マウスは Time.deltaTime を掛けない
                var deltaTimeMultiplier = IsCurrentDeviceMouse ? 1.0f : Time.deltaTime;

                _cinemachineTargetYaw += localInput.look.x * deltaTimeMultiplier;
                _cinemachineTargetPitch += localInput.look.y * deltaTimeMultiplier;
            }

            _cinemachineTargetYaw = ClampAngle(_cinemachineTargetYaw, float.MinValue, float.MaxValue);
            _cinemachineTargetPitch = ClampAngle(_cinemachineTargetPitch, BottomClamp, TopClamp);

            if (_playerCameraRoot != null)
            {
                _playerCameraRoot.rotation = Quaternion.Euler(
                    _cinemachineTargetPitch + CameraAngleOverride,
                    _cinemachineTargetYaw,
                    0.0f);
            }
        }

        private void UpdateAnimationState()
        {
            if (_characterAnim == null)
            {
                return;
            }

            // 発射アニメーションのトリガー検知
            var fireTriggered = FireAnimationSequence != _lastProcessedFireAnimationSequence;
            if (fireTriggered)
            {
                _lastProcessedFireAnimationSequence = FireAnimationSequence;
            }

            var debugFireTriggered = DebugFire && !_previousDebugFire;
            _previousDebugFire = DebugFire;

            // kcc.RealSpeed はリシミュレーションの影響でブレるため、
            // Lerp 済みの _moveVelocity（[Networked] で補間される）から速度を算出する。
            var animSpeed = ((Vector3)_moveVelocity).magnitude;
            _characterAnim.UpdateSample2State(
                animSpeed,
                kcc.IsGrounded,
                kcc.HasJumped,
                Time.deltaTime,
                EnableUpperBodyPose,
                fireTriggered || debugFireTriggered);
        }

        private async UniTaskVoid InitializeAnimationAsync()
        {
            if (targetAnimator == null)
            {
                Debug.LogWarning("Sample2NetworkThirdPersonController: Animator is not assigned.");
                return;
            }

            if (IdleClip == null || WalkClip == null || RunClip == null ||
                JumpClip == null || FallClip == null || LandClip == null)
            {
                Debug.LogWarning("Sample2NetworkThirdPersonController: Base animation clips are not assigned.");
                return;
            }

            if (UpperBodyMask == null || RifleIdleClip == null || FiringRifleClip == null)
            {
                Debug.LogWarning("Sample2NetworkThirdPersonController: Upper-body animation setup is incomplete. Base locomotion will continue without rifle overlay.");
            }

            _characterAnim = new Sample2CharacterAnim(targetAnimator, new[] { IdleClip }, () => Time.timeScale);
            var clips = new ThirdPersonAnimationClips
            {
                Idle = IdleClip,
                Walk = WalkClip,
                Run = RunClip,
                Jump = JumpClip,
                Fall = FallClip,
                Land = LandClip
            };
            var upperBodyClips = new Sample2UpperBodyAnimationClips
            {
                RifleIdle = RifleIdleClip,
                FiringRifle = FiringRifleClip,
                UpperBodyMask = UpperBodyMask,
                FiringUpperBodyMask = FiringUpperBodyMask
            };

            var runSpeedThreshold = (MoveSpeed + SprintSpeed) * 0.5f;
            await _characterAnim.InitializeThirdPersonAsync(clips, runSpeedThreshold, FallTimeout, upperBodyClips);
        }

        private void OnFootstep(AnimationEvent animationEvent)
        {
            if (!(animationEvent.animatorClipInfo.weight > 0.5f))
            {
                return;
            }

            if (FootstepAudioClips == null || FootstepAudioClips.Length <= 0)
            {
                return;
            }
            var index = Random.Range(0, FootstepAudioClips.Length);
            AudioSource.PlayClipAtPoint(FootstepAudioClips[index], transform.position,
                FootstepAudioVolume);
        }

        private void OnLand(AnimationEvent animationEvent)
        {
            if (!(animationEvent.animatorClipInfo.weight > 0.5f))
            {
                return;
            }
            if (LandingAudioClip == null)
            {
                return;
            }
            AudioSource.PlayClipAtPoint(LandingAudioClip, transform.position, FootstepAudioVolume);
        }

        public override void Despawned(NetworkRunner runner, bool hasState)
        {
            Cleanup();
        }

        private void OnDestroy()
        {
            Cleanup();
        }

        private void Cleanup()
        {
            _characterAnim?.Dispose();
            _characterAnim = null;
        }

        private static float ClampAngle(float lfAngle, float lfMin, float lfMax)
        {
            // -180～180 に正規化してからクランプ
            lfAngle = Mathf.Repeat(lfAngle + 180f, 360f) - 180f;
            return Mathf.Clamp(lfAngle, lfMin, lfMax);
        }

        public void SetControlEnabled(bool isEnabled)
        {
            _canControl = isEnabled;
        }
    }
}
