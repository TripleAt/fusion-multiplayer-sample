using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace Project.Animation
{
    /// <summary>
    /// アニメーション登録情報を保持する構造体
    /// </summary>
    public struct AnimationSetupInfo
    {
        public Guid Guid { get; }
        public AnimationClip[] Clips { get; }
        public int MixerPriority { get; }
        public bool IsAdditiveLayer { get; }
        public bool IsLoopAnim { get; }
        public int BlendGroup { get; }
        public AvatarMask AvatarMask { get; }
        public double AutoTransitionThreshold { get; }

        public AnimationSetupInfo(Guid guid, AnimationClip[] clips, int mixerPriority, bool isAdditiveLayer, bool isLoopAnim, int blendGroup = 0, AvatarMask avatarMask = null, double autoTransitionThreshold = 0.98)
        {
            Guid = guid;
            Clips = clips;
            MixerPriority = mixerPriority;
            IsAdditiveLayer = isAdditiveLayer;
            IsLoopAnim = isLoopAnim;
            BlendGroup = blendGroup;
            AvatarMask = avatarMask;
            AutoTransitionThreshold = autoTransitionThreshold;
        }
    }

    /// <summary>
    /// ThirdPerson用のアニメーションクリップ群
    /// </summary>
    public struct ThirdPersonAnimationClips
    {
        public AnimationClip Idle;
        public AnimationClip Walk;
        public AnimationClip Run;
        public AnimationClip Jump;
        public AnimationClip Fall;
        public AnimationClip Land;
    }

    public class CharacterAnim : IDisposable
    {
        private readonly AnimationSystemController _animationController;
        private readonly Func<float> _getTimeScale;
        private readonly Animator _animator;

        // Override レイヤーに登録されたアニメーション GUID 一覧
        private readonly List<Guid> _overrideAnimationGuids = new List<Guid>();

        // locomotion（ベースアニメーション）の GUID と AnimationClip
        public readonly Guid _locomotionGuid = Guid.NewGuid();
        private readonly AnimationClip[] _locomotionClips;

        // 更新ループ用のキャンセルソース
        private CancellationTokenSource _updateLoopCTS;

        // ThirdPerson 用の状態
        private Guid _walkGuid;
        private Guid _runGuid;
        private Guid _jumpGuid;
        private Guid _fallGuid;
        private Guid _landGuid;
        private float _runSpeedThreshold;
        private float _fallTimeout;
        private float _fallTimeoutDelta;
        private bool _wasGrounded;

        /// <summary>
        /// CharacterAnim クラスの新しいインスタンスを初期化します。
        /// </summary>
        /// <param name="animator">制御対象のUnity Animatorコンポーネント。</param>
        /// <param name="locomotionClips">デフォルトのロコモーションアニメーションクリップ群。</param>
        /// <param name="getTimeScale">アニメーションのタイムスケール取得関数。</param>
        /// <exception cref="ArgumentNullException">animatorまたはlocomotionClipsがnullの場合にスローされます。</exception>
        public CharacterAnim(Animator animator, AnimationClip[] locomotionClips, Func<float> getTimeScale)
        {
            _animator = animator ?? throw new ArgumentNullException(nameof(animator));
            _locomotionClips = locomotionClips ?? throw new ArgumentNullException(nameof(locomotionClips));
            _getTimeScale = getTimeScale ?? throw new ArgumentNullException(nameof(getTimeScale));
            _animationController = new AnimationSystemController(_animator, _getTimeScale);
        }

        /// <summary>
        /// ロコモーションおよび追加のオーバーライドアニメーションを登録し、
        /// アニメーショングラフを構築して開始することにより、アニメーションシステムを初期化します。
        /// ロコモーションは常に優先度0で登録されます。
        /// </summary>
        /// <param name="additionalAnimations">ベースロコモーションに加えて登録するアニメーションの<see cref="AnimationSetupInfo"/>の配列。</param>
        /// <param name="cancellationToken">初期化を中止するために監視するオプションのキャンセル処理トークン。</param>
        /// <returns>非同期初期化処理を表す<see cref="UniTask"/>。</returns>
        public async UniTask InitializeAsync(AnimationSetupInfo[] additionalAnimations, CancellationToken cancellationToken = default)
        {
            // locomotion 登録（Override レイヤー、ループ再生）
            await _animationController.AddAnimationAsync(
                _locomotionGuid,
                _locomotionClips,
                mixerPriority: 0,
                isAdditiveLayer: false,
                isLoopAnim: true,
                cancellationToken: cancellationToken);
            _overrideAnimationGuids.Add(_locomotionGuid);

            // locomotion 以外のアニメーション登録
            foreach (var anim in additionalAnimations)
            {
                await _animationController.AddAnimationAsync(
                    anim.Guid,
                    anim.Clips,
                    anim.MixerPriority,
                    anim.IsAdditiveLayer,
                    anim.IsLoopAnim,
                    anim.BlendGroup,
                    anim.AvatarMask,
                    anim.AutoTransitionThreshold,
                    cancellationToken: cancellationToken);
                if (!anim.IsAdditiveLayer)
                {
                    _overrideAnimationGuids.Add(anim.Guid);
                }
            }

            // グラフ全体の初期化と再生開始
            await _animationController.InitializeAsync(_overrideAnimationGuids, _locomotionGuid, cancellationToken);
            _ = _animationController.PlayAsync(cancellationToken);

            // 更新ループ開始
            _updateLoopCTS = new CancellationTokenSource();
            _ = UpdateLoopAsync(_updateLoopCTS.Token);
        }

        /// <summary>
        /// 毎フレームグラフを更新するループ。
        /// </summary>
        private async UniTaskVoid UpdateLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                _animationController.Update(Time.unscaledDeltaTime * _getTimeScale());
                await UniTask.Yield(PlayerLoopTiming.Update);
            }
        }

        /// <summary>
        /// 指定されたアニメーションへの遷移を要求します。
        /// </summary>
        /// <param name="targetAnimationGuid">遷移先のアニメーションGUID。</param>
        /// <param name="fullBlendDuration">ブレンド完了までの時間（秒）。</param>
        /// <param name="priority">リクエストの優先度。値が大きいほど優先度が高い。デフォルトは0。</param>
        /// <param name="resetTarget">再生前にリセットするかどうか。</param>
        /// <param name="resetToZeroOnCancel">キャンセル時に対象ブレンドを0へ戻すかどうか。デフォルトはfalse。</param>
        /// <param name="cancellationToken">この遷移リクエストをキャンセルするためのトークン。</param>
        /// <returns>遷移リクエストデータ。無効な場合はnull。</returns>
        public AnimationTransitionRequestData RequestTransition(
            Guid targetAnimationGuid,
            float fullBlendDuration,
            int priority = 0,
            bool resetTarget = true,
            bool resetToZeroOnCancel = false,
            CancellationToken cancellationToken = default)
        {
            return _animationController?.RequestTransition(
                targetAnimationGuid,
                fullBlendDuration,
                priority,
                resetTarget,
                resetToZeroOnCancel,
                cancellationToken);
        }

        /// <summary>
        /// 指定されたアニメーションへ遷移し、再生完了まで待機します。
        /// </summary>
        /// <param name="targetAnimationGuid">ターゲットアニメーションのGUID。</param>
        /// <param name="fullBlendDuration">ブレンド完了までの時間（秒）。</param>
        /// <param name="priority">リクエストの優先度。値が大きいほど優先度が高い。デフォルトは0。</param>
        /// <param name="resetTarget">再生前にリセットするかどうか。</param>
        /// <param name="cancellationToken">キャンセルトークン。</param>
        /// <returns>アニメーション再生完了を表すUniTask。</returns>
        public async UniTask PlayAsync(
            Guid targetAnimationGuid,
            float fullBlendDuration,
            int priority = 0,
            bool resetTarget = true,
            CancellationToken cancellationToken = default)
        {
            if (_animationController != null)
            {
                await _animationController.PlayAsync(targetAnimationGuid, fullBlendDuration, priority, resetTarget, cancellationToken);
            }
        }

        /// <summary>
        /// ThirdPerson用のアニメーションを初期化します。
        /// </summary>
        /// <param name="clips">ThirdPerson用のアニメーションクリップ群。</param>
        /// <param name="runSpeedThreshold">歩きから走りに切り替わる速度の閾値。</param>
        /// <param name="fallTimeout">落下アニメーションに遷移するまでの時間（秒）。</param>
        /// <param name="cancellationToken">初期化を中止するために監視するオプションのキャンセル処理トークン。</param>
        /// <returns>非同期初期化処理を表す<see cref="UniTask"/>。</returns>
        public async UniTask InitializeThirdPersonAsync(
            ThirdPersonAnimationClips clips,
            float runSpeedThreshold,
            float fallTimeout,
            CancellationToken cancellationToken = default)
        {
            _runSpeedThreshold = runSpeedThreshold;
            _fallTimeout = fallTimeout;
            _fallTimeoutDelta = fallTimeout;
            _wasGrounded = true;

            _walkGuid = Guid.NewGuid();
            _runGuid = Guid.NewGuid();
            _jumpGuid = Guid.NewGuid();
            _fallGuid = Guid.NewGuid();
            _landGuid = Guid.NewGuid();

            var additionalAnimations = new[]
            {
                new AnimationSetupInfo(_walkGuid, new[] { clips.Walk }, mixerPriority: 10, isAdditiveLayer: false, isLoopAnim: true),
                new AnimationSetupInfo(_runGuid, new[] { clips.Run }, mixerPriority: 20, isAdditiveLayer: false, isLoopAnim: true),
                new AnimationSetupInfo(_jumpGuid, new[] { clips.Jump }, mixerPriority: 30, isAdditiveLayer: false, isLoopAnim: false),
                new AnimationSetupInfo(_fallGuid, new[] { clips.Fall }, mixerPriority: 40, isAdditiveLayer: false, isLoopAnim: true),
                new AnimationSetupInfo(_landGuid, new[] { clips.Land }, mixerPriority: 50, isAdditiveLayer: false, isLoopAnim: false),
            };

            await InitializeAsync(additionalAnimations, cancellationToken);
        }

        /// <summary>
        /// ThirdPersonキャラクターの状態を更新し、適切なアニメーションへの遷移を行います。
        /// </summary>
        /// <param name="speed">現在の移動速度。</param>
        /// <param name="grounded">キャラクターが地面に接地しているかどうか。</param>
        /// <param name="jumpTriggered">ジャンプがトリガーされたかどうか。</param>
        /// <param name="deltaTime">前フレームからの経過時間。</param>
        public void UpdateThirdPersonState(float speed, bool grounded, bool jumpTriggered, float deltaTime)
        {
            // ジャンプ処理（priority 50）
            if (jumpTriggered && grounded)
            {
                _animationController?.RequestTransition(_jumpGuid, 0.05f, priority: 50);
                _wasGrounded = grounded;
                return;
            }

            // 空中処理（priority 30）
            if (!grounded)
            {
                _fallTimeoutDelta -= deltaTime;
                if (_fallTimeoutDelta <= 0f)
                {
                    _animationController?.RequestTransition(_fallGuid, 0.1f, priority: 30, resetTarget: true);
                }
                _wasGrounded = grounded;
                return;
            }

            // 以降は接地時の処理
            _fallTimeoutDelta = _fallTimeout;

            // 着地時の遷移（priority 35）
            if (!_wasGrounded)
            {
                _animationController?.RequestTransition(_landGuid, 0.05f, priority: 35);
            }

            // 移動速度に応じた遷移（priority 10）
            UpdateLocomotionBySpeed(speed);

            _wasGrounded = grounded;
        }

        /// <summary>
        /// 移動速度に応じてロコモーションアニメーションを更新します。
        /// </summary>
        private void UpdateLocomotionBySpeed(float speed)
        {
            var targetGuid = speed switch
            {
                <= 0.001f => _locomotionGuid,
                _ when speed < _runSpeedThreshold => _walkGuid,
                _ => _runGuid
            };
            
            Debug.Log($"Target AnimationName: {(targetGuid == _locomotionGuid ? "Idle" : targetGuid == _walkGuid ? "Walk" : "Run")}, Speed: {speed}");

            _animationController?.RequestTransition(targetGuid, 0.1f, priority: 10, resetTarget: true);
        }

        /// <summary>
        /// <see cref="CharacterAnim"/> インスタンスが使用するすべてのリソースを解放します。
        /// これには、実行中のタスクのキャンセル、およびサブスクリプションの破棄が含まれます。
        /// </summary>
        public void Dispose()
        {
            _updateLoopCTS?.Cancel();
            _updateLoopCTS?.Dispose();
            _animationController?.Dispose();
        }
    }
}
