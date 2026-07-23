# Windows Serverテスト環境への導入手順

## 1. 目的と構成

本書は、SyncCoordinator WebとWorkerを同じWindows Serverへ配置し、Webをサーバー内からだけ利用するテスト環境の導入手順を示す。

例では次の値を使用する。環境に合わせて読み替えること。

| 項目 | 例 |
|---|---|
| 管理DB | SQL Server / `SyncCoordinator` |
| Web配置先 | `D:\SynCo\Web` |
| Worker配置先 | `D:\SynCo\Worker` |
| Data Protection Key Ring | `D:\SynCo\keys` |
| IISサイト／App Pool | `SynCo` |
| IISバインド | `http://127.0.0.1:5000` |

管理DBと業務DBの用途は異なる。

- 管理DB`SyncCoordinator`: システム設定、同期ルール、暗号化した接続情報、Checkpoint、Inbox、Snapshotなどを保存する。EF Core migrationで構築する。
- 業務DB: 同期元／同期先の既存テストDBを使用する。管理画面が生成するSQLを確認して、同期補助テーブルとTriggerを後から配備する。

Workerは最後まで起動しない。先に管理DB、Web、業務DBの準備と同期ルールの検証を完了する。

## 2. 前提条件

- ビルドPCにリポジトリ指定の.NET SDKがインストールされている。
- Windows ServerでIISを利用できる。
- Windows Serverに対応する.NET Hosting Bundleがインストールされている。自己完結型publishでも、IISでホストするWebにはASP.NET Core Moduleが必要である。
- 管理DB用SQL Serverと、同期対象のテスト用業務DBへ接続できる。
- コマンドは特記がなければリポジトリ直下で実行する。
- WebとWorkerは同じ管理DBと同じKey Ringを使用する。

## 3. 管理DBを準備する

SQL Serverに空の管理DB`SyncCoordinator`を作成する。管理DBのスキーマは手作業で作成せず、migration bundleで適用する。

### 3.1 migration bundleを作成する

ローカルツールを復元する。

```powershell
dotnet tool restore
```

`win-x64`用の資産ファイルを先に生成する。この手順がないと、`project.assets.json`に`net10.0/win-x64`ターゲットがないという`NETSDK1047`が発生する場合がある。

```powershell
dotnet restore src\SyncCoordinator.Worker\SyncCoordinator.Worker.csproj --runtime win-x64
```

migration bundleを作成する。

```powershell
dotnet ef migrations bundle --project src\SyncCoordinator.Infrastructure\SyncCoordinator.Infrastructure.csproj --startup-project src\SyncCoordinator.Worker\SyncCoordinator.Worker.csproj --context CoordinatorDbContext --runtime win-x64 --self-contained --output artifacts\SyncCoordinator.DbMigrate.exe
```

出力済みファイルを置き換える場合は、末尾に`--force`を追加する。

### 3.2 管理DBへ適用する

`SyncCoordinator.DbMigrate.exe`をSQL Serverへ接続できるWindows Serverへコピーし、接続文字列を指定して実行する。

```powershell
.\SyncCoordinator.DbMigrate.exe --connection "Server=SQLSERVER;Database=SyncCoordinator;Integrated Security=true;Encrypt=true;TrustServerCertificate=true"
```

`SQLSERVER`は実際のSQL Server名またはインスタンス名へ置き換える。本番相当の証明書を使用できる場合は`TrustServerCertificate=false`とする。

WebとWorkerでは、自動migrationを無効のままにする。

```json
"CoordinatorDatabase": {
  "ApplyMigrations": false,
  "SeedDemoData": false
}
```

## 4. Webをpublishする

ビルドPCで自己完結型publishを作成する。Windows Serverへソースコードや.NET SDKを配置する必要はない。

```powershell
dotnet publish src\SyncCoordinator.Web\SyncCoordinator.Web.csproj -c Release -r win-x64 --self-contained true -o artifacts\Web
```

`artifacts\Web`の中身をWindows Serverの`D:\SynCo\Web`へコピーする。`web.config`を含むpublish出力一式を配置し、ソースディレクトリをIISの物理パスにしない。

## 5. WebのProduction設定を作成する

`D:\SynCo\Web\appsettings.Production.json`を作成する。Windows Server上の既定環境名は`Production`である。

```json
{
  "ConnectionStrings": {
    "coordinator-db": "Server=SQLSERVER;Database=SyncCoordinator;Integrated Security=true;Encrypt=true;TrustServerCertificate=true"
  },
  "CoordinatorDatabase": {
    "ApplyMigrations": false,
    "SeedDemoData": false
  },
  "DatabaseDeployment": {
    "AllowDirectApply": false
  },
  "DataProtection": {
    "KeyRingPath": "D:/SynCo/keys"
  }
}
```

