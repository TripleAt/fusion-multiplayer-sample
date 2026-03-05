using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Fusion;
using Fusion.Sockets;
using StarterAssets;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Project.Scripts.Photon
{
    /// <summary>
    /// シーン上に配置するスポーン管理 + 入力収集クラス。
    /// Update で入力を蓄積し、OnInput で Fusion に渡す。
    /// </summary>
    public class BasicSpawner : MonoBehaviour, INetworkRunnerCallbacks
    {
        [Header("Player Spawning")] [SerializeField]
        private NetworkPrefabRef playerPrefab;

        private NetworkRunner _runner;
        private StarterAssetsInputs _localInput;
        private GameObject _mainCamera;
        private NetworkInputData _accumulatedInput;
        private bool _resetAccumulatedInput;

        public void OnPlayerJoined(NetworkRunner runner, PlayerRef player)
        {
            if (runner.IsServer)
            {
                var spawnPosition = new Vector3(0f, 1f, 0f);
                var playerObject = runner.Spawn(playerPrefab, spawnPosition, Quaternion.identity, player);
                runner.SetPlayerObject(player, playerObject);
            }

            if (player == runner.LocalPlayer)
            {
                BindLocalReferencesAsync(runner).Forget();
            }
        }

        public void OnPlayerLeft(NetworkRunner runner, PlayerRef player)
        {
            if (!runner.IsServer) return;

            var playerObject = runner.GetPlayerObject(player);
            if (playerObject == null) return;

            runner.Despawn(playerObject);
        }

        private void Update()
        {
            if (_localInput == null) return;

            if (_resetAccumulatedInput)
            {
                _resetAccumulatedInput = false;
                _accumulatedInput = default;
            }

            var data = new NetworkInputData();

            if (_localInput.move != Vector2.zero && _mainCamera != null)
            {
                var camYaw = _mainCamera.transform.eulerAngles.y;
                var rawDir = new Vector3(_localInput.move.x, 0f, _localInput.move.y);
                data.MoveDirection = Quaternion.Euler(0f, camYaw, 0f) * rawDir;
            }

            var buttons = new NetworkButtons();
            if (_localInput.jump) buttons.Set(ButtonFlag.Jump, true);
            if (_localInput.sprint) buttons.Set(ButtonFlag.Sprint, true);
            data.Buttons = buttons;

            _accumulatedInput = data;
            _localInput.jump = false;
        }

        public void OnInput(NetworkRunner runner, NetworkInput networkInput)
        {
            networkInput.Set(_accumulatedInput);
            _resetAccumulatedInput = true;
        }

        public void OnShutdown(NetworkRunner runner, ShutdownReason shutdownReason)
        {
            if (_runner == null) return;

            Destroy(_runner);
            _runner = null;
        }

        // --- INetworkRunnerCallbacks 空実装 ---
        public void OnInputMissing(NetworkRunner runner, PlayerRef player, NetworkInput input) { }
        public void OnConnectedToServer(NetworkRunner runner) { }
        public void OnDisconnectedFromServer(NetworkRunner runner, NetDisconnectReason reason) { }
        public void OnConnectRequest(NetworkRunner runner, NetworkRunnerCallbackArgs.ConnectRequest request, byte[] token) { }
        public void OnConnectFailed(NetworkRunner runner, NetAddress remoteAddress, NetConnectFailedReason reason) { }
        public void OnUserSimulationMessage(NetworkRunner runner, SimulationMessagePtr message) { }
        public void OnSessionListUpdated(NetworkRunner runner, List<SessionInfo> sessionList) { }
        public void OnCustomAuthenticationResponse(NetworkRunner runner, Dictionary<string, object> data) { }
        public void OnHostMigration(NetworkRunner runner, HostMigrationToken hostMigrationToken) { }
        public void OnSceneLoadDone(NetworkRunner runner) { }
        public void OnSceneLoadStart(NetworkRunner runner) { }
        public void OnObjectExitAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player) { }
        public void OnObjectEnterAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player) { }
        public void OnReliableDataReceived(NetworkRunner runner, PlayerRef player, ReliableKey key, ArraySegment<byte> data) { }
        public void OnReliableDataProgress(NetworkRunner runner, PlayerRef player, ReliableKey key, float progress) { }

        private async UniTaskVoid StartGameAsync(GameMode mode)
        {
            _runner = gameObject.AddComponent<NetworkRunner>();
            _runner.ProvideInput = mode != GameMode.Server;
            _runner.AddCallbacks(this);
            var scene = SceneRef.FromIndex(SceneManager.GetActiveScene().buildIndex);
            await _runner.StartGame(new StartGameArgs()
                {
                    GameMode = mode,
                    SessionName = "TestRoom",
                    Scene = scene,
                    SceneManager = gameObject.AddComponent<NetworkSceneManagerDefault>()
                })
                .AsUniTask()
                .AttachExternalCancellation(this.GetCancellationTokenOnDestroy())
                .SuppressCancellationThrow();
        }

        private async UniTaskVoid BindLocalReferencesAsync(NetworkRunner runner)
        {
            await UniTask.WaitUntil(
                () => runner != null && runner.GetPlayerObject(runner.LocalPlayer) != null,
                cancellationToken: this.GetCancellationTokenOnDestroy());

            var playerObject = runner.GetPlayerObject(runner.LocalPlayer);
            if (playerObject == null)
            {
                return;
            }

            playerObject.TryGetComponent(out _localInput);
            if (Camera.main != null)
            {
                _mainCamera = Camera.main.gameObject;
            }
        }

        private void OnGUI()
        {
            if (_runner != null) return;

            if (GUI.Button(new Rect(0, 0, 200, 40), "Server"))
            {
                StartGameAsync(GameMode.Server).Forget();
            }

            if (GUI.Button(new Rect(0, 40, 200, 40), "Join"))
            {
                StartGameAsync(GameMode.Client).Forget();
            }
        }
    }
}
