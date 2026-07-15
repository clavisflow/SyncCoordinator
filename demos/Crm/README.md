# CRM demo

顧客ポータルから同期された問い合わせ（Support Case）を受付・回答し、訪問作業指示（Work Order）を作成するデモ用CRMです。

## 前提

- .NET 10
- SQL Server
- `dbo.SupportCase`と`dbo.WorkOrder`が作成済みであること
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

このCRMは`dbo.SupportCase`と`dbo.WorkOrder`を直接読み書きします。`SupportCase`はPortalと異なる`CaseRef`、`ContactName`、`WorkflowState`などの物理列名を持ち、Repositoryで画面モデルへaliasします。問い合わせの更新では同期元を示す`SourceCode`を保持し、CRMから作成する作業指示には`CRM`を設定します。同期補助テーブルとTriggerはCoordinator管理画面から配備します。
