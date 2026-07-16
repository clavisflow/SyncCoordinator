# Webhook連携ガイド

SyncCoordinatorは、同期結果やシステム状態を外部システムへHTTP POSTで通知します。この文書は、Webhook受信側を実装するための公開契約です。

管理画面での通知先の登録、テスト送信、配送履歴の確認方法は[操作マニュアル](user-guide.md#通知を設定する)を参照してください。

## 基本契約

- リクエストメソッドは`POST`です。
- `Content-Type`は`application/json; charset=utf-8`です。
- HTTP 2xxを配送成功とみなします。レスポンス本文は使用しません。
- 配送はat-least-onceです。同じイベントが複数回届く可能性があります。
- イベントの到着順は保証しません。
- 受信側は`eventId`を永続化し、処理済みイベントを重複処理しないでください。
- payloadには同期メタデータだけを含めます。業務データ、接続情報、例外詳細は含めません。
- payloadの日時はUTCのISO 8601形式です。

## イベント種別

| `eventType` | 発生条件 |
| --- | --- |
| `sync.upserted` | 同期先への追加または更新が完了した |
| `sync.deleted` | 同期先への削除が完了した |
| `conflict.detected` | 同期競合を検出して競合履歴を記録した |
| `sync.failed` | 同期処理が失敗、または値検証エラーにより保留された |
| `system.paused` | 管理画面でシステムを一時停止した |
| `system.resumed` | 管理画面でシステムを再開した |
| `webhook.test` | 管理画面からテスト通知を登録した |

競合を検出した後に自動解決して同期先へ適用した場合は、`conflict.detected`に加えて`sync.upserted`または`sync.deleted`が発生することがあります。イベントはそれぞれ独立して処理してください。

## HTTPヘッダー

| ヘッダー | 必須 | 内容 |
| --- | --- | --- |
| `Webhook-Id` | はい | payloadの`eventId`と同じUUID |
| `Webhook-Timestamp` | はい | 配送試行時刻を表すUnix timestamp（秒） |
| `Webhook-Signature` | 署名有効時 | `v1=`で始まるHMAC-SHA256署名 |

再試行では`Webhook-Id`とpayloadは変わりません。`Webhook-Timestamp`は配送試行ごとに更新されるため、`Webhook-Signature`も変わります。

## Payload version 1

すべてのイベントで次のフィールドを出力します。イベントに関係しないフィールドは省略せず`null`にします。

| フィールド | JSON型 | nullable | 内容 |
| --- | --- | --- | --- |
| `schemaVersion` | number | いいえ | 現在は`1` |
| `eventId` | string | いいえ | イベントを一意に識別するUUID |
| `eventType` | string | いいえ | イベント種別 |
| `occurredAt` | string | いいえ | イベント発生日時。UTCのISO 8601形式 |
| `routeId` | string | はい | 同期ルールのUUID |
| `routeName` | string | はい | 同期ルール名 |
| `sourceSystem` | string | はい | 同期元システムコード |
| `destinationSystem` | string | はい | 同期先システムコード |
| `entityType` | string | はい | 同期対象のエンティティ種別 |
| `entityId` | string | はい | 同期対象を識別する値。UUIDや数値に限定されない |
| `sourceMessageId` | string | はい | 同期元の変更通知を識別するUUID |
| `deliveryMessageId` | string | はい | 同期先への配送を識別するUUID |
| `systemCode` | string | はい | 一時停止または再開したシステムコード |
| `systemName` | string | はい | 一時停止または再開したシステム表示名 |

### イベント別フィールド

`eventId`、`eventType`、`occurredAt`はすべてのイベントで値を持ちます。

| イベント | ルート・同期元・同期先・対象・メッセージID | `systemCode`、`systemName` |
| --- | --- | --- |
| `sync.upserted` | 値あり | `null` |
| `sync.deleted` | 値あり | `null` |
| `conflict.detected` | 値あり | `null` |
| `sync.failed` | 値あり | `null` |
| `system.paused` | `null` | 値あり |
| `system.resumed` | `null` | 値あり |
| `webhook.test` | `null` | `null` |

## Payload例

### 同期完了

```json
{
  "schemaVersion": 1,
  "eventId": "90e757b1-c349-4c68-b468-8217ef0ac253",
  "eventType": "sync.upserted",
  "occurredAt": "2026-07-16T03:04:05+00:00",
  "routeId": "bc706fcc-bbd0-491d-a88f-317fa510c64a",
  "routeName": "CRMからField Serviceへ作業指示を同期",
  "sourceSystem": "crm",
  "destinationSystem": "field-service",
  "entityType": "work-order",
  "entityId": "WO-12345",
  "sourceMessageId": "432ef5ed-003e-4ad1-8d78-64b366010f92",
  "deliveryMessageId": "7273b025-218c-46e1-81fc-ab417c014e82",
  "systemCode": null,
  "systemName": null
}
```

`sync.deleted`、`conflict.detected`、`sync.failed`も同じフィールド構成で、`eventType`が変わります。`sync.failed`には例外メッセージや業務payloadを含めません。詳細は管理画面の同期履歴、保留データ、システムイベントで確認してください。

### システムの一時停止

```json
{
  "schemaVersion": 1,
  "eventId": "f253e5d3-611c-4c01-b7df-2cc952a9e3ac",
  "eventType": "system.paused",
  "occurredAt": "2026-07-16T03:10:00+00:00",
  "routeId": null,
  "routeName": null,
  "sourceSystem": null,
  "destinationSystem": null,
  "entityType": null,
  "entityId": null,
  "sourceMessageId": null,
  "deliveryMessageId": null,
  "systemCode": "crm",
  "systemName": "CRM"
}
```

### テスト通知

```json
{
  "schemaVersion": 1,
  "eventId": "ad03d5c8-c38c-42d3-a5e6-d4db444d3bd4",
  "eventType": "webhook.test",
  "occurredAt": "2026-07-16T03:15:00+00:00",
  "routeId": null,
  "routeName": null,
  "sourceSystem": null,
  "destinationSystem": null,
  "entityType": null,
  "entityId": null,
  "sourceMessageId": null,
  "deliveryMessageId": null,
  "systemCode": null,
  "systemName": null
}
```

## 署名検証

新しい通知先ではHMAC-SHA256署名が既定で有効です。通知先の保存時に表示される署名秘密鍵は、ランダムな32 byte値をBase64で表した文字列です。秘密鍵は表示された時点で安全な構成ソースへ保存してください。同じ値を管理画面から再表示することはできません。

署名は次の手順で計算します。

1. `Webhook-Timestamp`の文字列を取得する。
2. HTTP request bodyを変換せず、受信したUTF-8文字列のまま取得する。
3. `timestamp + "." + rawBody`をUTF-8へ変換する。
4. Base64デコードした署名秘密鍵を使い、HMAC-SHA256を計算する。
5. 計算結果をBase64エンコードし、先頭に`v1=`を付ける。
6. 結果を`Webhook-Signature`と定数時間比較する。

```text
signedContent = UTF8(Webhook-Timestamp + "." + rawRequestBody)
secretBytes   = Base64Decode(signatureSecret)
signature     = "v1=" + Base64Encode(HMAC-SHA256(secretBytes, signedContent))
```

JSONを解析して再シリアライズすると、空白やプロパティ表現が変わり署名が一致しない場合があります。必ず解析前のrequest bodyで検証してください。

リプレイ攻撃を防ぐため、署名に加えて次を検証してください。

- `Webhook-Timestamp`が現在時刻から許容範囲内である。目安は前後5分です。
- `Webhook-Id`とpayloadの`eventId`が一致する。
- `eventId`をまだ処理していない。

### C#での検証例

次の例は署名検証の最小部分です。実際の受信処理では、処理済み`eventId`をデータベース等へ永続化してください。

```csharp
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

app.MapPost("/webhooks/sync-coordinator", async (
    HttpRequest request,
    IConfiguration configuration) =>
{
    using var reader = new StreamReader(request.Body, Encoding.UTF8);
    var rawBody = await reader.ReadToEndAsync();

    var timestamp = request.Headers["Webhook-Timestamp"].ToString();
    var providedSignature = request.Headers["Webhook-Signature"].ToString();
    var webhookId = request.Headers["Webhook-Id"].ToString();
    if (!long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var unixSeconds) ||
        !providedSignature.StartsWith("v1=", StringComparison.Ordinal))
    {
        return Results.Unauthorized();
    }

    var nowUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    if (unixSeconds < nowUnixSeconds - 300 || unixSeconds > nowUnixSeconds + 300)
    {
        return Results.Unauthorized();
    }

    byte[] secret;
    byte[] providedHash;
    try
    {
        secret = Convert.FromBase64String(
            configuration["SyncCoordinator:WebhookSecret"]
            ?? throw new InvalidOperationException("Webhook secret is not configured."));
        providedHash = Convert.FromBase64String(providedSignature[3..]);
    }
    catch (FormatException)
    {
        return Results.Unauthorized();
    }

    var signedContent = Encoding.UTF8.GetBytes(timestamp + "." + rawBody);
    var expectedHash = HMACSHA256.HashData(secret, signedContent);
    if (!CryptographicOperations.FixedTimeEquals(expectedHash, providedHash))
    {
        return Results.Unauthorized();
    }

    using var payload = JsonDocument.Parse(rawBody);
    var eventId = payload.RootElement.GetProperty("eventId").GetGuid();
    if (!Guid.TryParse(webhookId, out var headerEventId) || headerEventId != eventId)
    {
        return Results.BadRequest();
    }

    // eventIdが処理済みなら、処理を繰り返さずHTTP 2xxを返す。
    // 未処理なら、業務処理とeventIdの保存を同じトランザクションで行う。
    return Results.Ok();
});
```

## 配送と再試行

接続失敗、タイムアウト、またはHTTP 2xx以外のレスポンスでは、次の間隔で再試行します。

| 失敗後 | 次回試行まで |
| --- | --- |
| 1回目 | 1分 |
| 2回目 | 5分 |
| 3回目 | 30分 |
| 4回目 | 2時間 |
| 5回目 | 6時間 |
| 6回目 | 12時間 |

初回を含む最大7回の試行後、配送状態を`Failed`にします。タイムアウトは10秒で、HTTPリダイレクトには追従しません。Webhookの失敗は同期処理を停止させません。

通知先がイベント発生時点で無効、またはそのイベント種別を購読していない場合、その通知先向けの配送は作成されません。後から有効化または購読を追加しても、過去のイベントは遡って配送されません。

## 重複排除

HTTP 2xxのレスポンスがSyncCoordinatorへ届かなかった場合、受信側で処理が完了していても再試行されます。受信側では次の流れを推奨します。

1. `eventId`を一意キーとして処理済みイベントを検索する。
2. 処理済みなら副作用を繰り返さず、HTTP 2xxを返す。
3. 未処理なら、通知に対する処理と`eventId`の保存を可能な限り同じトランザクションで行う。
4. 処理が完了した場合だけHTTP 2xxを返す。

イベント順序には依存せず、通知を最新状態の確認契機として扱ってください。必要な業務データは、認可された業務システムのAPIまたはデータベースから取得してください。

## バージョン互換性

受信側は`schemaVersion`を確認し、未知のバージョンを暗黙に処理しないでください。同じschema versionへ将来フィールドが追加される可能性があるため、未知のフィールドは無視してください。既存フィールドの削除、型変更、意味の非互換変更が必要になった場合は`schemaVersion`を更新します。

## セキュリティ

- 閉域LAN以外ではHTTPSを使用してください。
- 署名を有効にし、request bodyを処理する前に検証してください。
- 署名秘密鍵と、認証トークンを含む通知先URLを秘密情報として扱ってください。
- 秘密鍵や通知先URLをソースコード、チケット、チャット、スクリーンショットへ残さないでください。
- 受信エンドポイントではrequest bodyのサイズ制限、タイムアウト、レート制限を設定してください。
- payloadの識別子をSQLやURLへ使用する場合も、信頼済み入力として扱わず検証してください。
- 秘密鍵を再生成した場合は、受信側の設定を同時に更新してください。

## 受信側実装のチェックリスト

- [ ] HTTPSの受信URLを用意した
- [ ] `Webhook-Signature`を未加工のrequest bodyで検証している
- [ ] `Webhook-Timestamp`の許容時間を検証している
- [ ] `Webhook-Id`とpayloadの`eventId`を照合している
- [ ] `eventId`を永続化して重複排除している
- [ ] 未知の`eventType`と未知のフィールドを安全に無視できる
- [ ] 処理完了時だけHTTP 2xxを返している
- [ ] 10秒以内に応答できない処理は内部キューへ引き渡している
- [ ] 管理画面から`webhook.test`を送り、配送履歴を確認した
