using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Cysharp.Threading.Tasks;
using R3;
using UnityEngine;

namespace Project.Animation
{
    internal sealed class AnimationTransitionProcessor : IDisposable
    {
        private readonly AnimationBaseGraph _animationGraph;
        private readonly Func<float> _getTimeScale;

        private readonly CancellationTokenSource _lifetimeCts = new();
        private readonly Subject<AnimationTransitionRequestData> _processQueueSubject = new();
        private readonly CompositeDisposable _compositeDisposable = new();
        private readonly HashSet<Guid> _pendingTargets = new();
        private readonly Dictionary<AnimationTransitionRequestData, CancellationTokenSource> _overrideRequestCts = new();

        // 処理中のリクエストがあれば保持
        private readonly List<AnimationTransitionRequestData> _currentRequestDataList = new();
        private readonly List<Guid> _allGuids;

        /// <summary>
        /// <see cref="AnimationTransitionProcessor"/> の新しいインスタンスを初期化します。
        /// </summary>
        /// <param name="animationGraph">アニメーションの再生とブレンドを制御する <see cref="AnimationBaseGraph"/>。</param>
        /// <param name="getTimeScale">現在のアニメーションタイムスケールを取得する関数。</param>
        /// <param name="allGuids">システムに存在するすべてのアニメーションGUIDのリスト。</param>
        public AnimationTransitionProcessor(AnimationBaseGraph animationGraph, Func<float> getTimeScale, List<Guid> allGuids)
        {
            _animationGraph = animationGraph;
            _getTimeScale = getTimeScale;
            _allGuids = allGuids;
            _processQueueSubject.SubscribeAwait(
                (request, cancelToken) => ProcessQueueAsync(request, cancelToken).AsValueTask()
                ).AddTo(_compositeDisposable);
        }

        /// <summary>
        /// 指定されたターゲットGUIDに対するリクエストが、現在処理中またはキューに登録されているかどうかを確認します。
        /// </summary>
        /// <param name="targetGuid">確認するターゲットアニメーションのGUID。</param>
        /// <returns>リクエストが処理中またはキューに存在する場合はtrue、それ以外の場合はfalse。</returns>
        public bool HasSameTargetInProcessOrQueue(Guid targetGuid)
        {
            // 現在実行中のデータ、または保留中のターゲットを確認
            return _currentRequestDataList.Any(currentRequestData =>
                currentRequestData != null && currentRequestData.TargetAnimationGuid == targetGuid) ||
                   _pendingTargets.Contains(targetGuid);
        }

        /// <summary>
        /// 指定されたGUIDのアニメーションのブレンド率が既に1（100%）かどうかを確認します。
        /// </summary>
        /// <param name="guid">確認するアニメーションのGUID。</param>
        /// <returns>ブレンド率が1の場合はtrue、それ以外の場合はfalse。</returns>
        public bool IsBlendRate1(Guid guid)
        {
            return _animationGraph.TryGetBlendRate(guid, out var blend) && Mathf.Approximately(blend, 1f);
        }

        /// <summary>
        /// 現在処理中のリクエストの最高優先度を取得します。
        /// </summary>
        /// <returns>処理中のリクエストがある場合はその最高優先度、ない場合はint.MinValue。</returns>
        public int GetCurrentMaxPriority(Guid targetGuid)
        {
            if (!_animationGraph.TryGetBlendGroup(targetGuid, out var targetBlendGroup))
            {
                return int.MinValue;
            }

            var maxPriority = int.MinValue;
            foreach (var request in _currentRequestDataList)
            {
                if (request == null)
                {
                    continue;
                }

                if (!_animationGraph.TryGetBlendGroup(request.TargetAnimationGuid, out var requestBlendGroup) ||
                    requestBlendGroup != targetBlendGroup)
                {
                    continue;
                }

                if (request.Priority > maxPriority)
                {
                    maxPriority = request.Priority;
                }
            }

            return maxPriority;
        }

