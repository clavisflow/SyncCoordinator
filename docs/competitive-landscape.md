# SyncCoordinator 競合・参考製品調査

- 状態: Draft
- 調査日: 2026-07-16
- 目的: 製品ポジショニング、製品サイト、デモ動画の訴求とビジュアルを競合の定型から差別化する
- 調査範囲: 各製品の公式サイト、公式ドキュメント、公式製品資料

## 1. 結論

SyncCoordinatorの最も近い直接競合はSymmetricDSである。

SymmetricDSは、異種DB間の一方向・双方向同期、Triggerによる変更取得、変換、ネットワーク障害からの回復、競合検出、自動または手動の競合解決を提供する。接続先、初期ロード、分散拠点、比較・修復、性能・拡張性ではSyncCoordinatorより広く成熟している。

したがって、SyncCoordinatorを単に「異種DBを双方向同期できる製品」とだけ説明しても、独自の位置は作れない。

一方、調査した多くの製品は、次のいずれかを主役にしている。

- 大量のConnectorと、任意のSourceから任意のDestinationへのデータ移動
- DWH、Data Lake、AIへデータを集約する分析系パイプライン
- ノーコードの汎用処理フロー作成
- 高性能、低遅延、大規模拠点数
- 導入社数、市場シェア、著名顧客、アナリスト評価

SyncCoordinatorが主役にすべきなのは、Connector数や汎用フローではない。

> 一つの業務レコードが、設計の異なる複数システムを移動し、両側で変更され、障害や競合が起きても、運用者が理由と採用値を追跡して安全に収束させられること。

サイトの再設計では「箱と矢印で製品全体を説明する」のではなく、「一つのレコードの移動と判断」を時間軸で見せる。

## 2. 競合の分類

### A. 直接競合

#### SymmetricDS

