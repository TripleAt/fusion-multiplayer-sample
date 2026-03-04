using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using R3;
using UnityEngine;

namespace Project.Animation
{
    public struct Sample2UpperBodyAnimationClips
    {
        public AnimationClip RifleIdle;
        public AnimationClip FiringRifle;
        /// <summary>腕だけマスク（待機用）</summary>
        public AvatarMask UpperBodyMask;
        /// <summary>上半身マスク（発射用: Body+Head+Arms）</summary>
        public AvatarMask FiringUpperBodyMask;
    }

    public sealed class Sample2CharacterAnim : IDisposable
    {
        private const int BaseBlendGroup = 0;
        private const int UpperBlendGroup = 1;
        private const int FiringBlendGroup = 2;

        private readonly AnimationSystemController _animationController;
        private readonly Func<float> _getTimeScale;
        private readonly AnimationClip _idleClip;
        private readonly CompositeDisposable _subscriptions = new();

        private readonly Guid _idleGuid = Guid.NewGuid();
        private Guid _walkGuid;
        private Guid _runGuid;
        private Guid _jumpGuid;
        private Guid _fallGuid;
        private Guid _landGuid;
        private Guid _upperOffGuid;
        private Guid _rifleIdleGuid;
        private Guid _firingOffGuid;
        private Guid _firingGuid;

        private CancellationTokenSource _updateLoopCTS;
        private float _runSpeedThreshold;
        private float _fallTimeout;
        private float _fallTimeoutDelta;
        private bool _wasGrounded;
        private bool _upperBodyInitialized;
        private bool _upperBodyPoseEnabled;
        private bool _isFiringUpperBody;

        public Sample2CharacterAnim(Animator animator, AnimationClip[] locomotionClips, Func<float> getTimeScale)
        {
            if (animator == null)
            {
                throw new ArgumentNullException(nameof(animator));
            }

            if (locomotionClips == null || locomotionClips.Length == 0 || locomotionClips[0] == null)
            {
                throw new ArgumentNullException(nameof(locomotionClips));
            }

            _getTimeScale = getTimeScale ?? throw new ArgumentNullException(nameof(getTimeScale));
            _idleClip = locomotionClips[0];
            _animationController = new AnimationSystemController(animator, _getTimeScale);
        }