        public void CancelLowerPriorityOverrideRequests(Guid targetGuid, int newPriority)
        {
            if (!_animationGraph.TryGetBlendGroup(targetGuid, out var targetBlendGroup))
            {
                return;
            }

            var requestsToCancel = _overrideRequestCts.Keys
                .Where(request => request != null && request.Priority < newPriority)
                .Where(request =>
                    _animationGraph.TryGetAnimationIsAdditiveLayer(request.TargetAnimationGuid, out var isAdditiveLayer) &&
                    !isAdditiveLayer &&
                    _animationGraph.TryGetBlendGroup(request.TargetAnimationGuid, out var requestBlendGroup) &&
                    requestBlendGroup == targetBlendGroup)
                .ToList();

            foreach (var request in requestsToCancel)
            {
                CancelOverrideRequest(request);
            }
        }

        /// <summary>
        /// 指定されたターゲットGUIDに対する保留中のリクエストをキャンセルします。
        /// 現在処理中のリクエストは個別のCancellationTokenで制御されるため、ここでは保留状態の解除のみ行います。
        /// </summary>
        /// <param name="targetGuid">キャンセルするターゲットアニメーションのGUID。</param>
        public void CancelTargetRequest(Guid targetGuid)
        {
            _pendingTargets.Remove(targetGuid);
        }

        /// <summary>
        /// すべてのオーバーライドアニメーション（非加算レイヤー）に関連する保留中および実行中のリクエストをクリアします。
        /// </summary>
        public void ClearAllOverrideRequests()
        {
            var targetsToRemove = _pendingTargets
                .Where(target => _animationGraph.TryGetAnimationIsAdditiveLayer(target, out var isAdditiveLayer) && !isAdditiveLayer)
                .ToList();
            foreach (var target in targetsToRemove)
            {
                _pendingTargets.Remove(target);
            }
            var overrideRequests = _currentRequestDataList
                .Where(request => request != null &&
                                  _animationGraph.TryGetAnimationIsAdditiveLayer(request.TargetAnimationGuid, out var isAdditiveLayer) &&
                                  !isAdditiveLayer)
                .ToList();
            foreach (var request in overrideRequests)
            {
                CancelOverrideRequest(request);
            }
            foreach (var request in _overrideRequestCts.Keys.ToList())
            {
                CancelOverrideRequest(request);
            }
        }

        /// <summary>
        /// 保留中および実行中のすべてのリクエストをクリアします。
        /// </summary>
        public void ClearAllRequests()
        {
            _pendingTargets.Clear();
            _currentRequestDataList.Clear();
            foreach (var cts in _overrideRequestCts.Values)
            {
                cts.Cancel();
                cts.Dispose();
            }
            _overrideRequestCts.Clear();
        }

        /// <summary>
        /// アニメーション遷移リクエストを処理キューに追加します。
        /// </summary>
        /// <param name="request">追加するアニメーション遷移リクエストデータ。</param>
        public void EnqueueRequest(AnimationTransitionRequestData request)
        {
            _pendingTargets.Add(request.TargetAnimationGuid);
            HandleRequest(request);
        }

        /// <summary>
        /// 発行されたアニメーションリクエストを順番に処理します。
        /// 実行中のリクエストは _currentRequestDataList に保持されます。
        /// </summary>
        private void HandleRequest(AnimationTransitionRequestData request)
        {
            if (!_animationGraph.TryGetAnimationIsAdditiveLayer(request.TargetAnimationGuid,
                    out var isAdditiveLayer))
            {
                RemovePendingTarget(request.TargetAnimationGuid);
                return;
            }

            if (isAdditiveLayer)
            {
                _currentRequestDataList.Add(request);
                ProcessAdditiveAsync(request).Forget();
                return;
            }
            _overrideRequestCts[request] = new CancellationTokenSource();
            _processQueueSubject.OnNext(request);
        }

        private async UniTask ProcessQueueAsync(AnimationTransitionRequestData request, CancellationToken cancellationToken)
        {
            if (_overrideRequestCts.TryGetValue(request, out var internalCts) && internalCts.IsCancellationRequested)
            {
                CleanupOverrideRequest(request);
                return;
            }

            var groupGuids = GetGuidsInSameBlendGroup(request.TargetAnimationGuid);
            var currentStartBlends = new Dictionary<Guid, float>();
            foreach (var guid in groupGuids)
            {
                // キーが存在しない場合は 0f を返す
                var currentBlend = _animationGraph.TryGetBlendRate(guid, out var b) ? b : 0f;
                currentStartBlends[guid] = currentBlend;
            }
            request.StartBlends = currentStartBlends;
            _currentRequestDataList.Add(request);
            if (request.ResetTarget)
            {
                var blend = _animationGraph.TryGetBlendRate(request.TargetAnimationGuid, out var val) ? val : 0f;
                if (Mathf.Approximately(blend, 0f) || Mathf.Approximately(blend, 1f))
                {
                    _animationGraph.TryResetPlayMixer(request.TargetAnimationGuid);
                }
            }
            await ProcessOverrideAsync(request, cancellationToken);
        }

