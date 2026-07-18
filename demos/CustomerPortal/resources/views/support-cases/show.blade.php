@extends('layouts.app')

@section('title', ($case->value('CaseNumber') ?? '相談内容').'｜製品おたすけ窓口')

@section('content')
<div class="site-width sub-page">
    <nav class="breadcrumbs" aria-label="パンくず"><a href="{{ route('home', absolute: false) }}">トップ</a><span>›</span><span>{{ $case->value('CaseNumber') }}</span></nav>

    <div class="detail-heading">
        <div><p class="section-label">受付番号 {{ $case->value('CaseNumber') }}</p><h1>{{ $case->value('Subject') ?? 'ご相談内容' }}</h1><p>{{ $case->value('ProductName') ?? '製品名未登録' }}　／　最終更新：{{ $case->updatedAtJapan() }}</p></div>
        <span class="status status-large {{ $case->statusClass() }}">{{ $case->statusLabel() }}</span>
    </div>

    <div class="detail-layout">
        <section class="detail-main">
            <div class="detail-card">
                <h2>お困りのこと・症状</h2>
                <p class="description-text">{{ $case->value('Description') ?? '内容が登録されていません。' }}</p>
            </div>

            <div class="response-card {{ $case->value('ResponseMessage') ? 'has-response' : '' }}">
                <div class="response-heading">
                    <span class="response-icon" aria-hidden="true"></span><h2>担当者からのご案内</h2>
                    <small>{{ $case->value('ResponseMessage') ? 'Service CRMから反映' : 'Service CRMで確認中' }}</small>
                </div>
                @if ($case->value('ResponseMessage'))
                    <p>{{ $case->value('ResponseMessage') }}</p>
                @else
                    <p>内容を確認しています。担当者からのご案内が届くまで、しばらくお待ちください。</p>
                @endif
            </div>

            <div class="detail-actions"><a class="button button-secondary" href="{{ route('support-cases.edit', $case->entityId, absolute: false) }}">相談内容を変更する</a></div>
        </section>

        <aside class="detail-side" aria-label="受付内容">
            <h2>受付内容</h2>
            <dl>
                <dt>お名前</dt><dd>{{ $case->value('CustomerName') ?? '—' }}</dd>
                <dt>メールアドレス</dt><dd>{{ $case->value('Email') ?? '—' }}</dd>
                <dt>電話番号</dt><dd>{{ $case->value('Phone') ?? '—' }}</dd>
                <dt>製品名</dt><dd>{{ $case->value('ProductName') ?? '—' }}</dd>
                <dt>製造番号</dt><dd>{{ $case->value('SerialNumber') ?? '—' }}</dd>
                <dt>訪問希望日</dt><dd>{{ $case->value('PreferredVisitDate') ?? '指定なし' }}</dd>
            </dl>
        </aside>
    </div>
</div>
@endsection
