<?php

namespace App\Support;

use Carbon\CarbonImmutable;

final readonly class SupportCaseRecord
{
    /** @param array<string, string|null> $payload */
    public function __construct(
        public string $entityId,
        public string $originSystem,
        public CarbonImmutable $updatedAtUtc,
        public array $payload,
    ) {
    }

    public function value(string $key): ?string
    {
        $value = $this->payload[$key] ?? null;

        return is_string($value) && trim($value) !== '' ? $value : null;
    }

    public function statusLabel(): string
    {
        return match ($this->value('Status')) {
            'New' => '受付待ち',
            'Received' => '受付済み',
            'Scheduled' => '訪問予定',
            'InProgress' => '対応中',
            'Completed' => '完了',
            'Cancelled' => 'キャンセル',
            default => '確認中',
        };
    }

    public function statusClass(): string
    {
        return match ($this->value('Status')) {
            'Completed' => 'status-completed',
            'Cancelled' => 'status-cancelled',
            'InProgress' => 'status-progress',
            'Scheduled' => 'status-scheduled',
            'Received' => 'status-received',
            default => 'status-new',
        };
    }

    public function updatedAtJapan(): string
    {
        return $this->updatedAtUtc->setTimezone('Asia/Tokyo')->format('Y年n月j日 H:i');
    }
}
