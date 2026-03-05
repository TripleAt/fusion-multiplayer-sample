using Fusion;
using UnityEngine;

namespace Project.Scripts.Photon
{
    /// <summary>
    /// Sample2 専用のネットワーク入力データ構造体。
    /// </summary>
    public struct Sample2NetworkInputData : INetworkInput
    {
        /// <summary>
        /// カメラ方向を適用済みのワールド空間移動方向。
        /// </summary>
        public Vector3 MoveDirection;

        /// <summary>
        /// ボタン入力（Jump, Sprint, Fire）。
        /// </summary>
        public NetworkButtons Buttons;
    }
}