        /// <summary>
        /// 1件のオーバーライドアニメーションリクエストを、指定されたブレンド時間で線形補間処理します。
        /// キャプチャされた開始状態（StartBlends）から、ターゲットアニメーションは1へ、その他は0へ補間されます。
        /// </summary>
        private async UniTask ProcessOverrideAsync(AnimationTransitionRequestData request, CancellationToken cancellationToken)
        {
            var internalCts = _overrideRequestCts.GetValueOrDefault(request);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                request.CancellationToken,
                cancellationToken,
                internalCts?.Token ?? CancellationToken.None);
            var linkedToken = linkedCts.Token;

            var startTarget = request.StartBlends.GetValueOrDefault(request.TargetAnimationGuid);
            var effectiveDuration = (1f - startTarget) * request.FullBlendDuration;
    
            var groupGuids = GetGuidsInSameBlendGroup(request.TargetAnimationGuid);
            var elapsed = 0f;
            while (!linkedToken.IsCancellationRequested && elapsed < effectiveDuration)
            {
                var delta = Time.deltaTime * _getTimeScale();
                elapsed += delta;
                foreach (var guid in groupGuids)
                {
                    var start = request.StartBlends.GetValueOrDefault(guid);
                    // 対象は最終的に 1、その他は 0 にする
                    var target = (guid == request.TargetAnimationGuid) ? 1f : 0f;
                    // effectiveDuration に合わせた補間パラメータ t を計算
                    var t = Mathf.Clamp01(elapsed / effectiveDuration);
                    var newBlend = Mathf.Lerp(start, target, t);
                    _animationGraph.TrySetBlendRate(guid, newBlend, true);
                    _animationGraph.TryPlayMixer(guid);
                }
                await UniTask.Yield(PlayerLoopTiming.Update);
            }
            if (linkedToken.IsCancellationRequested)
            {
                CleanupOverrideRequest(request);
                return;
            }
            // 補間完了後、確実に最終値を設定
            foreach (var guid in request.StartBlends.Keys)
            {
                var target = (guid == request.TargetAnimationGuid) ? 1f : 0f;
                _animationGraph.TrySetBlendRate(guid, target);
            }
            CleanupOverrideRequest(request);
        }

