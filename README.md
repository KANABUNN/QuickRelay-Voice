# QuickRelay Voice

QuickRelay Voice（QRV）は、QuickRelay の緊急地震速報（EEW）を Windows で受信し、AivisSpeech で読み上げるアプリです。警報、警報地域の追加、対象となる予報、明示取消を扱います。配信の接続状態、上流と取込の状態、時刻差、音声合成、音声出力を画面で確認できます。

## 起動

公開フォルダーから起動する場合は、Windows x64 用の .NET 10 Desktop Runtime を入れ、`QuickRelay Voice.exe` を実行します。開発環境からは .NET 10 SDK で実行できます。

```powershell
dotnet run --project .\QuickRelay.Voice\QuickRelay.Voice.csproj
```

1. AivisSpeech を起動します。QRV の既定ポートは `10101` です。「一覧を取得」で話者スタイルを選び、「テスト再生」で Windows の既定の音声出力を確認できます。スタイルIDを空欄にすると、利用可能な「ノーマル」スタイルを優先します。
2. 配信との接続試験には、試験用の `wss://ホスト/v1/eew` とその端末用 Bearer トークンを指定し、配信環境を「試験」にします。「消音で受信」は新規設定時に有効です。消音のまま「設定を保存して接続」を選び、同期完了と受信状況を画面で確認できます。
3. 警報の対象は既定で全国です。「全国の警報を対象にする」を外すと、対象地域に絞れます。地域欄には配信側で定義された `codeType:code` を1行に1件入力します。予報の読み上げは既定で無効です。有効にすると最低震度と続報の震度差を設定できます。

消音中も状態を更新し、有効な読み上げ候補を最近の動作欄に表示します。消音を解除すると、その後に成立した候補から判定します。同期中や接続先の上流・取込・時刻に異常がある場合は、音声を保留します。再接続時に取得した報は状態の復元に使います。QRV がすでに報知した系列の明示取消は、開始期限内に条件がそろえば一度読み上げます。訓練・試験の発話には環境名が付きます。

## ビルドと確認

リポジトリのルートで実行します。

```powershell
dotnet build .\QuickRelay.Voice\QuickRelay.Voice.csproj -c Release
dotnet run --project .\QuickRelay.Voice.Checks\QuickRelay.Voice.Checks.csproj -c Release
dotnet publish .\QuickRelay.Voice\QuickRelay.Voice.csproj -c Release -r win-x64 --self-contained false -o .\artifacts\publish
& '.\artifacts\publish\QuickRelay Voice.exe'
```

`QuickRelay.Voice.Checks` は、ローカルの TLS WSS フィクスチャと模擬電文を使います。証明書検証、Bearer 認証、購読、同期と再開、連番の欠落、発話条件を確認できます。この確認は QuickRelay の本番配信への接続を必要としません。

## 保存先

- `%LOCALAPPDATA%\QuickRelay Voice\settings.json` に配信先、配信環境、対象地域、消音、予報、AivisSpeech の設定を保存します。
- 同じフォルダーの `feed-*.json` に配信の再開位置と地震の状態を、`speech-*.json` に重複防止用の報知開始記録を保存します。
- トークンは Windows 資格情報マネージャーの汎用資格情報 `QuickRelay Voice/<接続先・環境のハッシュ>` に保存します。
- 同じフォルダーに `readings.json` を置くと、地名の音声表記を変更できます。JSON オブジェクトに `{"例示地域":"れいじちいき"}` の形式で指定すると、次回接続時に読み込みます。

出力の直前に報知開始記録を保存します。記録後から実際の出音までに異常終了した場合は、再起動後の重複防止を優先するため、その音声が流れないことがあります。

## QuickRelay PR-C との接続

開発時点の配信契約は v1.1 設計案の `schemaVersion: 1` です。PR-C の試験配信が利用できるまでは、コードに同梱した TLS WSS フィクスチャと、配信に接続せずに使えるローカルの音声テストで確認できます。接続試験では、PR-C が生成したフレーム、地域の `codeType` と階層、予報の品質値、状態と取消、期限、認証の実値を確認します。現時点の地域設定はコード種別とコードの完全一致で判定します。

ローカルの AivisSpeech Engine 1.1.0-dev で話者一覧、二段階の合成、固定音声のキャッシュ、Windows 出力の準備まで確認しました。本番配信から Windows での出音までの通し試験、聞き取り、警報開始と割り込み停止の実測 p95 は、PR-C の試験配信と実際の運用端末で確認する項目です。AivisSpeech が準備できていない場合、本番の新規警報には内蔵の非言語音を使います。

## 参照資料

- `EarthQuickly_Aivis_Bridge_統合基本設計書_v1.0.docx` と `EarthQuickly_Aivis_Bridge_VPS連携詳細設計案_v1.1.md`
- QuickRelay PR #15 のマージ済みコミット `244265b`。 [QuickRelay PR #15](https://github.com/KANABUNN/QuickRelay/pull/15)
- Talker（旧 EarthQuickly-Aivis-Bridge）のコミット `7fd40d6` を参照し、話者の解決、合成、再生制御の考え方を C# に移植しました。内蔵音は QRV が PCM を生成します。
- 合成 API と Windows 出力 API の公式資料。 [AivisSpeech Engine API](https://aivis-project.github.io/AivisSpeech-Engine/api/) ・ [Microsoft WinMM API](https://learn.microsoft.com/windows/win32/api/mmeapi/)
