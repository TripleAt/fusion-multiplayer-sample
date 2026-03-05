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
    /// Fusion + SimpleKCC によるネットワーク対応三人称キャラクターコントローラー。
    /// SimpleKCC をコンポジションで使用し、NetworkBehaviour を継承する。
    /// カメラ方向はシミュレーションに含めず、入力ポーリング側でワールド空間方向に変換済みの
    /// MoveDirection を受け取る設計。
    /// </summary>
    public sealed class NetworkThirdPersonController : NetworkBehaviour, IBeforeTick
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

        // --- Networked state (ロールバック・再シミュレーション対応) ---
        [Networked] private float JumpTimeoutDelta { get; set; }
        [Networked] private Vector3 _moveVelocity { get; set; }

        // --- Input / Simulation state  ---
        private NetworkInputData _currentInput;
        private float _coyoteTime;

        // --- Local state (ビジュアル・カメラ用、ネットワーク同期不要) ---
        private float _cinemachineTargetYaw;
        private float _cinemachineTargetPitch;
        private CharacterAnim _characterAnim;

        [Header("References")]
        [SerializeField] private PlayerInput playerInput;
        [SerializeField] private StarterAssetsInputs localInput;

        private const float Threshold = 0.01f;
        private const float CoyoteTimeDuration = 0.15f;

        private bool IsCurrentDeviceMouse => playerInput != null && playerInput.currentControlScheme == "KeyboardMouse";

        public override void Spawned()
        {
            JumpTimeoutDelta = JumpTimeout;
            _moveVelocity = Vector3.zero;
            _currentInput = default;
            kcc.SetGravity(Gravity);

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

                var kccCollider = transform.Find("KCCCollider");
                if (kccCollider != null)
                {
                    kccCollider.gameObject.tag = "Player";
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
        /// GetInput が失敗した場合は前回の入力が再利用される
        /// </summary>
        void IBeforeTick.BeforeTick()
        {
            if (Object == null) return;

            if (Object.InputAuthority == PlayerRef.None)
            {
                return;
            }
            
            if (GetInput(out NetworkInputData input))
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
            // プロキシ（他クライアントのリモートキャラ）は SimpleKCC の同期に任せる。
            // GetInput はサーバーと Input Authority でしか取得できない
            if (!HasStateAuthority && !HasInputAuthority) return;

            var moveDir = _currentInput.MoveDirection;
            var isSprinting = _currentInput.Buttons.IsSet(ButtonFlag.Sprint);
            var jumpPressed = _currentInput.Buttons.IsSet(ButtonFlag.Jump);

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
            if (localInput == null)
            {
                return;
            }

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

            // kcc.RealSpeed はリシミュレーションの影響でブレるため、
            // Lerp 済みの _moveVelocity（[Networked] で補間される）から速度を算出する。
            var animSpeed = _moveVelocity.magnitude;
            _characterAnim.UpdateThirdPersonState(animSpeed, kcc.IsGrounded, kcc.HasJumped, Time.deltaTime);
        }

        private async UniTaskVoid InitializeAnimationAsync()
        {
            if (targetAnimator == null)
            {
                Debug.LogWarning("NetworkThirdPersonController: Animator is not assigned.");
                return;
            }

            if (IdleClip == null || WalkClip == null || RunClip == null ||
                JumpClip == null || FallClip == null || LandClip == null)
            {
                Debug.LogWarning("NetworkThirdPersonController: Animation clips are not assigned.");
                return;
            }

            _characterAnim = new CharacterAnim(targetAnimator, new[] { IdleClip }, () => Time.timeScale);
            var clips = new ThirdPersonAnimationClips
            {
                Idle = IdleClip,
                Walk = WalkClip,
                Run = RunClip,
                Jump = JumpClip,
                Fall = FallClip,
                Land = LandClip
            };

            var runSpeedThreshold = (MoveSpeed + SprintSpeed) * 0.5f;
            await _characterAnim.InitializeThirdPersonAsync(clips, runSpeedThreshold, FallTimeout);
        }

        private void OnFootstep(AnimationEvent animationEvent)
        {
            if (!(animationEvent.animatorClipInfo.weight > 0.5f))
            {
                return;
            }

            if (FootstepAudioClips.Length <= 0)
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
    }
}
