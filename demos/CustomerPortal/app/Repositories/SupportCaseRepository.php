<?php

namespace App\Repositories;

use App\Support\SupportCaseRecord;
use Carbon\CarbonImmutable;
use Illuminate\Support\Facades\DB;
use Illuminate\Support\Str;

final class SupportCaseRepository
{
    private const NEW_ORIGIN_SYSTEM = 'PORTAL';

    /** @var list<string> */
    private const PAYLOAD_FIELDS = [
        'CaseNumber',
        'CustomerName',
        'Email',
        'Phone',
        'ProductName',
        'SerialNumber',
        'Subject',
        'Description',
        'PreferredVisitDate',
        'Status',
        'ResponseMessage',
    ];

    /** @return list<SupportCaseRecord> */
    public function all(): array
    {
        $rows = DB::select(
            <<<'SQL'
                SELECT CaseNumber AS EntityId, CaseNumber, CustomerName, Email, Phone, ProductName,
                       SerialNumber, Subject, Description, PreferredVisitDate, Status, ResponseMessage,
                       OriginSystem, UpdatedAtUtc
                FROM SupportCase
                ORDER BY UpdatedAtUtc DESC
            SQL,
        );

        return array_map(fn (object $row): SupportCaseRecord => $this->hydrate($row), $rows);
    }

    public function find(string $entityId, bool $forUpdate = false): ?SupportCaseRecord
    {
        $sql = <<<'SQL'
            SELECT CaseNumber AS EntityId, CaseNumber, CustomerName, Email, Phone, ProductName,
                   SerialNumber, Subject, Description, PreferredVisitDate, Status, ResponseMessage,
                   OriginSystem, UpdatedAtUtc
            FROM SupportCase
            WHERE CaseNumber = ?
        SQL;

        if ($forUpdate) {
            $sql .= ' FOR UPDATE';
        }

        $row = DB::selectOne($sql, [$entityId]);

        return $row === null ? null : $this->hydrate($row);
    }

    /** @param array<string, mixed> $input */
    public function create(array $input): string
    {
        $entityId = 'SC-'.now('UTC')->format('Ymd').'-'.strtoupper(substr(str_replace('-', '', (string) Str::uuid()), 0, 8));
        $payload = $this->payloadFromInput(
            $input,
            $entityId,
            'New',
            null,
        );

        DB::insert(
            <<<'SQL'
                INSERT INTO SupportCase
                    (CaseNumber, CustomerName, Email, Phone, ProductName, SerialNumber, Subject,
                     Description, PreferredVisitDate, Status, ResponseMessage, OriginSystem, UpdatedAtUtc)
                VALUES
                    (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, UTC_TIMESTAMP(6))
            SQL,
            [
                $payload['CaseNumber'],
                $payload['CustomerName'],
                $payload['Email'],
                $payload['Phone'],
                $payload['ProductName'],
                $payload['SerialNumber'],
                $payload['Subject'],
                $payload['Description'],
                $payload['PreferredVisitDate'],
                $payload['Status'],
                $payload['ResponseMessage'],
                self::NEW_ORIGIN_SYSTEM,
            ],
        );

        return $entityId;
    }

    /** @param array<string, mixed> $input */
    public function update(string $entityId, array $input): bool
    {
        return DB::transaction(function () use ($entityId, $input): bool {
            $current = $this->find($entityId, forUpdate: true);
            if ($current === null) {
                return false;
            }

            $payload = $this->payloadFromInput(
                $input,
                $current->value('CaseNumber'),
                $current->value('Status') ?? 'New',
                $current->value('ResponseMessage'),
            );

            $affected = DB::update(
                <<<'SQL'
                    UPDATE SupportCase
                    SET CustomerName = ?, Email = ?, Phone = ?, ProductName = ?, SerialNumber = ?,
                        Subject = ?, Description = ?, PreferredVisitDate = ?, Status = ?, ResponseMessage = ?,
                        UpdatedAtUtc = UTC_TIMESTAMP(6)
                    WHERE CaseNumber = ?
                SQL,
                [
                    $payload['CustomerName'],
                    $payload['Email'],
                    $payload['Phone'],
                    $payload['ProductName'],
                    $payload['SerialNumber'],
                    $payload['Subject'],
                    $payload['Description'],
                    $payload['PreferredVisitDate'],
                    $payload['Status'],
                    $payload['ResponseMessage'],
                    $entityId,
                ],
            );

            return $affected === 1;
        }, attempts: 3);
    }

    /**
     * @param array<string, mixed> $input
     * @return array<string, string|null>
     */
    private function payloadFromInput(
        array $input,
        ?string $caseNumber,
        string $status,
        ?string $responseMessage,
    ): array {
        return [
            'CaseNumber' => $this->normalize($caseNumber),
            'CustomerName' => $this->normalize($input['customer_name'] ?? null),
            'Email' => $this->normalize($input['email'] ?? null),
            'Phone' => $this->normalize($input['phone'] ?? null),
            'ProductName' => $this->normalize($input['product_name'] ?? null),
            'SerialNumber' => $this->normalize($input['serial_number'] ?? null),
            'Subject' => $this->normalize($input['subject'] ?? null),
            'Description' => $this->normalize($input['description'] ?? null),
            'PreferredVisitDate' => $this->normalize($input['preferred_visit_date'] ?? null),
            'Status' => $this->normalize($status) ?? 'New',
            'ResponseMessage' => $this->normalize($responseMessage),
        ];
    }

    private function hydrate(object $row): SupportCaseRecord
    {
        $payload = [];
        foreach (self::PAYLOAD_FIELDS as $field) {
            $value = $row->{$field} ?? null;
            $payload[$field] = is_scalar($value) ? (string) $value : null;
        }

        return new SupportCaseRecord(
            entityId: (string) $row->EntityId,
            originSystem: (string) $row->OriginSystem,
            updatedAtUtc: CarbonImmutable::parse((string) $row->UpdatedAtUtc, 'UTC'),
            payload: $payload,
        );
    }

    private function normalize(mixed $value): ?string
    {
        if (! is_scalar($value)) {
            return null;
        }

        $normalized = trim((string) $value);

        return $normalized === '' ? null : $normalized;
    }
}