        public async UniTask InitializeThirdPersonAsync(
            ThirdPersonAnimationClips clips,
            float runSpeedThreshold,
            float fallTimeout,
            Sample2UpperBodyAnimationClips upperBodyClips,
            CancellationToken cancellationToken = default)
        {
            DisposeRuntimeState();

            _runSpeedThreshold = runSpeedThreshold;
            _fallTimeout = fallTimeout;
            _fallTimeoutDelta = fallTimeout;
            _wasGrounded = true;
            _upperBodyPoseEnabled = true;
            _isFiringUpperBody = false;

            _walkGuid = Guid.NewGuid();
            _runGuid = Guid.NewGuid();
            _jumpGuid = Guid.NewGuid();
            _fallGuid = Guid.NewGuid();
            _landGuid = Guid.NewGuid();
            _upperOffGuid = Guid.NewGuid();
            _rifleIdleGuid = Guid.NewGuid();
            _firingOffGuid = Guid.NewGuid();
            _firingGuid = Guid.NewGuid();

            var additionalAnimations = new List<AnimationSetupInfo>
            {
                new(_walkGuid, new[] { clips.Walk }, mixerPriority: 10, isAdditiveLayer: false, isLoopAnim: true, blendGroup: BaseBlendGroup),
                new(_runGuid, new[] { clips.Run }, mixerPriority: 20, isAdditiveLayer: false, isLoopAnim: true, blendGroup: BaseBlendGroup),
                new(_jumpGuid, new[] { clips.Jump }, mixerPriority: 30, isAdditiveLayer: false, isLoopAnim: false, blendGroup: BaseBlendGroup),
                new(_fallGuid, new[] { clips.Fall }, mixerPriority: 40, isAdditiveLayer: false, isLoopAnim: true, blendGroup: BaseBlendGroup),
                new(_landGuid, new[] { clips.Land }, mixerPriority: 50, isAdditiveLayer: false, isLoopAnim: false, blendGroup: BaseBlendGroup),
            };

            _upperBodyInitialized = upperBodyClips.RifleIdle != null &&
                                    upperBodyClips.FiringRifle != null &&
                                    upperBodyClips.UpperBodyMask != null &&
                                    upperBodyClips.FiringUpperBodyMask != null;

            var defaultAnimationsByGroup = new Dictionary<int, Guid>
            {
                [BaseBlendGroup] = _idleGuid,
            };

            if (_upperBodyInitialized)
            {
                // 腕だけレイヤー（待機用）
                var upperOffClip = new AnimationClip { name = "Sample2_UpperOff" };
                additionalAnimations.Add(new AnimationSetupInfo(_upperOffGuid, new[] { upperOffClip }, mixerPriority: 100, isAdditiveLayer: false, isLoopAnim: true, blendGroup: UpperBlendGroup, avatarMask: upperBodyClips.UpperBodyMask));
                additionalAnimations.Add(new AnimationSetupInfo(_rifleIdleGuid, new[] { upperBodyClips.RifleIdle }, mixerPriority: 110, isAdditiveLayer: false, isLoopAnim: true, blendGroup: UpperBlendGroup, avatarMask: upperBodyClips.UpperBodyMask));
                defaultAnimationsByGroup[UpperBlendGroup] = _upperOffGuid;

                // 上半身レイヤー（発射用: Body+Head+Arms）
                var firingOffClip = new AnimationClip { name = "Sample2_FiringOff" };
                additionalAnimations.Add(new AnimationSetupInfo(_firingOffGuid, new[] { firingOffClip }, mixerPriority: 200, isAdditiveLayer: false, isLoopAnim: true, blendGroup: FiringBlendGroup, avatarMask: upperBodyClips.FiringUpperBodyMask));
                additionalAnimations.Add(new AnimationSetupInfo(_firingGuid, new[] { upperBodyClips.FiringRifle }, mixerPriority: 210, isAdditiveLayer: false, isLoopAnim: false, blendGroup: FiringBlendGroup, avatarMask: upperBodyClips.FiringUpperBodyMask, autoTransitionThreshold: 1.0));
                defaultAnimationsByGroup[FiringBlendGroup] = _firingOffGuid;
            }

            await InitializeAsync(additionalAnimations, defaultAnimationsByGroup, cancellationToken);
        }

        public void UpdateSample2State(
            float speed,
            bool grounded,
            bool jumpTriggered,
            float deltaTime,
            bool enableUpperBodyPose,
            bool fireTriggered)
        {
            UpdateThirdPersonState(speed, grounded, jumpTriggered, deltaTime);
            UpdateUpperBodyState(enableUpperBodyPose, fireTriggered);
        }

        private async UniTask InitializeAsync(
            List<AnimationSetupInfo> additionalAnimations,
            Dictionary<int, Guid> defaultAnimationsByGroup,
            CancellationToken cancellationToken)
        {
            var allGuids = new List<Guid> { _idleGuid };

            await _animationController.AddAnimationAsync(
                _idleGuid,
                new[] { _idleClip },
                mixerPriority: 0,
                isAdditiveLayer: false,
                isLoopAnim: true,
                blendGroup: BaseBlendGroup,
                cancellationToken: cancellationToken);

            foreach (var animation in additionalAnimations)
            {
                await _animationController.AddAnimationAsync(
                    animation.Guid,
                    animation.Clips,
                    animation.MixerPriority,
                    animation.IsAdditiveLayer,
                    animation.IsLoopAnim,
                    animation.BlendGroup,
                    animation.AvatarMask,
                    animation.AutoTransitionThreshold,
                    cancellationToken);
                allGuids.Add(animation.Guid);
            }

            await _animationController.InitializeAsync(allGuids, defaultAnimationsByGroup, cancellationToken);
            _ = _animationController.PlayAsync(cancellationToken);

            _subscriptions.Add(_animationController.OnEndAnimationObservable.Subscribe(OnAnimationEnded));

            _updateLoopCTS = new CancellationTokenSource();
            _ = UpdateLoopAsync(_updateLoopCTS.Token);

            if (_upperBodyInitialized)
            {
                RequestArmsTransition(_rifleIdleGuid, 0f, resetTarget: true);
            }
        }