- 公式: [SymmetricDS](https://symmetricds.org/)
- 分類: 異種DB・分散拠点向けデータ同期／レプリケーション
- 主な訴求:
  - Multi-primary、双方向同期
  - 異種DB対応
  - Trigger型Change Data Capture
  - オフラインやネットワーク障害からの自動回復
  - 競合検出と自動／手動解決
  - 初期ロード、データ移行、比較・修復
  - 80以上のSource／Targetと大規模拠点
- Web上の見せ方:
  - まずEnterprise Data Platformという広いカテゴリーを宣言
  - Platform Capabilitiesを機能単位で列挙
  - Multi-Primary、Cloud Integration、Multi-tierなどの用途ごとに構成図を表示
  - Community／Pro、ドキュメント、Releaseを前面に出す
- SyncCoordinatorとの重なり: 非常に大きい
- SyncCoordinatorが正面から競わない領域:
  - Connector数
  - 数千拠点への配信
  - 初期ロード
  - 比較・修復
  - 長い製品実績
- 差を作れる可能性:
  - 日本語を含む管理画面中心の設定・運用
  - DB反映SQLを画面で確認し、検証後にだけルールを有効化する安全な導入手順
  - 物理列の型・長さを取得した方向別変換と書込み前検証
  - 競合検出後の現在値変化を再確認してから適用する個別解決
  - 同じレコードの後続競合をチェーンとして扱う運用モデル
  - 3つの実際の業務アプリを使った、理解可能な実行デモ

### B. 近接競合

#### CData Sync

- 公式: [CData Sync](https://www.cdata.com/sync/)
- 分類: Enterprise Data Replication、ETL／ELT、Reverse ETL
- 主な訴求:
  - 数百のSaaS、API、DB、DWH、Data Lake
  - CDC、動的Schema管理、変換
  - オンプレミス、Cloud、Hybrid配備
  - 高可用性、並列処理、監査ログ
  - Analytics、Operations、AIへのデータ配送
- 双方向:
  - 公式記事では、方向ごとに独立した2つのJobを動かす構成として説明される
  - Conflictを避けるためのフィルターや設計は利用者側で計画する
- Web上の見せ方:
  - Source群→製品→Destination群の大きな接続図
  - 「Continuous, flexible data replication at scale」のような規模と汎用性の見出し
  - Gartner、顧客事例、Connector数、Product Tourを早い段階で提示
  - Analytics、Operations、AIという広い用途を一つのページで扱う
- SyncCoordinatorとの差:
  - CDataは広いデータ移動プラットフォーム
  - SyncCoordinatorは一つのRoute内で双方向の観測値、Snapshot、Inbox、Conflictを一貫して管理する
  - SyncCoordinatorは業務レコードの競合を管理画面から解決することを中心に置ける

#### Qlik Talend Data Integration / Qlik CDC

- 公式: [Qlik CDC Streaming](https://www.qlik.com/us/products/qlik-data-streaming-cdc)
- 分類: Enterprise CDC、Data Integration、Analytics向けデータ移動
- 主な訴求:
  - Transaction LogベースのCDC
  - Cloud、DWH、Data Lakeへの継続的な配送
  - 大規模Enterprise、Analytics、Data Quality
- Web上の見せ方:
  - Real-time、Cloud、Analyticsを中心とした大企業向けの抽象度の高い訴求
  - 動画、Solution Brief、企業事例
- SyncCoordinatorとの差:
  - 運用DB同士を双方向に収束させる個別レコードの判断より、Data Platformへの移動が中心

#### Striim

- 公式: [Striim](https://www.striim.com/)
- 分類: Real-time Data Integration / Streaming / CDC
- 主な訴求:
  - 「Real-Time Data. Real-World AI.」
  - Database、Application、Cloud間のStreaming
  - Fortune 500の業界事例
  - AI、リアルタイム分析、意思決定
- Web上の見せ方:
  - 製品機能より、著名企業と業界成果を主役にする
  - AI、リアルタイム、顧客事例の大きな写真・ロゴ
- SyncCoordinatorとの差:
  - SyncCoordinatorは巨大なStreaming Platformを目指さず、RDB業務レコードの運用に範囲を絞る

### C. 開発者向け代替

#### Debezium

- 公式: [Debezium](https://debezium.io/)
- 分類: Open Source Change Data Capture Platform
- 主な訴求:
  - DB変更をEvent Streamへ変換するCDC基盤
  - Kafka Connect等と組み合わせる開発者向けBuilding Block
- Web上の見せ方:
  - 短い製品定義
  - Architecture、Documentation、Communityを中心にする
  - 商用製品サイトのような長いBenefit一覧を置かない
- SyncCoordinatorとの差:
  - Debeziumは変更イベントを届ける部品
  - SyncCoordinatorはMapping、Destination適用、二重適用防止、Conflict、運用画面までを一つの製品として扱う
- 参考にすべき点:
  - 無理に大きなマーケティング用語を使わず、技術的な正体を短く説明する姿勢

#### Airbyte

- 公式: [Airbyte Data Replication](https://airbyte.com/data-replication)
- 分類: Open Source / Cloud Data Movement、ELT、CDC
- 主な訴求:
  - 「The open standard for data movement」
  - 600以上のConnector
  - BatchまたはCDC、Open SourceまたはCloud
  - Source→Warehouse／LakeへのReplication
  - Connector Builder、Schema Change、Observability
- Web上の見せ方:
  - 短く強いCategory Claim
  - Connector Marketplaceを視覚的中心にする
  - 大きな実績数字、企業ロゴ、Open Source Community
  - 明るく現代的なProduct IllustrationとUI
- SyncCoordinatorとの差:
  - AirbyteはData Movementの広さを販売する
  - SyncCoordinatorはConnector数で競わず、双方向業務更新の収束と判断を販売する
- 参考にすべき点:
  - 一文でカテゴリーを取る見出し
  - Open Sourceであることをページ後半の補足ではなく、信頼理由として扱う構成
- 模倣しない点:
  - Connector Logo Cloud
  - 根拠のない大きな数字の帯
  - AIを主要用途へ無理に追加すること

#### Estuary

- 公式: [Estuary](https://estuary.dev/)
- 分類: CDC、Streaming、Batch ETL
- 主な訴求:
  - 「The Right Data. At the Right Time.」
  - Sub-100msからBatchまでのRight-time
  - Analytics、Operations、AI
- 参考にすべき点:
  - 「すべてReal-time」と言わず、用途に適した時間という考えを短い言葉にしている
- SyncCoordinatorへの示唆:
  - SyncCoordinatorも速度ではなく、安全な収束と運用可能性を短い言葉にできる

### D. 国内の汎用データ連携製品

#### ASTERIA Warp

- 公式: [ASTERIA Warp](https://www.asteria.com/jp/warp/)
- 分類: EAI／ESB、ノーコードデータ連携
- 主な訴求:
  - 19年連続国内シェアNo.1
  - 10,000社以上
  - 100種類以上の接続先
  - ノーコード、内製化、業務自動化
  - 月額価格を製品ページに掲載
- Web上の見せ方:
  - Hero直後に市場シェア、導入社数、Connector数の証拠
  - Badge、受賞、顧客満足度
  - Connector一覧、用途、価格、資料、相談、体験版を高密度に配置
- SyncCoordinatorとの差:
  - ASTERIAは汎用的なFlowを作成する製品
  - SyncCoordinatorは決められた同期モデルを安全に運用する製品
- 参考にすべき点:
  - 日本の情シスが確認したい、価格、動作環境、サポート、資料への明確な導線
  - 「何ができるか」だけでなく「採用してよい根拠」を近くに置く
- 現段階で模倣できない点:
  - 導入社数、シェア、顧客ロゴ、受賞

#### DataSpider Servista

- 公式: [DataSpider Servista](https://www.saison-technology.com/service/product/lineup/dataspider/feature/)
- 分類: On-premises EAI／ETL、ノーコードデータ連携
- 主な訴求:
  - GUIで「つくらずに、つなぐ」
  - 多数のAdapter
  - 大容量データの高速処理
  - Java 14,000 Step相当を13 Iconで構成できるという具体的な比較
- Web上の見せ方:
  - 抽象的な「簡単」ではなく、Code量とIcon数を比較する
  - 製品画面のFlowを価値の証拠にする
- SyncCoordinatorへの示唆:
  - 「安全」という抽象語だけで終えず、実際に何を保存・再確認するから安全なのかを一つの具体例で示す

#### Waha! Transformer

- 公式: [Waha! Transformer](https://waha-transformer.com/)
- 分類: 国産No-code ETL
- 主な訴求:
  - 25年以上、2,600 License
  - 大量データ高速処理
  - LegacyからCloudまで
  - Industry Case、資料、動画、相談
- Web上の見せ方:
  - 国内Enterprise Productらしい情報量の多い構成
  - History、Performance、Support、Case Studyを繰り返し信頼材料にする
- SyncCoordinatorとの差:
  - ETL処理の作成と大容量一括処理では競わない
  - 継続的な業務更新の競合と復旧を主役にする

#### HULFT Square

- 公式: [HULFT Square](https://www.hulft.com/help/en-us/HULFTSquare/Content/TOP/GettingStarted/ex_square.htm)
- 分類: Cloud iPaaS、Data Integration、File Transfer
- 主な訴求:
  - Cloud／On-premisesのData Integrationを集中管理
  - Connector、Mapper、Job、Monitoring
  - GUIでProcess FlowとData Flowを作成
- SyncCoordinatorとの差:
  - HULFT Squareは汎用Integration Scriptを作るPlatform
  - SyncCoordinatorは変更同期に特化し、Route、Mapping、Conflict、Inboxという固定モデルを提供する

## 3. 市場で繰り返されている製品ページの型

### 海外Data Platform型

1. 短い英語のCategory Claim
2. Source Logo群とDestination Logo群
3. 抽象的なPipeline図
4. Connector数、処理件数、導入社数
5. 機能Card
6. 著名顧客、Analyst Badge
7. Product Tour、Free Trial、Talk to Sales

該当例: Airbyte、CData、Striim、Estuary

### 国内Enterprise Software型

1. No.1、導入社数、受賞Badge
2. 「ノーコード」「誰でも簡単」
3. できることを多数列挙
4. Connector一覧
5. Use Caseと顧客事例
6. 価格、資料Download、相談、体験版
7. Supportと国内実績

該当例: ASTERIA Warp、DataSpider Servista、Waha! Transformer

### Open Source Infrastructure型

1. 技術的な一文定義
2. Architecture図
3. DocumentationとQuick Start
4. CommunityとRelease
5. 商用版との境界

該当例: Debezium、SymmetricDS

## 4. 現在のSyncCoordinatorサイトが似て見える理由

現在の初版は、海外Data Platform型の一般的な構造をほぼそのまま使用している。

- Heroの大見出し
- 右側のDB Nodeと矢印
- 4つの数字を並べるProof Strip
- 4枚のFeature Card
- Dark Sectionの処理Flow
- Product Screenshot
- 大きな最終CTA

配色はSyncCoordinator固有でも、情報構造が一般的なB2B SaaS Landing Pageであるため、別製品へLogoとCopyを差し替えても成立してしまう。

特に次の要素は削除または再考する。

- 実績ではない「3 RDB」「3-way」「HMAC」を実績数字のように並べる帯
- 実際のProduct UIではない、Heroの架空Status Panel
- 同じ大きさのFeature Cardによる機能の均等列挙
- 英語のMicrocopyを多数置いて雰囲気を作る方法
- DBの箱と矢印だけで双方向同期を説明する構図

## 5. SyncCoordinatorが取るべき独自方向

### 推奨: 「一つのレコードの旅」+「競合事件簿」

製品サイトを機能Catalogではなく、一つの業務レコードを追う記録として構成する。

主役は製品LogoでもDB Iconでもなく、次の具体的なレコードである。

```text
SupportCase / SC-20260716-001
Customer Portal → CRM → Field Service
```

#### 第一幕: 同じ業務、違う物理設計

左右にDB製品Logoを置くのではなく、同じレコードの物理列を並べる。

```text
MySQL                          SQL Server
case_number         →         CaseRef
customer_name       →         ContactName
status = accepted   →         StatusCode = InProgress
```

列名、最大長、Codeが実際に変わる様子を見せる。

#### 第二幕: 変更は一方向に流れない

Portal側とCRM側で同じFieldが変更され、時間軸が二つに分かれる。

```text
15:02 Portal: PreferredVisitDate = 7/20
15:04 CRM:    PreferredVisitDate = 7/22
```

ここで初めて「競合」という製品の核心を出す。

#### 第三幕: なぜ安全に解決できるか

抽象語ではなく、管理画面が保持する4つの値を見せる。

```text
Base       7/18
Incoming   7/20
Current    7/22
Adopted    7/20
```

「解決Buttonを押した後、WorkerがCurrentを再取得し、表示時から変わっていない場合だけ適用する」という一文を添える。

#### 第四幕: 障害後の再開

一般的な5 Step Architecture図ではなく、同じMessageの状態変化を時系列で示す。

```text
Attempt 1  Failed       15:08:02
Attempt 2  Completed    15:09:04
Delivery Message ID     同一
Destination Apply       1回
```

#### 第五幕: 実画面で証明

物語で概念を理解した後に、実際のDashboard、Mapping、Conflict Detail、Operationsを見せる。

### 視覚言語

- 管理画面と同じ「紙の台帳」「配線図」のモチーフを強める
- Cardの集合ではなく、一本の縦または横の時間軸を使う
- Database Iconより、Field名、Value、Timestamp、Stateを大きく扱う
- 装飾用の架空Statusは使わず、Demo Dataに存在する値だけを使う
- 英語Microcopyを減らし、日本語の業務語と物理Field名の対比をデザイン要素にする
- Motionを使う場合は、Logoや背景ではなくRecord Valueが変換・分岐・収束する箇所だけに使う

## 6. 採用する参考点

| 参考製品 | 採用する点 | 採用しない点 |
|---|---|---|
| SymmetricDS | 技術的正体をArchitectureとUse Caseで明示 | 広いEnterprise Platformを名乗ること |
| Debezium | 簡潔で正確なOpen Source Infrastructureの説明 | 開発者だけを対象にすること |
| Airbyte | 一文でCategoryを取るHeadline | Connector Cloud、規模の数字、AIへの拡張 |
| CData Sync | Product Tourと実画面でSetupを説明 | Source／Destinationを無限に並べる構図 |
| ASTERIA Warp | 価格・環境・Supportなど採用判断情報への導線 | No.1 Badge中心の構成 |
| DataSpider | 抽象価値を具体的な比較で証明 | 汎用Flow Designerを主役にすること |
| Waha! | 長期運用とSupportを信頼材料にする姿勢 | 情報を一ページへ過密に載せること |
| Estuary | CategoryのTrade-offを短い言葉にする | Low Latencyを中心価値にすること |

## 7. 再設計時の仮Headline

一般的な「異なるDBを安全に双方向同期」より、運用上の差を前に出す。

### 案A

#### 見出し

同じデータが、両側で変わったら。

#### 説明

SyncCoordinatorは、異なる業務DB間の変更を配送するだけでなく、変換、競合、再試行の判断まで記録し、安全に収束させる同期基盤です。

### 案B

#### 見出し

データを届ける。<br>
判断を残す。

#### 説明

SQL Server、MySQL、PostgreSQL間の双方向同期を、Base、Incoming、Current、Adoptedの履歴とともに運用します。

### 案C

#### 見出し

業務DBの同期を、<br>
「動いたはず」で終わらせない。

#### 説明

どの変更が、どの経路を通り、何回試行され、どの値に収束したか。SyncCoordinatorは同期の設計と運用記録を一つにします。

現時点の推奨は案Cである。製品名を知らない利用者にも課題が伝わり、競合が多用する「Connect everything」「Real-time data」「No-code」から離れられる。

## 8. 次の作業

1. 現サイトの情報構造を破棄し、Record Timeline型のWireframeへ変更する。
2. Heroは架空のNetwork Panelではなく、実DemoのSupportCaseを使用する。
3. Conflict Detailの最新Screenを撮影し、Base／Incoming／Current／Adoptedをサイトの中心画像にする。
4. Communityの公開方針、対応DB、配備形態、制約を簡潔な比較表にする。
5. 公開実績がない段階では顧客Logoや利用件数を代用せず、実DBを使うE2Eと公開された技術仕様を信頼材料にする。
6. 概要動画も同じRecord Timelineを台本として再利用する。
