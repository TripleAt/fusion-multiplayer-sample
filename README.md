# PhotonFusionSample

Photon Fusion 連携を前提に作成した Unity サンプルプロジェクトです。

この公開版リポジトリには Photon Fusion 本体および Photon 関連バイナリは含めていません。Photon Fusion はライセンス上の都合で再配布していないため、必要な場合は各自で正規の手順で導入してください。

`Assets/Project/Sample1` と `Assets/Project/Sample2` は残しています。Photon 関連スクリプトは同梱していないため、シーンやプレハブには Missing Script が含まれる状態ですが、Photon 前提のサンプル構成が分かるようにあえて残しています。

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

このリポジトリ内の独自コードと設定ファイルは、特記がない限り同梱の `LICENSE` に従います。

ただし、同梱アセットおよび依存パッケージは、それぞれの元ライセンスに従います。

- Unity Starter Assets (`Assets/Starter Assets`)
  - Unity 提供アセットです。このリポジトリ独自のライセンスでは再許諾しません。利用・再配布時は Unity 側の利用条件を確認してください。
- Mixamo animation assets (`Assets/MixamoAnim`)
  - Adobe Mixamo 由来アセットです。利用条件は配布元の規約を確認してください。
- RetroWeaponPack_V1 (`Assets/RetroWeaponPack_V1`)
  - 配布元アセットの利用条件に従ってください。このリポジトリ独自のライセンスでは再許諾しません。
- R3 (`com.cysharp.r3`)
  - MIT License
- UniTask (`com.cysharp.unitask`)
  - MIT License
- VContainer (`jp.hadashikick.vcontainer`)
  - MIT License
- ParrelSync (`com.veriorpies.parrelsync`)
  - MIT License
- NuGetForUnity (`com.github-glitchenzo.nugetforunity`)
  - MIT License

Photon Fusion はこのリポジトリに同梱していないため、本リポジトリからライセンス再配布は行いません。Photon Fusion を利用する場合は、導入元の利用規約とライセンスに従ってください。

## Setup

1. Unity 6 系でプロジェクトを開きます。
2. `Packages/manifest.json` に定義された依存パッケージを取得します。
3. Photon Fusion を使う場合は、各自で正規に導入してください。
4. `Sample1` / `Sample2` は Missing Script を含むため、必要に応じて Photon 側のスクリプトを差し戻して利用してください。
