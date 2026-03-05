# PhotonFusionSample

Photon Fusion 連携を前提に作成した Unity サンプルプロジェクトです。

この公開版リポジトリには Photon Fusion 本体および Photon 関連バイナリは含めていません。
Photon Fusion はライセンス上の都合で再配布していないため、必要な場合は各自で正規の手順で導入してください。

## Repository Policy

- Photon Fusion SDK / Fusion Addons / Photon 関連 DLL は同梱していません
- `Assets/Project/Scripts/Project.asmdef` の Photon 参照は意図的に残しています
- Unity / IDE の生成物は公開対象から除外しています

## Included / Excluded

公開版に含める主な内容:

- `Assets/Project`
- `Assets/Starter Assets`
- `Assets/MixamoAnim`
- `Assets/RetroWeaponPack_V1`
- `Packages/manifest.json`
- `ProjectSettings`

公開版から除外している主な内容:

- `Assets/Photon`
- `Assets/Packages`
- `UserSettings`
- `*.csproj`, `*.sln`, `*.slnx` などの生成ファイル

## License Notes

このリポジトリ自体には独自ライセンスを設定していません。

同梱アセットおよび依存パッケージは、それぞれの配布元ライセンス・利用規約に従ってください。

- Unity Starter Assets (`Assets/Starter Assets`)
  - Unity 提供アセットです。利用・再配布時は Unity 側の利用条件を確認してください。
- R3 (`com.cysharp.r3`)
  - MIT License
- UniTask (`com.cysharp.unitask`)
  - MIT License
- ParrelSync (`com.veriorpies.parrelsync`)
  - MIT License
- NuGetForUnity (`com.github-glitchenzo.nugetforunity`)
  - MIT License

Photon Fusion はこのリポジトリに同梱していないため、本リポジトリからライセンス再配布は行いません。Photon Fusion を利用する場合は、導入元の利用規約とライセンスに従ってください。

## Setup

1. Unity 6.0,53f1 でプロジェクトを開きます
2. 一度セーフモードで開き
3. Exit Safe Modeを選択
4. 次のパッケージをインポート
・photon-fusion-2.0.9-stable-1566.unitypackage
・fusion-simple-kcc-2.0.15.unitypackage

5. それぞれインポートし一度Unityを閉じ再度Unityを再起動

特にアタッチし直しは必要ないと思いますが `Sample1` / `Sample2` は Missing Script を含むため、必要に応じて Photon 側のスクリプトを差し戻して利用してください。
