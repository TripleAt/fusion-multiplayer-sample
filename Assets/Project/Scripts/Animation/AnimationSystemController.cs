using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using R3;
using UnityEngine;

namespace Project.Animation
{
    /// <summary>
    /// アニメーションシステム全体を統括するコントローラー。
    /// AnimationBaseGraphを所有し、TransitionServiceとProcessorを内包してメディエーターとして機能します。
    /// </summary>
    public sealed class AnimationSystemController : IDisposable
    {
        private readonly AnimationBaseGraph _animationGraph;
        private AnimationTransitionProcessor _processor;
        private AnimationTransitionService _transitionService;
        private readonly Func<float> _getTimeScale;

        /// <summary>
        /// アニメーションが終了しようとしているときにそのアニメーションのGUIDを発行するObservableを取得します。
        /// </summary>
        public Observable<Guid> AutoTransitionObservable => _animationGraph.AutoTransitionObservable;

        /// <summary>
        /// アニメーションの再生が完了したときにそのアニメーションのGUIDを発行するObservableを取得します。
        /// </summary>
        public Observable<Guid> OnEndAnimationObservable => _animationGraph.OnEndAnimationObservable;

        /// <summary>
        /// AnimationSystemControllerの新しいインスタンスを初期化します。
        /// </summary>
        /// <param name="animator">制御対象のUnity Animatorコンポーネント。</param>
        /// <param name="getTimeScale">現在のタイムスケールを取得する関数。</param>
        public AnimationSystemController(Animator animator, Func<float> getTimeScale)
        {
            _animationGraph = new AnimationBaseGraph(animator);
            _getTimeScale = getTimeScale;
        }

        /// <summary>
        /// アニメーションをグラフに追加します。
        /// </summary>
        public async UniTask AddAnimationAsync(
            Guid guid,
            UnityEngine.AnimationClip[] clips,
            int mixerPriority = 0,
            bool isAdditiveLayer = false,
            bool isLoopAnim = false,
            int blendGroup = 0,
            AvatarMask avatarMask = null,
            double autoTransitionThreshold = 0.98,
            CancellationToken cancellationToken = default)
        {
            await _animationGraph.AddAnimationAsync(
                guid,
                clips,
                mixerPriority,
                isAdditiveLayer,
                isLoopAnim,
                blendGroup,
                avatarMask,
                autoTransitionThreshold,
                cancellationToken);
        }

        /// <summary>
        /// すべてのアニメーションが追加された後、アニメーショングラフを初期化します。
        /// </summary>
        /// <param name="animList">管理対象のアニメーションGUIDのリスト。</param>
        /// <param name="locomotionGuid">デフォルトのロコモーションアニメーションのGUID。</param>
        /// <param name="cancellationToken">キャンセルトークン。</param>
        public async UniTask InitializeAsync(
            List<Guid> animList,
            Guid locomotionGuid,
            CancellationToken cancellationToken = default)
        {
            await InitializeAsync(animList, new Dictionary<int, Guid> { [0] = locomotionGuid }, cancellationToken);
        }

        public async UniTask InitializeAsync(
            List<Guid> animList,
            Dictionary<int, Guid> defaultAnimationGuidsByGroup,
            CancellationToken cancellationToken = default)
        {
            await _animationGraph.CreateInitAsync(cancellationToken);

            // Processorの初期化
            _processor?.Dispose();
            _processor = new AnimationTransitionProcessor(_animationGraph, _getTimeScale, animList);

            // TransitionServiceの初期化（Processorを渡す）
            _transitionService?.Dispose();
            _transitionService = new AnimationTransitionService(
                _animationGraph,
                _processor,
                defaultAnimationGuidsByGroup);
        }

        /// <summary>
        /// グラフを再生開始します。
        /// </summary>
        public async UniTask PlayAsync(CancellationToken cancellationToken = default)
        {
            await _animationGraph.PlayAsync(cancellationToken);
        }

        /// <summary>
        /// グラフを手動更新します。
        /// </summary>
        /// <param name="deltaTime">デルタタイム。</param>
        public void Update(float deltaTime)
        {
            _animationGraph.Update(deltaTime);
        }

        /// <summary>
        /// 指定されたブレンドグループのレイヤーウェイトを設定します。
        /// ウェイトを0にするとそのレイヤーは完全に無効化され、下位レイヤーがそのまま表示されます。
        /// </summary>
        /// <param name="blendGroup">ブレンドグループID。</param>
        /// <param name="weight">ウェイト値（0.0～1.0）。</param>
        /// <returns>正常に設定された場合はtrue。</returns>
        public bool SetBlendGroupLayerWeight(int blendGroup, float weight)
        {
            return _animationGraph.TrySetBlendGroupLayerWeight(blendGroup, weight);
        }

        /// <summary>
        /// アニメーション遷移をリクエストします。
        /// </summary>
        public AnimationTransitionRequestData RequestTransition(
            Guid targetAnimationGuid,
            float fullBlendDuration,
            int priority = 0,
            bool resetTarget = true,
            bool resetToZeroOnCancel = false,
            CancellationToken cancellationToken = default)
        {
            return _transitionService?.RequestTransition(
                targetAnimationGuid,
                fullBlendDuration,
                priority,
                resetTarget,
                resetToZeroOnCancel,
                cancellationToken);
        }

        /// <summary>
        /// アニメーションへ遷移し、再生完了まで待機します。
        /// </summary>
        public async UniTask PlayAsync(
            Guid targetAnimationGuid,
            float fullBlendDuration,
            int priority = 0,
            bool resetTarget = true,
            CancellationToken cancellationToken = default)
        {
            if (_transitionService != null)
            {
                await _transitionService.PlayAsync(
                    targetAnimationGuid,
                    fullBlendDuration,
                    priority,
                    resetTarget,
                    cancellationToken);
            }
        }

        /// <summary>
        /// リソースを解放します。
        /// </summary>
        public void Dispose()
        {
            _transitionService?.Dispose();
            _processor?.Dispose();
            _animationGraph?.Dispose();
        }
    }
}