JSON内のWindowsパスには`D:/SynCo/keys`のように`/`を使用すると、バックスラッシュのエスケープミスを避けられる。`D:\\SynCo\\keys`も有効だが、`D:\SynCo\keys`はJSONとして不正である。SQL Serverの名前付きインスタンスをJSONへ書く場合は、`SERVER\\SQLEXPRESS`のように`\`を二重化する。

設定ファイルは起動前に検証する。

```powershell
Get-Content D:\SynCo\Web\appsettings.Production.json -Raw | ConvertFrom-Json
```

## 6. Key Ringを準備する

Webが業務DB接続情報やWebhook秘密鍵を暗号化し、Workerが同じ情報を復号するため、両プロセスが同じKey Ringを使用する。

```powershell
New-Item -ItemType Directory -Path "D:\SynCo\keys" -Force
```

現在の実装ではKey RingのXMLを追加の証明書等で暗号化しないため、フォルダーACLで保護する。WebとWorkerの実行アカウント以外へ不要な権限を与えない。

## 7. IISへWebを登録する

### 7.1 App Pool

App Pool`SynCo`を作成し、次を設定する。

- .NET CLR Version: `No Managed Code`
- Managed Pipeline Mode: `Integrated`
- Enable 32-Bit Applications: `False`
- Identity: `ApplicationPoolIdentity`、またはテスト環境用の専用サービスアカウント

### 7.2 サイトとlocalhost限定バインド

IISサイト`SynCo`を作成する。

- Physical Path: `D:\SynCo\Web`
- Application Pool: `SynCo`
- Type: `http`
- IP Address: `127.0.0.1`
- Port: `5000`
- Host name: 空欄

`すべて未割り当て`やLAN側IPのバインドは削除する。これによりWebはサーバー自身からだけ利用できる。Windows FirewallでTCP 5000を開放しない。

バインドを確認する。

```powershell
Get-WebBinding -Name "SynCo"
```

`bindingInformation`が`127.0.0.1:5000:`であればよい。`localhost`がIPv6の`::1`へ解決される環境では接続できないことがあるため、確認と初期設定には`127.0.0.1`を明示する。

### 7.3 フォルダー権限

Application Pool仮想アカウントの名前解決は`AppHostSvc`が担当する。`icacls`で「アカウント名とセキュリティIDの間のマッピング」エラーが出た場合は、サービス状態とApp PoolのIdentityを確認する。

```powershell
Get-Service AppHostSvc
```

停止中なら起動する。

```powershell
Start-Service AppHostSvc
```

Webへ読取り／実行、Key Ringへ変更権限を付与する。

```powershell
icacls "D:\SynCo\Web" /grant "IIS AppPool\SynCo:(OI)(CI)RX"
```

```powershell
icacls "D:\SynCo\keys" /grant "IIS AppPool\SynCo:(OI)(CI)M"
```

App Poolが専用アカウントで動作する場合は、`IIS AppPool\SynCo`ではなくそのアカウントへ付与する。

## 8. IIS実行アカウントへ管理DB権限を付与する

`Integrated Security=true`の場合、IISでWebを動かすIdentityにも管理DBの読書き権限が必要である。SQL ServerがWebと同じサーバーにあり、`ApplicationPoolIdentity`を使用する例を示す。

```sql
CREATE LOGIN [IIS APPPOOL\SynCo] FROM WINDOWS;
USE [SyncCoordinator];
CREATE USER [IIS APPPOOL\SynCo] FOR LOGIN [IIS APPPOOL\SynCo];
ALTER ROLE [db_datareader] ADD MEMBER [IIS APPPOOL\SynCo];
ALTER ROLE [db_datawriter] ADD MEMBER [IIS APPPOOL\SynCo];
```

既にLOGINまたはUSERが存在する場合は、該当する`CREATE`文を実行しない。SQL Serverが別ホストの場合、Application Pool仮想アカウントはネットワーク上でWebサーバーのコンピューターアカウントとして動作する。専用ドメインアカウントをApp Pool Identityに設定する方法を優先し、そのアカウントへ管理DB権限を付与する。

## 9. Webを起動して初期設定する

App Poolを再起動する。

```powershell
Restart-WebAppPool -Name "SynCo"
```

状態を確認する。

```powershell
Get-WebAppPoolState -Name "SynCo"
```

Windows Server自身のブラウザで次を開く。

```text
http://127.0.0.1:5000/account/setup
```

管理者の初期設定とパスワードリセットはlocalhost限定である。初期パスワード設定後は`/login`からログインする。

画面がグレー背景だけになる場合は、CSSまでは取得できているが、Blazorの初回描画時に管理DBアクセス等で失敗している可能性がある。`SyncCoordinator.Web.exe`の直接実行は診断に使用できるが、IISサイトと同時にポート5000を使用しない。

```powershell
Set-Location D:\SynCo\Web
```

```powershell
.\SyncCoordinator.Web.exe
```

直接実行で`Now listening on: http://localhost:5000`が表示されればプロセス起動は正常である。診断後は`Ctrl+C`で終了する。

