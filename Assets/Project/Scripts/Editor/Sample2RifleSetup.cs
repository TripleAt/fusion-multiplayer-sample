using Project.Scripts.Photon;
using UnityEditor;
using UnityEngine;

namespace Project.Scripts.Editor
{
    /// <summary>
    /// Sample2_PlayerArmature プレハブの Right_Hand ボーンに
    /// Rifle_01 メッシュと FirePoint を直接埋め込むエディタユーティリティ。
    /// メニュー: Tools > Sample2 > Setup Rifle In Prefab
    /// </summary>
    public static class Sample2RifleSetup
    {
        private const string PlayerPrefabPath = "Assets/Project/Sample2/Prefabs/Sample2_PlayerArmature.prefab";
        private const string RifleFbxPath = "Assets/RetroWeaponPack_V1/Rifle_01/Fbx_Files/Rifle_01.fbx";

        [MenuItem("Tools/Sample2/Setup Rifle In Prefab")]
        public static void SetupRifleInPrefab()
        {
            var rifleFbx = AssetDatabase.LoadAssetAtPath<GameObject>(RifleFbxPath);
            if (rifleFbx == null)
            {
                Debug.LogError($"Rifle FBX not found at: {RifleFbxPath}");
                return;
            }

            // プレハブを編集モードで開く
            var prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPrefabPath);
            if (prefabAsset == null)
            {
                Debug.LogError($"Player prefab not found at: {PlayerPrefabPath}");
                return;
            }

            var prefabContents = PrefabUtility.LoadPrefabContents(PlayerPrefabPath);
            try
            {
                // 既存のライフルがあれば削除
                var existingRifle = FindRecursive(prefabContents.transform, "Rifle");
                if (existingRifle != null)
                {
                    Object.DestroyImmediate(existingRifle.gameObject);
                    Debug.Log("Removed existing Rifle.");
                }
                var existingFirePoint = FindRecursive(prefabContents.transform, "FirePoint");
                if (existingFirePoint != null)
                {
                    Object.DestroyImmediate(existingFirePoint.gameObject);
                }

                // Right_Hand ボーンを検索
                var rightHand = FindRecursive(prefabContents.transform, "Right_Hand");
                if (rightHand == null)
                {
                    Debug.LogError("Right_Hand bone not found in prefab.");
                    return;
                }

                // ライフルをインスタンス化してRight_Handの子に
                var rifleInstance = (GameObject)PrefabUtility.InstantiatePrefab(rifleFbx, rightHand);
                rifleInstance.name = "Rifle";
                // オフセット調整（Mixamo Right_Hand ボーン空間に合わせる）
                // ※ 実行後にエディタで微調整してください
                rifleInstance.transform.localPosition = Vector3.zero;
                rifleInstance.transform.localRotation = Quaternion.Euler(-90f, 0f, 90f);
                rifleInstance.transform.localScale = new Vector3(0.01f, 0.01f, 0.01f);

                // FirePoint（銃口）をプレイヤールート直下に作成
                // ボーンやFBXスケールに影響されない安定した位置
                var firePointGo = new GameObject("FirePoint");
                firePointGo.transform.SetParent(prefabContents.transform);
                firePointGo.transform.localPosition = new Vector3(0f, 1.2f, 0.5f);
                firePointGo.transform.localRotation = Quaternion.identity;
                firePointGo.transform.localScale = Vector3.one;

                // WeaponController に FirePoint を設定
                var weaponController = prefabContents.GetComponent<Sample2WeaponController>();
                if (weaponController != null)
                {
                    weaponController.FirePoint = firePointGo.transform;
                    Debug.Log("✅ WeaponController.FirePoint set.");
                }
                else
                {
                    Debug.LogWarning("Sample2WeaponController not found on prefab.");
                }

                var controller = prefabContents.GetComponent<Sample2NetworkThirdPersonController>();
                var animator = prefabContents.GetComponent<Animator>();
                if (controller != null && animator != null)
                {
                    var serializedController = new SerializedObject(controller);
                    var targetAnimatorProperty = serializedController.FindProperty("targetAnimator");
                    if (targetAnimatorProperty != null)
                    {
                        targetAnimatorProperty.objectReferenceValue = animator;
                        serializedController.ApplyModifiedPropertiesWithoutUndo();
                        Debug.Log("✅ Sample2NetworkThirdPersonController.targetAnimator set.");
                    }
                }

                // プレハブを保存
                PrefabUtility.SaveAsPrefabAsset(prefabContents, PlayerPrefabPath);
                Debug.Log("✅ Rifle + FirePoint added to Right_Hand bone in prefab.");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(prefabContents);
            }
        }

        private static Transform FindRecursive(Transform root, string name)
        {
            if (root.name == name) return root;
            for (var i = 0; i < root.childCount; i++)
            {
                var found = FindRecursive(root.GetChild(i), name);
                if (found != null) return found;
            }
            return null;
        }
    }
}
