# QuickRelay Voice

QuickRelay Voice（QRV）は、QuickRelay の緊急地震速報（EEW）を Windows で受信し、AivisSpeech で読み上げるアプリです。警報、警報地域の追加、対象となる予報、明示取消を扱います。配信の接続状態、上流と取込の状態、時刻差、音声合成、音声出力を画面で確認できます。

## 起動

公開フォルダーから起動する場合は、Windows x64 用の .NET 10 Desktop Runtime を入れ、`QuickRelay Voice.exe` を実行します。開発環境からは .NET 10 SDK で実行できます。

```powershell
dotnet run --project .\QuickRelay.Voice\QuickRelay.Voice.csproj
```

1. AivisSpeech を起動します。QRV の既定ポートは `10101` です。「一覧を取得」で話者スタイルを選び、「テスト再生」で Windows の既定の音声出力を確認できます。スタイルIDを空欄にすると、利用可能な「ノーマル」スタイルを優先します。
2. QuickRelay EEWブリッジの `wss://ホスト/v1/eew` と端末用 Bearer トークンを指定します。PR-Cのサーバーが提供する配信環境は `production` です。「消音で受信」は新規設定時に有効です。配信の初回確認は消音のまま「設定を保存して接続」を選び、同期完了と上流・取込の状態を画面で確認します。
3. 警報の対象は既定で全国です。「全国の警報を対象にする」を外すと、対象地域に絞れます。地域欄には配信側で定義された `codeType:code` を1行に1件入力します。予報の読み上げは既定で無効です。有効にすると最低震度と続報の震度差を設定できます。

消音中も状態を更新し、有効な読み上げ候補を最近の動作欄に表示します。消音を解除すると、その後に成立した候補から判定します。同期中や接続先の上流・取込・時刻に異常がある場合は音声を保留します。再接続時のスナップショットと再送は、明示取消を含めて無音で適用します。QRV がすでに報知した系列のライブ取消は、開始期限内に条件がそろえば一度読み上げます。

## ビルドと確認

リポジトリのルートで実行します。

```powershell
dotnet build .\QuickRelay.Voice\QuickRelay.Voice.csproj -c Release
dotnet run --project .\QuickRelay.Voice.Checks\QuickRelay.Voice.Checks.csproj -c Release
dotnet publish .\QuickRelay.Voice\QuickRelay.Voice.csproj -c Release -r win-x64 --self-contained false -o .\artifacts\publish
& '.\artifacts\publish\QuickRelay Voice.exe'
```

`QuickRelay.Voice.Checks` は、QuickRelay PR #18に含まれる正式な配信例6件と、ローカルの TLS WSS フィクスチャを使います。購読、警報地域の出典、予報品質、証明書検証、Bearer認証、同期と再開、クローズコード、発話条件を確認できます。

## 保存先

- `%LOCALAPPDATA%\QuickRelay Voice\settings.json` に配信先、配信環境、対象地域、消音、予報、AivisSpeech の設定を保存します。
- 同じフォルダーの `feed-prc-*.json` に配信の再開位置と地震の状態を、`speech-*.json` に重複防止用の報知開始記録を保存します。0.1.0からの更新後は初回にスナップショットで同期し、報知開始記録を引き継ぎます。
- トークンは Windows 資格情報マネージャーの汎用資格情報 `QuickRelay Voice/<接続先・環境のハッシュ>` に保存します。
- 4410を受信したときは、失効済みトークンを識別するハッシュを `revoked-*.json` に保存します。アプリを再起動した場合も新しいトークンが必要です。
- 同じフォルダーに `readings.json` を置くと、地名の音声表記を変更できます。JSON オブジェクトに `{"例示地域":"れいじちいき"}` の形式で指定すると、次回接続時に読み込みます。

出力の直前に報知開始記録を保存します。記録後から実際の出音までに異常終了した場合は、再起動後の重複防止を優先するため、その音声が流れないことがあります。

## QuickRelay PR-C との接続

QuickRelay PR #18のマージコミット `bbea0b9` に含まれる正式なWSS v1契約に対応します。地域設定は `codeType` とコードの完全一致で判定します。予報音声には予報品質が `estimated` で、規模の推定に使った観測点が2点以上と配信されたものを使用します。 [正式スキーマ](https://github.com/KANABUNN/QuickRelay/blob/bbea0b9380447db48f0885a5a027264cd5b40dd3/contracts/eew-wss.schema.json)

4409を受信すると保存カーソルを解除し、接続し直して全同期します。4410では自動再接続を止め、新しいトークンの入力を求めます。4400、4403、4413では購読、認可、上限エラーを表示して停止します。429では1分待ってから再接続します。 [QuickRelayのWSS運用資料](https://github.com/KANABUNN/QuickRelay/blob/bbea0b9380447db48f0885a5a027264cd5b40dd3/docs/eew-bridge.md)

ローカルの AivisSpeech Engine 1.1.0-dev で話者一覧、二段階の合成、固定音声のキャッシュ、Windows 出力の準備まで確認しました。VPS上の配信からWindowsの出音までの通し試験、聞き取り、警報開始と割り込み停止の実測 p95 は、配信を展開した環境と運用端末で確認する項目です。AivisSpeechが準備できていない場合、新規警報には内蔵の非言語音を使います。

## 参照資料

- `EarthQuickly_Aivis_Bridge_統合基本設計書_v1.0.docx` と `EarthQuickly_Aivis_Bridge_VPS連携詳細設計案_v1.1.md`
- QuickRelay PR #15 のマージ済みコミット `244265b`。 [QuickRelay PR #15](https://github.com/KANABUNN/QuickRelay/pull/15)
- Talker（旧 EarthQuickly-Aivis-Bridge）のコミット `7fd40d6` を参照し、話者の解決、合成、再生制御の考え方を C# に移植しました。内蔵音は QRV が PCM を生成します。
- 合成 API と Windows 出力 API の公式資料。 [AivisSpeech Engine API](https://aivis-project.github.io/AivisSpeech-Engine/api/) ・ [Microsoft WinMM API](https://learn.microsoft.com/windows/win32/api/mmeapi/)