        private async UniTaskVoid UpdateLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                _animationController.Update(Time.unscaledDeltaTime * _getTimeScale());
                await UniTask.Yield(PlayerLoopTiming.Update);
            }
        }

        private void UpdateThirdPersonState(float speed, bool grounded, bool jumpTriggered, float deltaTime)
        {
            if (jumpTriggered && grounded)
            {
                _animationController.RequestTransition(_jumpGuid, 0.05f, priority: 50);
                _wasGrounded = grounded;
                return;
            }

            if (!grounded)
            {
                _fallTimeoutDelta -= deltaTime;
                if (_fallTimeoutDelta <= 0f)
                {
                    _animationController.RequestTransition(_fallGuid, 0.1f, priority: 30, resetTarget: false);
                }

                _wasGrounded = grounded;
                return;
            }

            _fallTimeoutDelta = _fallTimeout;

            if (!_wasGrounded)
            {
                _animationController.RequestTransition(_landGuid, 0.05f, priority: 35);
            }

            var targetGuid = speed switch
            {
                <= 0.01f => _idleGuid,
                _ when speed < _runSpeedThreshold => _walkGuid,
                _ => _runGuid
            };

            _animationController.RequestTransition(targetGuid, 0.1f, priority: 10, resetTarget: false);
            _wasGrounded = grounded;
        }

        private void UpdateUpperBodyState(bool enableUpperBodyPose, bool fireTriggered)
        {
            if (!_upperBodyInitialized)
            {
                return;
            }

            if (!enableUpperBodyPose)
            {
                _upperBodyPoseEnabled = false;
                _isFiringUpperBody = false;
                RequestArmsTransition(_upperOffGuid, 0.08f, resetTarget: false);
                RequestFiringTransition(_firingOffGuid, 0.08f, resetTarget: false);
                return;
            }

            if (!_upperBodyPoseEnabled)
            {
                _upperBodyPoseEnabled = true;
                RequestArmsTransition(_rifleIdleGuid, 0.08f, resetTarget: true);
            }

            if (fireTriggered)
            {
                _isFiringUpperBody = true;
                RequestFiringTransition(_firingGuid, 0.05f, resetTarget: true);
                return;
            }

            if (!_isFiringUpperBody)
            {
                RequestArmsTransition(_rifleIdleGuid, 0.08f, resetTarget: false);
                RequestFiringTransition(_firingOffGuid, 0.08f, resetTarget: false);
            }
        }

        private void RequestArmsTransition(Guid targetGuid, float blendDuration, bool resetTarget)
        {
            _animationController.RequestTransition(targetGuid, blendDuration, priority: 60, resetTarget: resetTarget);
        }

        private void RequestFiringTransition(Guid targetGuid, float blendDuration, bool resetTarget)
        {
            _animationController.RequestTransition(targetGuid, blendDuration, priority: 70, resetTarget: resetTarget);
        }

        private void OnAnimationEnded(Guid guid)
        {
            if (guid != _firingGuid)
            {
                return;
            }

            _isFiringUpperBody = false;
            // 発射終了 → 発射レイヤーをOFFに戻す
            RequestFiringTransition(_firingOffGuid, 0.08f, resetTarget: false);
        }

        private void DisposeRuntimeState()
        {
            _subscriptions.Clear();

            if (_updateLoopCTS != null)
            {
                _updateLoopCTS.Cancel();
                _updateLoopCTS.Dispose();
                _updateLoopCTS = null;
            }
        }

        public void Dispose()
        {
            DisposeRuntimeState();
            _subscriptions.Dispose();
            _animationController.Dispose();
        }
    }
}
