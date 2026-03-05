using Fusion;
using UnityEngine;

namespace Project.Scripts.Photon
{
    /// <summary>
    /// ネットワーク入力データ構造体。
    /// カメラ方向適用済みのワールド空間移動方向を含む。
    /// </summary>
    public struct NetworkInputData : INetworkInput
    {
        /// <summary>
        /// カメラ方向を適用済みのワールド空間移動方向。
        /// magnitude はアナログ入力強度を表す（0～1）。
        /// </summary>
        public Vector3 MoveDirection;

        /// <summary>
        /// ボタン入力（Jump, Sprint）。
        /// </summary>
        public NetworkButtons Buttons;
    }
}