        private async UniTaskVoid ProcessAdditiveAsync(AnimationTransitionRequestData request)
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                request.CancellationToken,
                _lifetimeCts.Token);
            var linkedToken = linkedCts.Token;

            _animationGraph.TryPlayMixer(request.TargetAnimationGuid);
            var elapsed = 0f;
            var startBlend = _animationGraph.TryGetBlendRate(request.TargetAnimationGuid, out var b) ? b : 0f;
            // Additiveの場合、対象のみ更新し、他のBlendRateは変更しない
            while (!linkedToken.IsCancellationRequested && elapsed < request.FullBlendDuration)
            {
                var delta = Time.deltaTime * _getTimeScale();
                elapsed += delta;
                var t = Mathf.Clamp01(elapsed / request.FullBlendDuration);
                var newBlend = Mathf.Lerp(startBlend, 1f, t);
                _animationGraph.TrySetBlendRate(request.TargetAnimationGuid, newBlend);
                await UniTask.Yield(PlayerLoopTiming.Update);
            }
            if (linkedToken.IsCancellationRequested)
            {
                _currentRequestDataList.Remove(request);
                RemovePendingTarget(request.TargetAnimationGuid);
                return;
            }
            _animationGraph.TrySetBlendRate(request.TargetAnimationGuid, 1f);
            _currentRequestDataList.Remove(request);
            RemovePendingTarget(request.TargetAnimationGuid);
        }

        private void RemovePendingTarget(Guid targetGuid)
        {
            _pendingTargets.Remove(targetGuid);
        }

        /// <summary>
        /// 指定されたGUIDと同じブレンドグループに属するGUIDのリストを返します。
        /// ブレンドグループの取得に失敗した場合は、全GUIDを返します（後方互換）。
        /// </summary>
        private List<Guid> GetGuidsInSameBlendGroup(Guid targetGuid)
        {
            if (!_animationGraph.TryGetBlendGroup(targetGuid, out var targetGroup))
            {
                return _allGuids;
            }

            var result = new List<Guid>();
            foreach (var guid in _allGuids)
            {
                if (_animationGraph.TryGetBlendGroup(guid, out var group) && group == targetGroup)
                {
                    result.Add(guid);
                }
            }
            return result;
        }

        private void CancelOverrideRequest(AnimationTransitionRequestData request)
        {
            if (_overrideRequestCts.TryGetValue(request, out var cts))
            {
                cts.Cancel();
            }
            _currentRequestDataList.Remove(request);
            RemovePendingTarget(request.TargetAnimationGuid);
        }

        private void CleanupOverrideRequest(AnimationTransitionRequestData request)
        {
            _currentRequestDataList.Remove(request);
            RemovePendingTarget(request.TargetAnimationGuid);
            if (!_overrideRequestCts.TryGetValue(request, out var cts))
            {
                return;
            }
            _overrideRequestCts.Remove(request);
            cts.Dispose();
        }

        /// <summary>
        /// <see cref="AnimationTransitionProcessor"/> が使用しているリソースを解放します。
        /// </summary>
        public void Dispose()
        {
            if (!_lifetimeCts.IsCancellationRequested)
            {
                _lifetimeCts.Cancel();
            }
            foreach (var cts in _overrideRequestCts.Values)
            {
                cts.Cancel();
                cts.Dispose();
            }
            _overrideRequestCts.Clear();
            _lifetimeCts.Dispose();
            _compositeDisposable.Dispose();
            _processQueueSubject.Dispose();
        }
    }

    /// <summary>
    /// アニメーション遷移リクエストのデータを保持するレコード。
    /// </summary>
    /// <param name="TargetAnimationGuid">ターゲットアニメーションの一意の識別子。</param>
    /// <param name="FullBlendDuration">完全にブレンドするまでの総時間（秒単位）。</param>
    /// <param name="ResetTarget">ターゲットアニメーションを再生前にリセットするかどうか。</param>
    /// <param name="Priority">リクエストの優先度。値が大きいほど優先度が高い。</param>
    /// <param name="ResetToZeroOnCancel">キャンセル時に対象ブレンドを0へ戻すかどうか。</param>
    /// <param name="CancellationToken">このリクエストのキャンセルを監視するトークン。</param>
    public sealed record AnimationTransitionRequestData(
        Guid TargetAnimationGuid,
        float FullBlendDuration,
        bool ResetTarget,
        int Priority,
        bool ResetToZeroOnCancel,
        CancellationToken CancellationToken)
    {
        /// <summary>ターゲットアニメーションの一意の識別子を取得します。</summary>
        public Guid TargetAnimationGuid { get; } = TargetAnimationGuid;
        /// <summary>ターゲットアニメーションを再生前にリセットするかどうかを取得します。</summary>
        public bool ResetTarget { get; } = ResetTarget;
        /// <summary>完全にブレンドするまでの総時間（秒単位）を取得します。</summary>
        public float FullBlendDuration { get; } = FullBlendDuration;
        /// <summary>リクエストの優先度。値が大きいほど優先度が高い。</summary>
        public int Priority { get; } = Priority;
        /// <summary>キャンセル時に対象ブレンドを0へ戻すかどうかを取得します。</summary>
        public bool ResetToZeroOnCancel { get; } = ResetToZeroOnCancel;
        /// <summary>遷移開始時の各アニメーションのブレンド率のディクショナリを取得または設定します。</summary>
        public Dictionary<Guid, float> StartBlends { get; set; }
        /// <summary>このリクエストのキャンセルを監視するトークンを取得します。</summary>
        public CancellationToken CancellationToken { get; } = CancellationToken;
    }
}
