# CRM demo

顧客ポータルから同期された問い合わせ（Support Case）を受付・回答し、訪問作業指示（Work Order）を作成するデモ用CRMです。

## 前提

- .NET 10
- SQL Server
- `dbo.SupportCase`、`dbo.WorkOrder`、`dbo.WorkOrderAssignment`が作成済みであること
- `demo-crm-db` という名前の接続文字列が設定されていること

このアプリは起動時にDB、Docker、SQL Serverを起動せず、DDLも実行しません。接続文字列やDBが利用できない場合は、各画面に案内を表示します。

接続文字列はソース管理対象の設定ファイルには保存せず、Aspireのリソース参照、環境変数、またはUser Secretsで指定します。

```powershell
dotnet user-secrets init
dotnet user-secrets set "ConnectionStrings:demo-crm-db" "Server=...;Database=...;..."
```

## 起動

```powershell
dotnet run --project demos/Crm/SyncCoordinator.Demo.Crm.csproj
```

このCRMは正規化された`dbo.SupportCase`、`dbo.WorkOrder`、`dbo.WorkOrderAssignment`を直接読み書きします。画面では受付情報を作業指示へ結合して表示しますが、重複列としては保存しません。`WorkOrderAssignment.StaffNo`が1件以上ある作業指示だけがField Serviceの同期対象です。同期補助テーブルとTriggerはCoordinator管理画面から配備します。
