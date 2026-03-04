using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Cysharp.Threading.Tasks;
using R3;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace Project.Animation
{
    public sealed class AnimationBaseGraph : IDisposable
	{
		private readonly Animator _animator;
		private PlayableGraph _graph;
		private readonly Dictionary<Guid, AnimationData> _mixers = new();
		private AnimationLayerMixerPlayable _rootMixer;
		private readonly Dictionary<int, uint> _blendGroupToLayerIndex = new();

		/// <summary>
		/// アニメーションが終了しようとしているときにそのアニメーションのGUIDを発行するObservableを取得します。
		/// これにより、自動遷移（例：デフォルトのロコモーション状態に戻る）の必要性を示唆します。
		/// </summary>
        private readonly Subject<Guid> _autoTransitionSubject = new();
		public Observable<Guid> AutoTransitionObservable => _autoTransitionSubject;

		/// <summary>
		/// アニメーションの再生が完了したときにそのアニメーションのGUIDを発行するObservableを取得します。
		/// これは通常、ループしないアニメーションに使用されます。
		/// </summary>
        private readonly Subject<Guid> _onEndSubject = new();
		public Observable<Guid> OnEndAnimationObservable => _onEndSubject;
		
		private sealed record AnimationData
		{
			public int MixerPriority;
			public bool IsAdditiveLayer;
			public bool IsLoopAnim;
            public int BlendGroup;
            public AvatarMask AvatarMask;
			public AnimationMixerPlayable MixerPlayable;
			public Playable ParentMixerPlayable;
			public int ParentIndex = 0;
			public float BlendRate = 0.0f;
			public double Duration = 0.0;
			public double AutoTransitionThreshold = 0.98; // デフォルトは98%
		}

		/// <summary>
		/// <see cref="AnimationBaseGraph"/> クラスの新しいインスタンスを初期化します。
		/// </summary>
		/// <param name="animator">このグラフが制御するUnity Animatorコンポーネント。</param>
		public AnimationBaseGraph(Animator animator)
		{
			_animator = animator;
			_graph = PlayableGraph.Create("PlayableGraphAnimator");
		}

		/// <summary>
		/// PlayableGraphが有効でまだ再生されていない場合に再生を開始します。
		/// グラフが終了するまで待機します（ループするグラフの場合は無期限になることがあります）。
		/// </summary>
		/// <param name="cancellationToken">非同期操作中のキャンセルを監視するためのオプションのCancellationToken。</param>
		/// <returns>非同期操作を表すUniTask。</returns>
		public async UniTask PlayAsync(CancellationToken cancellationToken = default)
		{
			if (!_graph.IsValid())
			{
				Debug.LogError("Invalid graph detected!");
				return;
			}
			if (!_graph.IsPlaying())
			{
				_graph.Play();  // 初回のみ再生
			}

			await UniTask.WaitUntil(() => !_graph.IsValid() || _graph.IsDone(), cancellationToken: cancellationToken);
		}

		/// <summary>
		/// 指定されたGUIDに関連付けられたアニメーションミキサーをリセットして再生しようとします。
		/// ミキサーの速度を1に、時間を0に設定します。
		/// </summary>
		/// <param name="guid">リセットして再生するアニメーションミキサーのGUID。</param>
		/// <returns>ミキサーが見つかりリセットされた場合はtrue、それ以外の場合はfalse。</returns>
		public bool TryResetPlayMixer(Guid guid)
		{
			if (!_mixers.TryGetValue(guid, out var animationData))
			{
				Debug.LogWarning($"Animation with GUID {guid} not found.");
				return false;
			}
			
			animationData.MixerPlayable.SetSpeed(1);
			animationData.MixerPlayable.SetTime(0);
			return true;
		}

		/// <summary>
		/// 指定されたGUIDに関連付けられたアニメーションミキサーをリセットしてフリーズしようとします。
		/// ミキサーの速度を0に、時間を0に設定します。
		/// </summary>
		/// <param name="guid">リセットしてフリーズするアニメーションミキサーのGUID。</param>
		/// <returns>ミキサーが見つかりリセットされた場合はtrue、それ以外の場合はfalse。</returns>
		public bool TryResetFreezeMixer(Guid guid)
		{
			if (!_mixers.TryGetValue(guid, out var animationData))
			{
				Debug.LogWarning($"Animation with GUID {guid} not found.");
				return false;
			}
			
			animationData.MixerPlayable.SetSpeed(0);
			animationData.MixerPlayable.SetTime(0);
			return true;
		}

		/// <summary>
		/// 指定されたGUIDに関連付けられたアニメーションミキサーを、速度を0に設定してフリーズしようとします。
		/// 注意：タイムスケールは通常、親のUpdateメソッドによって制御されます。
		/// </summary>
		/// <param name="guid">フリーズするアニメーションミキサーのGUID。</param>
		/// <returns>ミキサーが見つかりフリーズされた場合はtrue、それ以外の場合はfalse。</returns>
		public bool TryFreezeMixer(Guid guid)
		{
			if (!_mixers.TryGetValue(guid, out var animationData))
			{
				Debug.LogWarning($"Animation with GUID {guid} not found.");
				return false;
			}
			
			animationData.MixerPlayable.SetSpeed(0);
			return true;
		}

		/// <summary>
		/// 指定されたGUIDに関連付けられたアニメーションミキサーを、速度を1に設定して再生しようとします。
		/// 注意：タイムスケールは通常、親のUpdateメソッドによって制御されます。
		/// </summary>
		/// <param name="guid">再生するアニメーションミキサーのGUID。</param>
		/// <returns>ミキサーが見つかり再生設定された場合はtrue、それ以外の場合はfalse。</returns>
		public bool TryPlayMixer(Guid guid)
		{
			if (!_mixers.TryGetValue(guid, out var animationData))
			{
				Debug.LogWarning($"Animation with GUID {guid} not found.");
				return false;
			}
			
			animationData.MixerPlayable.SetSpeed(1);
			return true;
		}

		/// <summary>
		/// 指定されたミキサー内のアクティブなアニメーションのブレンド率（入力ウェイト）を設定しようとします。
		/// </summary>
		/// <param name="guid">アニメーションミキサーのGUID。</param>
		/// <param name="rate">設定するブレンド率（0.0から1.0）。</param>
		/// <param name="ignoreAdditiveLayer">trueの場合、レイヤーが加算レイヤーでない場合にのみブレンド率が設定されます。デフォルトはfalseです。</param>
		/// <returns>ブレンド率が正常に設定された場合はtrue、それ以外の場合はfalse。</returns>
		public bool TrySetBlendRate(Guid guid, float rate, bool ignoreAdditiveLayer = false)
		{
			if (!_mixers.TryGetValue(guid, out var animationData))
			{
				Debug.LogWarning($"Animation with GUID {guid} not found.");
				return false;
			}

			if (ignoreAdditiveLayer && animationData.IsAdditiveLayer)
			{
				return false;
			}

            animationData.BlendRate = rate;
            if (animationData.ParentMixerPlayable.IsValid())
            {
                animationData.ParentMixerPlayable.SetInputWeight(animationData.ParentIndex, rate);
            }
            return true;
        }

		/// <summary>
		/// 指定されたアニメーションミキサーの現在のブレンド率を取得しようとします。
		/// </summary>
		/// <param name="guid">アニメーションミキサーのGUID。</param>
		/// <param name="rate">このメソッドが戻るとき、ミキサーが見つかった場合はブレンド率が含まれます。それ以外の場合は0.0f。</param>
		/// <returns>ミキサーが見つかり、そのブレンド率が取得された場合はtrue、それ以外の場合はfalse。</returns>
		public bool TryGetBlendRate(Guid guid, out float rate)
		{
			if (!_mixers.TryGetValue(guid, out var animationData))
			{
				Debug.LogWarning($"Animation with GUID {guid} not found.");
				rate = 0.0f;
				return false;
			}

			rate = animationData.BlendRate;
			return true;
		}

		/// <summary>
		/// 指定されたGUIDに関連付けられたアニメーションが加算レイヤーであるかどうかを判断しようとします。
		/// </summary>
		/// <param name="guid">アニメーションのGUID。</param>
		/// <param name="isAdditiveLayer">このメソッドが戻るとき、アニメーションが加算レイヤーである場合はtrueが含まれます。それ以外の場合はfalse。</param>
		/// <returns>アニメーションが見つかった場合はtrue、それ以外の場合はfalse。</returns>
		public bool TryGetAnimationIsAdditiveLayer(Guid guid, out bool isAdditiveLayer)
		{
			if (!_mixers.TryGetValue(guid, out var animationData))
			{
				Debug.LogWarning($"Animation with GUID {guid} not found.");
				isAdditiveLayer = false;
				return false;
			}

			isAdditiveLayer = animationData.IsAdditiveLayer;
			return true;
		}

        public bool TryGetBlendGroup(Guid guid, out int blendGroup)
        {
            if (!_mixers.TryGetValue(guid, out var animationData))
            {
                Debug.LogWarning($"Animation with GUID {guid} not found.");
                blendGroup = 0;
                return false;
            }

            blendGroup = animationData.BlendGroup;
            return true;
        }

		/// <summary>
		/// 指定されたブレンドグループに対応するレイヤーのウェイトを設定します。
		/// レイヤーウェイトを0にすると、そのレイヤーの出力は無効化され、下位レイヤーがそのまま表示されます。
		/// </summary>
		/// <param name="blendGroup">ウェイトを設定するブレンドグループのID。</param>
		/// <param name="weight">設定するウェイト値（0.0～1.0）。</param>
		/// <returns>正常に設定された場合はtrue、ブレンドグループが見つからない場合はfalse。</returns>
		public bool TrySetBlendGroupLayerWeight(int blendGroup, float weight)
		{
			if (!_blendGroupToLayerIndex.TryGetValue(blendGroup, out var layerIndex))
			{
				return false;
			}

			if (!_rootMixer.IsValid())
			{
				return false;
			}

			_rootMixer.SetInputWeight((int)layerIndex, weight);
			return true;
		}

		/// <summary>
		/// 新しいアニメーション（ミキサー内の複数のクリップで構成される可能性があります）をグラフに追加します。
		/// </summary>
		/// <param name="guid">このアニメーションの一意の識別子。</param>
		/// <param name="animationClips">このアニメーションのミキサーに含めるAnimationClipのコレクション。</param>
		/// <param name="mixerPriority">このミキサーの優先度（値が低いほど最初に処理されます）。デフォルトは0です。</param>
		/// <param name="isAdditiveLayer">このアニメーションレイヤーを加算レイヤーにするかどうかを指定します。デフォルトはfalseです。</param>
		/// <param name="isLoopAnim">アニメーションクリップをループさせるかどうかを指定します。デフォルトはfalseです。</param>
		/// <param name="autoTransitionThreshold">自動遷移通知を発行する閾値（0.0～1.0）。デフォルトは0.98（98%）です。</param>
		/// <param name="cancellationToken">非同期操作中のキャンセルを監視するためのオプションのCancellationToken。</param>
		/// <returns>アニメーションの追加の非同期操作を表すUniTask。</returns>
		public async UniTask AddAnimationAsync(Guid guid, IReadOnlyCollection<AnimationClip> animationClips, int mixerPriority = 0, bool isAdditiveLayer = false, bool isLoopAnim = false, int blendGroup = 0, AvatarMask avatarMask = null, double autoTransitionThreshold = 0.98, CancellationToken cancellationToken = default)
		{
			var isInitialized = _animator != null && _animator.isInitialized;

			// アニメーション初期化待ち
			if (!isInitialized)
			{
				await UniTask.WaitUntil(() => _animator != null && _animator.isInitialized,
					cancellationToken: cancellationToken);
				if(cancellationToken.IsCancellationRequested)
				{
					return;
				}
			}

			// アニメーションの登録
			var playables = new List<AnimationClipPlayable>();
			var maxDuration = 0.0;
			foreach (var clip in animationClips)
			{
				var playable = AnimationClipPlayable.Create(_graph, clip);
				playable.SetTime(0);
				playable.SetDuration(isLoopAnim ? double.PositiveInfinity : clip.length);
				playables.Add(playable);

				if (!isLoopAnim && clip.length > maxDuration)
				{
					maxDuration = clip.length;
				}
			}

			// すべてひとまとめにするためのレイヤーMixerを作っておく
			var mixer = AnimationMixerPlayable.Create(_graph, animationClips.Count);

			var playableOutput = _graph.GetOutputByType<AnimationPlayableOutput>(0);
			if (!playableOutput.IsOutputValid())
			{
				AnimationPlayableOutput.Create(_graph, $"Anim", _animator);
			}

			for (var i = 0; i < animationClips.Count; i++)
			{
				mixer.SetSpeed(0);
				mixer.ConnectInput(i, playables[i], 0);
				mixer.SetInputWeight(i, 1.0f);
			}

			_mixers[guid] = new AnimationData
			{
				IsAdditiveLayer = isAdditiveLayer,
				IsLoopAnim = isLoopAnim,
                BlendGroup = blendGroup,
                AvatarMask = avatarMask,
				MixerPriority = mixerPriority,
				MixerPlayable = mixer,
				Duration = maxDuration,
				AutoTransitionThreshold = autoTransitionThreshold
			};
		}

		/// <summary>
		/// すべてのアニメーションが追加された後、アニメーショングラフ構造を初期化します。
		/// これにより、ルートミキサーが作成され、オーバーライドレイヤーと加算レイヤーが接続されます。
		/// </summary>
		/// <param name="cancellationToken">非同期操作中のキャンセルを監視するためのオプションのCancellationToken。</param>
		/// <returns>非同期初期化プロセスを表すUniTask。</returns>
		public async UniTask CreateInitAsync(CancellationToken cancellationToken = default)
		{
			var isInitialized = _animator != null && _animator.isInitialized;
			// アニメーション初期化待ち
			if (!isInitialized)
			{
				await UniTask.WaitUntil(() => _animator != null && _animator.isInitialized,
					cancellationToken: cancellationToken);
				if (cancellationToken.IsCancellationRequested)
				{
					return;
				}
			}
			var playableOutput = _graph.GetOutputByType<AnimationPlayableOutput>(0);
			if (!playableOutput.IsOutputValid())
			{
				playableOutput = AnimationPlayableOutput.Create(_graph, $"Anim", _animator);
			}
			var layerGroups = _mixers
                .OrderBy(x => x.Value.MixerPriority)
                .GroupBy(x => new { x.Value.IsAdditiveLayer, x.Value.BlendGroup })
                .ToList();

			var rootMixer = AnimationLayerMixerPlayable.Create(_graph, Math.Max(layerGroups.Count, 1));
			_rootMixer = rootMixer;
			_blendGroupToLayerIndex.Clear();
			playableOutput.SetSourcePlayable(rootMixer);
			
			_graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
			for (var layerIndex = 0; layerIndex < layerGroups.Count; layerIndex++)
			{
                var layerGroup = layerGroups[layerIndex].ToList();
                var layerMixer = AnimationMixerPlayable.Create(_graph, layerGroup.Count);

                rootMixer.SetLayerAdditive((uint)layerIndex, layerGroups[layerIndex].Key.IsAdditiveLayer);
                rootMixer.ConnectInput(layerIndex, layerMixer, 0);
                rootMixer.SetInputWeight(layerIndex, 1f);

                var blendGroupKey = layerGroups[layerIndex].Key.BlendGroup;
                _blendGroupToLayerIndex[(int)blendGroupKey] = (uint)layerIndex;

                var avatarMask = layerGroup.Select(x => x.Value.AvatarMask).FirstOrDefault(mask => mask != null);
                if (avatarMask != null)
                {
                    rootMixer.SetLayerMaskFromAvatarMask((uint)layerIndex, avatarMask);
                }

                for (var mixerIndex = 0; mixerIndex < layerGroup.Count; mixerIndex++)
                {
                    var animationData = layerGroup[mixerIndex].Value;
                    SetMix(layerMixer, mixerIndex, animationData);
                    animationData.ParentMixerPlayable = layerMixer;
                    animationData.ParentIndex = mixerIndex;
                }
			}
		}

		private static void SetMix(AnimationMixerPlayable mixerPlayable, int additiveIndex, AnimationData animationData)
		{
			if (additiveIndex >= mixerPlayable.GetInputCount())
			{
				mixerPlayable.SetInputCount(additiveIndex + 1);
			}
			mixerPlayable.ConnectInput(additiveIndex, animationData.MixerPlayable, 0);
			mixerPlayable.SetInputWeight(additiveIndex, 0.0f);
		}

		/// <summary>
		/// 指定されたデルタ時間でPlayableGraphを評価することにより、手動で更新します。
		/// これは、グラフのTimeUpdateModeがManualに設定されている場合に呼び出す必要があります。
		/// アニメーションの終了も検知します。
		/// </summary>
		/// <param name="deltaTime">最後のフレームからの経過時間。</param>
		public void Update(float deltaTime)
		{
			if (!_graph.IsValid())
			{
				return;
			}

			_graph.Evaluate(deltaTime);

			// アニメーション終了検知
			CheckAnimationCompletion();
		}

		/// <summary>
		/// アクティブなアニメーションの終了をチェックし、必要に応じてイベントを発行します。
		/// </summary>
		private void CheckAnimationCompletion()
		{
			foreach (var (guid, data) in _mixers)
			{
				// ループアニメーションまたは非アクティブなアニメーションはスキップ
				if (data.IsLoopAnim || data.BlendRate <= 0.0f)
				{
					continue;
				}

				var currentTime = data.MixerPlayable.GetTime();

				// 終了間近の検知（autoTransition用）- 各アニメーションの閾値を使用
				if (data.Duration > 0 && currentTime >= data.Duration * data.AutoTransitionThreshold && currentTime < data.Duration)
				{
					_autoTransitionSubject.OnNext(guid);
				}

				// 終了検知（onEnd用）
				if (data.Duration > 0 && currentTime >= data.Duration)
				{
					_onEndSubject.OnNext(guid);
				}
			}
		}
		
		private void StopCurrentAnimations()
		{
			if (!_graph.IsValid())
			{
				return;
			}
		}

		/// <summary>
		/// <see cref="AnimationBaseGraph"/>インスタンスが使用するすべてのリソースを破棄します。
		/// これには、PlayableGraphの停止と破棄、および進行中のタスクのキャンセルが含まれます。
		/// </summary>
		public void Dispose()
		{
			if (!_graph.IsValid())
			{
				return;
			}
			_graph.Stop();
			_graph.Destroy();
            _mixers.Clear();
            _mixers.TrimExcess();
            _autoTransitionSubject.Dispose();
            _onEndSubject.Dispose();
		}
    }
}
