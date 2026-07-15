@extends('layouts.app')

@section('title', '相談内容を変更する｜製品おたすけ窓口')

@section('content')
<div class="site-width sub-page">
    <nav class="breadcrumbs" aria-label="パンくず"><a href="{{ route('home', absolute: false) }}">トップ</a><span>›</span><a href="{{ route('support-cases.show', $case->entityId, absolute: false) }}">{{ $case->value('CaseNumber') }}</a><span>›</span><span>内容を変更</span></nav>
    <div class="page-heading">
        <p class="section-label">ご相談内容の変更</p>
        <h1>相談内容を変更する</h1>
        <p>受付番号 {{ $case->value('CaseNumber') }}</p>
    </div>
    <form class="support-form" method="post" action="{{ route('support-cases.update', $case->entityId, absolute: false) }}">
        @csrf
        @method('PUT')
        @include('partials.support-case-form', ['case' => $case])
    </form>
</div>
@endsection
