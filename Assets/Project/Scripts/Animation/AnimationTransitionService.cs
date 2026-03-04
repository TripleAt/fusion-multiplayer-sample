using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using R3;
using UnityEngine;

namespace Project.Animation
{
    internal sealed class AnimationTransitionService : IDisposable
    {
        private readonly AnimationTransitionProcessor _processor;
        private readonly AnimationBaseGraph _animationGraph;
        private readonly IReadOnlyDictionary<int, Guid> _defaultAnimationGuidsByGroup;
        private readonly CompositeDisposable _compositeDisposable = new ();

        /// <summary>
        /// <see cref="AnimationTransitionService"/> の新しいインスタンスを初期化します。
        /// </summary>
        /// <param name="animationGraph">アニメーションの再生とブレンドを制御する <see cref="AnimationBaseGraph"/>。</param>
        /// <param name="processor">アニメーション遷移処理を実行する <see cref="AnimationTransitionProcessor"/>。</param>
        /// <param name="defaultAnimationGuidsByGroup">各ブレンドグループのデフォルトアニメーションGUID。</param>
        public AnimationTransitionService(AnimationBaseGraph animationGraph, AnimationTransitionProcessor processor, IReadOnlyDictionary<int, Guid> defaultAnimationGuidsByGroup)
        {
            _animationGraph = animationGraph;
            _processor = processor;
            _defaultAnimationGuidsByGroup = defaultAnimationGuidsByGroup;

            _animationGraph.AutoTransitionObservable.Subscribe(guid =>
            {
                if (_animationGraph.TryGetAnimationIsAdditiveLayer(guid, out var isAdditiveLayer) &&
                    !isAdditiveLayer &&
                    _animationGraph.TryGetBlendGroup(guid, out var blendGroup) &&
                    _defaultAnimationGuidsByGroup.TryGetValue(blendGroup, out var defaultGuid))
                {
                    RequestTransition(defaultGuid, 0.05f, priority: 0, resetTarget: false);
                }
            }).AddTo(_compositeDisposable);
            _animationGraph.OnEndAnimationObservable.Subscribe(guid =>
            {
                _animationGraph.TryResetFreezeMixer(guid);
            }).AddTo(_compositeDisposable);
        }

        /// <summary>
        /// 指定されたアニメーションへの遷移を要求します。
        /// 要求は処理キューに追加され、順次実行されます。
        /// </summary>
        /// <param name="targetAnimationGuid">遷移先のターゲットアニメーションのGUID。</param>
        /// <param name="fullBlendDuration">完全にブレンドするまでの時間（秒単位）。</param>
        /// <param name="priority">リクエストの優先度。値が大きいほど優先度が高い。デフォルトは0。</param>
        /// <param name="resetTarget">ターゲットアニメーションを再生前にリセットするかどうか。デフォルトはtrue。</param>
        /// <param name="resetToZeroOnCancel">キャンセル時に対象ブレンドを0へ戻すかどうか。デフォルトはfalse。</param>
        /// <param name="cancellationToken">この遷移リクエストをキャンセルするためのトークン。</param>
        /// <returns>生成された遷移要求データ。要求が無効な場合（例：既に処理中またはブレンド率が1の場合）はnullを返します。</returns>
        public AnimationTransitionRequestData RequestTransition(
            Guid targetAnimationGuid,
            float fullBlendDuration,
            int priority = 0,
            bool resetTarget = true,
            bool resetToZeroOnCancel = false,
            CancellationToken cancellationToken = default)
        {
            // 1. もしすでに同じ対象が「遷移実行中 or キューに登録されている」なら何もしない
            if (_processor.HasSameTargetInProcessOrQueue(targetAnimationGuid))
            {
                return null;
            }

            // 2. 対象の BlendRate がすでに 1 なら何もしない
            if (_processor.IsBlendRate1(targetAnimationGuid))
            {
                Debug.Log($"Animation {targetAnimationGuid} is already at full blend rate. Skipping transition.");
                return null;
            }
            else
            {
                Debug.Log($"Animation {targetAnimationGuid} is not at full blend rate. Starting transition.");
            }
            
            // 3. 優先度チェックはベースグループ（0）のみ対象。
            // 上半身などのオーバーレイ用グループは、下半身状態機械と独立して上書きする。
            if (_animationGraph.TryGetBlendGroup(targetAnimationGuid, out var blendGroup) &&
                blendGroup == 0)
            {
                var currentMaxPriority = _processor.GetCurrentMaxPriority(targetAnimationGuid);
                if (priority < currentMaxPriority)
                {
                    return null;
                }

                _processor.CancelLowerPriorityOverrideRequests(targetAnimationGuid, priority);
            }

            var request = new AnimationTransitionRequestData(
                targetAnimationGuid,
                fullBlendDuration,
                resetTarget,
                priority,
                resetToZeroOnCancel,
                cancellationToken);
            _processor.EnqueueRequest(request);
            return request;
        }

        /// <summary>
        /// 指定されたアニメーションへの遷移を要求し、そのアニメーションの再生完了まで待機します。
        /// </summary>
        /// <param name="targetAnimationGuid">ターゲットアニメーションのGUID。</param>
        /// <param name="fullBlendDuration">完全にブレンドするまでの時間（秒単位）。</param>
        /// <param name="priority">リクエストの優先度。値が大きいほど優先度が高い。デフォルトは0。</param>
        /// <param name="resetTarget">ターゲットアニメーションを再生前にリセットするかどうか。デフォルトはtrue。</param>
        /// <param name="cancellationToken">非同期操作をキャンセルするためのキャンセルトークン。</param>
        /// <returns>アニメーション再生完了を表す <see cref="UniTask"/>。</returns>
        public async UniTask PlayAsync(Guid targetAnimationGuid, float fullBlendDuration, int priority = 0, bool resetTarget = true, CancellationToken cancellationToken = default)
        {
            // 遷移要求を発行し、リクエストオブジェクトを取得
            var request = RequestTransition(
                targetAnimationGuid,
                fullBlendDuration,
                priority,
                resetTarget,
                resetToZeroOnCancel: true,
                cancellationToken: cancellationToken);
            if(request == null)
            {
                // すでに処理中または完了状態なら、単に終了イベントを待つ
                await _animationGraph.OnEndAnimationObservable
                    .Where(guid => guid == targetAnimationGuid)
                    .FirstAsync(cancellationToken);
                return;
            }
    
            // PlayAsync からは、キャンセルが必要な場合、返されたリクエストの CTS を利用できる
            // ここでは、アニメーション終了イベント（対象のGUID）を待機します。
            await _animationGraph.OnEndAnimationObservable
                .Where(guid => guid == targetAnimationGuid)
                .FirstAsync(cancellationToken);
        }

        /// <summary>
        /// <see cref="AnimationTransitionService"/> が使用しているリソースを解放します。
        /// </summary>
        public void Dispose()
        {
            _compositeDisposable.Dispose();
        }
    }
}