## 10. システムと同期ルールを設定する

Webで次の順に設定する。Workerはまだ起動しない。

1. 同期元システムを登録する。
2. 同期先システムを登録する。
3. 両方の業務DB接続をテストする。
4. 同期ルールを作成する。
5. テーブルと列をマッピングする。
6. 業務DB配備SQLを生成する。
7. DBAがSQLを確認し、対象業務DBへ適用する。
8. Webで配備状態を検証して`Prepared`にする。
9. 全体一時停止またはルール無効の状態を維持する。

業務DBには`SyncChangeQueue`、`SyncAppliedMessage`、`SyncEntityOrigin`、`SyncDeleteTombstone`、`SyncCoordinatorDeployment`、変更検知Triggerが追加される。既存業務テーブル自体は作り直さない。

## 11. Workerをpublishして配置する

ビルドPCで自己完結型publishを作成する。

```powershell
dotnet publish src\SyncCoordinator.Worker\SyncCoordinator.Worker.csproj -c Release -r win-x64 --self-contained true -o artifacts\Worker
```

`artifacts\Worker`の中身を`D:\SynCo\Worker`へコピーし、`appsettings.Production.json`を作成する。

```json
{
  "ConnectionStrings": {
    "coordinator-db": "Server=SQLSERVER;Database=SyncCoordinator;Integrated Security=true;Encrypt=true;TrustServerCertificate=true"
  },
  "CoordinatorDatabase": {
    "ApplyMigrations": false,
    "SeedDemoData": false
  },
  "DataProtection": {
    "KeyRingPath": "D:/SynCo/keys"
  }
}
```

Workerのサービスアカウントへ次の権限を付与する。

- `D:\SynCo\Worker`: 読取り／実行
- `D:\SynCo\keys`: 読取り／書込み／変更
- 管理DB: 必要な読書き
- 各業務DB: Queue読取り、同期対象行の読書き、同期補助テーブルの読書き

通常のWorkerアカウントへ業務DBのDDL権限を与えない。

Windows Serviceの登録名はコードに合わせて`SyncCoordinator Worker`とする。パスワードをコマンド履歴へ残さないよう、登録後の「ログオン」アカウント設定はサービス管理画面等で行う。

```powershell
sc.exe create "SyncCoordinator Worker" binPath= "D:\SynCo\Worker\SyncCoordinator.Worker.exe" start= demand
```

サービスアカウントと権限を設定し、Webで同期ルールが`Prepared`であることを確認してから起動する。最初はWorkerを1インスタンスだけ動かす。

## 12. 動作確認

1. Webで全体一時停止を解除し、対象ルールを有効化する。
2. 同期元のテストレコードを1件だけ更新する。
3. `SyncChangeQueue`へ通知が追加されたことを確認する。
4. 同期先のレコードが更新されたことを確認する。
5. WebのInbox、Checkpoint、システムイベントを確認する。
6. 同じ変更が重複適用されていないことを確認する。
7. 問題がなければ対象データを段階的に増やす。

## 13. よくあるエラー

### `NETSDK1047`で`net10.0/win-x64`がない

bundle作成前に次を実行する。

```powershell
dotnet restore src\SyncCoordinator.Worker\SyncCoordinator.Worker.csproj --runtime win-x64
```

### `Failed to load configuration`／`invalid escapable character`

JSON内の`\`が正しくエスケープされていない。Windowsパスは`D:/SynCo/keys`形式を使用する。名前付きSQL Serverは`SERVER\\SQLEXPRESS`と記述する。

### IISの`HTTP Error 500.30`

Webプロセスが起動中に終了している。配置先で実行ファイルを直接起動し、例外を確認する。

```powershell
.\SyncCoordinator.Web.exe
```

代表的な原因は設定JSON、Hosting Bundle、App Poolのx64設定、Key Ring ACLである。

### 背景だけ表示される

初回描画時の管理DBアクセス失敗を疑う。IIS App Pool Identityへ管理DBの読書き権限があることを確認する。また、初期設定はサーバー自身から`http://127.0.0.1:5000/account/setup`を開く。

### `IIS AppPool\SynCo`をSIDへ変換できない

App Pool名、Identity、`AppHostSvc`の状態を確認する。`ApplicationPoolIdentity`以外を使用している場合は実際の実行アカウントへACLを付与する。
