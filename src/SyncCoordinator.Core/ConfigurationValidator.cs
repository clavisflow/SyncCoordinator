using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SyncCoordinator.Contracts;

namespace SyncCoordinator.Core;

public static class ConfigurationValidator
{
    private static readonly Regex UnsafeRelatedConditionPattern = new(
        @";|--|/\*|\*/|\b(?:INSERT|UPDATE|DELETE|MERGE|DROP|ALTER|CREATE|TRUNCATE|EXEC(?:UTE)?|CALL|GRANT|REVOKE|DENY|BACKUP|RESTORE|WAITFOR)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static void ValidateSystem(SystemConfigurationInput input)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(input.Code))
        {
            errors.Add("システムコードは必須です。");
        }
        else if (input.Code.Trim().Length > 64)
        {
            errors.Add("システムコードは64文字以内です。");
        }

        if (string.IsNullOrWhiteSpace(input.DisplayName))
        {
            errors.Add("表示名は必須です。");
        }

        if (string.IsNullOrWhiteSpace(input.Provider))
        {
            errors.Add("Providerは必須です。");
        }
        else if (!new[] { "SqlServer", "MySql", "PostgreSql" }.Contains(input.Provider, StringComparer.OrdinalIgnoreCase))
        {
            errors.Add("未対応のProviderです。");
        }

        ThrowIfAny(errors);
    }

    public static void ValidateRoute(
        RouteConfigurationInput input,
        IReadOnlyCollection<string> systemCodes)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(input.Name))
        {
            errors.Add("ルール名は必須です。");
        }

        if (!ContainsCode(systemCodes, input.SourceSystem))
        {
            errors.Add("送信元システムが存在しません。");
        }

        if (!ContainsCode(systemCodes, input.DestinationSystem))
        {
            errors.Add("送信先システムが存在しません。");
        }
        else if (string.Equals(
                     input.SourceSystem,
                     input.DestinationSystem,
                     StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("送信元と送信先に同じシステムは指定できません。");
        }
        ThrowIfAny(errors);
    }

    public static void ValidateConnection(DatabaseConnectionInput input, string provider)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(input.Server)) errors.Add("サーバー名は必須です。");
        if (string.IsNullOrWhiteSpace(input.Database)) errors.Add("データベース名は必須です。");
        if (input.Port is <= 0 or > 65535) errors.Add("ポート番号は1～65535で指定してください。");

        var isSqlServer = string.Equals(provider, "SqlServer", StringComparison.OrdinalIgnoreCase);
        if (!isSqlServer && input.IntegratedSecurity)
        {
            errors.Add("Windows認証はSQL Serverでのみ使用できます。");
        }
        if (!input.IntegratedSecurity && string.IsNullOrWhiteSpace(input.UserName))
        {
            errors.Add("ユーザー名は必須です。");
        }
        if (!input.IntegratedSecurity && !input.HasStoredPassword && string.IsNullOrEmpty(input.Password))
        {
            errors.Add("パスワードは必須です。");
        }
        ThrowIfAny(errors);
    }

    public static void ValidateTableMapping(TableMappingInput input, RouteConfigurationInput route)
    {
        var errors = new List<string>();
        if (input.RouteId == Guid.Empty) errors.Add("同期ルールは必須です。");
        if (string.IsNullOrWhiteSpace(input.SourceSchema) || string.IsNullOrWhiteSpace(input.SourceTable)) errors.Add("同期元テーブルは必須です。");
        if (string.IsNullOrWhiteSpace(input.DestinationSchema) || string.IsNullOrWhiteSpace(input.DestinationTable)) errors.Add("同期先テーブルは必須です。");
        ValidateSourceConditionExpression(input.SourceConditionExpression, errors);
        if (input.Columns.Count == 0) errors.Add("列マッピングを1件以上指定してください。");
        if (input.Columns.Count > 0 && input.Columns.All(x => !x.IsKey)) errors.Add("キー列を1件以上指定してください。");
        if (input.Columns.Any(x => string.IsNullOrWhiteSpace(x.SourceColumn) || string.IsNullOrWhiteSpace(x.DestinationColumn))) errors.Add("同期元列と同期先列は必須です。");
        if (input.Columns.GroupBy(CanonicalFieldName, StringComparer.OrdinalIgnoreCase).Any(x => x.Count() > 1)) errors.Add("同期元列が重複しています。");
        if (input.Columns.GroupBy(x => x.DestinationColumn, StringComparer.OrdinalIgnoreCase).Any(x => x.Count() > 1)) errors.Add("同期先列が重複しています。");

        foreach (var column in input.Columns)
        {
            var fieldDirection = EffectiveDirection(column, route);
            if (!string.IsNullOrWhiteSpace(column.SourceTableAlias) &&
                input.RelatedTables.All(x => !string.Equals(x.Alias, column.SourceTableAlias, StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add($"列 '{column.SourceTableAlias}.{column.SourceColumn}' の関連テーブルが存在しません。");
            }
            if (!string.IsNullOrWhiteSpace(column.SourceTableAlias) && fieldDirection != SyncFieldDirection.Forward)
            {
                errors.Add($"関連テーブル列 '{column.SourceTableAlias}.{column.SourceColumn}' は送信元から同期先への片方向にしてください。");
            }
            if (column.IsKey && !string.IsNullOrWhiteSpace(column.SourceTableAlias))
            {
                errors.Add($"キー列 '{column.SourceTableAlias}.{column.SourceColumn}' は同期単位テーブルから選択してください。");
            }
            if (route.Direction != SyncDirection.Bidirectional && fieldDirection != SyncFieldDirection.Forward)
            {
                errors.Add($"片方向ルールの列 '{column.SourceColumn}' は正方向のみ指定できます。");
            }
            if (column.IsKey && fieldDirection != (route.Direction == SyncDirection.Bidirectional
                    ? SyncFieldDirection.Bidirectional
                    : SyncFieldDirection.Forward))
            {
                errors.Add($"キー列 '{column.SourceColumn}' はルール全体と同じ同期方向にしてください。");
            }
            if (column.IsKey && (!column.ForwardTransform.IsIdentity || !column.ReverseTransform.IsIdentity))
            {
                errors.Add($"キー列 '{column.SourceColumn}' には値変換を設定できません。");
            }
            ValidateTransform(column.ForwardTransform, $"{column.SourceColumn} → {column.DestinationColumn}", errors);
            ValidateTransform(column.ReverseTransform, $"{column.DestinationColumn} → {column.SourceColumn}", errors);
        }

        if (input.RelatedTables.GroupBy(x => x.Alias, StringComparer.OrdinalIgnoreCase).Any(x => x.Count() > 1))
        {
            errors.Add("関連テーブルの別名が重複しています。");
        }
        foreach (var related in input.RelatedTables)
        {
            if (string.IsNullOrWhiteSpace(related.Schema) || string.IsNullOrWhiteSpace(related.Table))
                errors.Add("関連テーブルは必須です。");
            else if (related.Schema.Length > 128 || related.Table.Length > 128)
                errors.Add($"関連テーブル '{related.Schema}.{related.Table}' のschema名とtable名は128文字以内です。");
            if (string.IsNullOrWhiteSpace(related.Alias)) errors.Add("関連テーブルの別名は必須です。");
            else if (related.Alias.Contains('.'))
                errors.Add($"関連テーブルの別名 '{related.Alias}' にピリオドは使用できません。");
            else if (related.Alias.Length > 128)
                errors.Add($"関連テーブルの識別名 '{related.Alias}' は128文字以内です。");
            else if (string.Equals(related.Alias, "sc_base", StringComparison.OrdinalIgnoreCase))
                errors.Add("関連テーブルの別名 'sc_base' は予約名のため使用できません。");
            ValidateRelatedJoinExpression(related, errors);
            ValidateRelatedConditionExpression(related, errors);
            if (related.Usage == RelatedTableUsage.Eligibility && input.Columns.Any(x =>
                    string.Equals(x.SourceTableAlias, related.Alias, StringComparison.OrdinalIgnoreCase)))
                errors.Add($"対象判定専用の関連テーブル '{related.Alias}' から同期列は選択できません。");
            if (related.Usage == RelatedTableUsage.Projection && input.Columns.All(x =>
                    !string.Equals(x.SourceTableAlias, related.Alias, StringComparison.OrdinalIgnoreCase)))
                errors.Add($"投影用の関連テーブル '{related.Alias}' から同期列を1件以上選択してください。");
        }

        if (input.SyncDeletes)
        {
            ValidateDeletionMode(
                input.SourceDeletionMode,
                input.SourceLogicalDeleteColumn,
                input.SourceLogicalDeleteValue,
                "同期元",
                errors);
            ValidateDeletionMode(
                input.DestinationDeletionMode,
                input.DestinationLogicalDeleteColumn,
                input.DestinationLogicalDeleteValue,
                "同期先",
                errors);
        }

        foreach (var fixedValue in input.FixedValues)
        {
            if (string.IsNullOrWhiteSpace(fixedValue.TargetColumn))
            {
                errors.Add("固定値の書き込み先列は必須です。");
            }
            else if (fixedValue.TargetColumn.Length > 128)
            {
                errors.Add($"固定値の書き込み先列 '{fixedValue.TargetColumn}' は128文字以内です。");
            }

            if (fixedValue.Value.Length > 4000)
            {
                errors.Add($"固定値は4000文字以内です（{fixedValue.TargetColumn}）。");
            }

            if (fixedValue.Direction == MappingWriteDirection.Reverse && route.Direction != SyncDirection.Bidirectional)
            {
                errors.Add("片方向ルールに戻り方向の固定値は設定できません。");
            }

            if (fixedValue.IsKey && fixedValue.TargetContract.IsNullable)
            {
                errors.Add($"固定値キー '{fixedValue.TargetColumn}' はNOT NULL列に設定してください。");
            }
            if (fixedValue.IsKey && string.IsNullOrWhiteSpace(fixedValue.Value))
            {
                errors.Add($"固定値キー '{fixedValue.TargetColumn}' の値は必須です。");
            }

            try
            {
                ValueTransformEngine.Transform(
                    JsonValue.Create(fixedValue.Value),
                    new ValueTransformInput(),
                    fixedValue.TargetContract,
                    fixedValue.TargetColumn,
                    fixedValue.TargetColumn);
            }
            catch (ValueTransformationException exception)
            {
                errors.Add($"固定値が書き込み先の列制約を満たしません: {exception.Message}");
            }
        }

        if (input.FixedValues
            .GroupBy(x => (x.Direction, x.TargetColumn), new FixedValueKeyComparer())
            .Any(x => x.Count() > 1))
        {
            errors.Add("同じ方向と書き込み先列の固定値が重複しています。");
        }

        var forwardColumns = input.Columns
            .Where(x => EffectiveDirection(x, route) is SyncFieldDirection.Forward or SyncFieldDirection.Bidirectional)
            .Select(x => x.DestinationColumn)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var reverseColumns = input.Columns
            .Where(x => EffectiveDirection(x, route) is SyncFieldDirection.Reverse or SyncFieldDirection.Bidirectional)
            .Select(x => x.SourceColumn)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (input.FixedValues.Any(x =>
                x.Direction == MappingWriteDirection.Forward && forwardColumns.Contains(x.TargetColumn) ||
                x.Direction == MappingWriteDirection.Reverse && reverseColumns.Contains(x.TargetColumn)))
        {
            errors.Add("通常の列マッピングと固定値に同じ書き込み先列は指定できません。");
        }

        ThrowIfAny(errors);
    }

    private static void ValidateTransform(
        ValueTransformInput transform,
        string label,
        List<string> errors)
    {
        if (transform.NullFallback.Length > 4000)
        {
            errors.Add($"{label} のNULL既定値は4000文字以内です。");
        }
        if (transform.ValueMap.Count > 200)
        {
            errors.Add($"{label} のコード変換は200件以内です。");
        }
        if (transform.ValueMap.GroupBy(x => x.SourceValue, StringComparer.Ordinal).Any(x => x.Count() > 1))
        {
            errors.Add($"{label} のコード変換元の値が重複しています。");
        }
        if (transform.ValueMap.Any(x => x.SourceValue.Length > 4000 || x.TargetValue.Length > 4000))
        {
            errors.Add($"{label} のコード変換値は4000文字以内です。");
        }
        if (transform.RejectUnmappedValues && transform.ValueMap.Count == 0)
        {
            errors.Add($"{label} で未定義値を拒否する場合はコード変換を1件以上指定してください。");
        }
    }

    private static void ValidateRelatedConditionExpression(RelatedTableInput related, List<string> errors)
    {
        var expression = related.ConditionExpression;
        if (string.IsNullOrWhiteSpace(expression))
        {
            return;
        }
        ValidateRelatedSqlExpression(related.Alias, "条件式", expression, errors);
    }

    private static void ValidateSourceConditionExpression(string expression, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return;
        }
        if (expression.Length > 4000)
        {
            errors.Add("同期元テーブルの同期対象条件は4000文字以内です。");
        }
        if (UnsafeRelatedConditionPattern.IsMatch(expression) || expression.Contains('\0'))
        {
            errors.Add("同期元テーブルの同期対象条件に、複数文、SQLコメント、または更新系SQLは使用できません。");
        }

        var remainingPlaceholders = expression
            .Replace("{source}", string.Empty, StringComparison.OrdinalIgnoreCase);
        if (remainingPlaceholders.Contains('{') || remainingPlaceholders.Contains('}'))
        {
            errors.Add("同期元テーブルの同期対象条件で使用できるプレースホルダーは '{source}' だけです。");
        }
    }

    private static void ValidateRelatedJoinExpression(RelatedTableInput related, List<string> errors)
    {
        var expression = related.JoinExpression;
        if (string.IsNullOrWhiteSpace(expression))
        {
            errors.Add($"関連テーブル '{related.Alias}' の結合式は必須です。");
            return;
        }
        ValidateRelatedSqlExpression(related.Alias, "結合式", expression, errors);
        if (!expression.Contains("{related}", StringComparison.OrdinalIgnoreCase) ||
            !expression.Contains("{source}", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"関連テーブル '{related.Alias}' の結合式では '{{source}}' と '{{related}}' の両方を参照してください。");
        }
    }

    private static void ValidateRelatedSqlExpression(
        string alias,
        string label,
        string expression,
        List<string> errors)
    {
        if (expression.Length > 4000)
        {
            errors.Add($"関連テーブル '{alias}' の{label}は4000文字以内です。");
        }
        if (UnsafeRelatedConditionPattern.IsMatch(expression) || expression.Contains('\0'))
        {
            errors.Add($"関連テーブル '{alias}' の{label}に、複数文、SQLコメント、または更新系SQLは使用できません。");
        }

        var remainingPlaceholders = expression
            .Replace("{related}", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{source}", string.Empty, StringComparison.OrdinalIgnoreCase);
        if (remainingPlaceholders.Contains('{') || remainingPlaceholders.Contains('}'))
        {
            errors.Add($"関連テーブル '{alias}' の{label}で使用できるプレースホルダーは '{{related}}' と '{{source}}' だけです。");
        }
    }

    private static bool ContainsCode(IReadOnlyCollection<string> codes, string? code) =>
        !string.IsNullOrWhiteSpace(code) && codes.Contains(code, StringComparer.OrdinalIgnoreCase);

    private static SyncFieldDirection EffectiveDirection(
        ColumnMappingInput column,
        RouteConfigurationInput route) =>
        column.Direction ?? (route.Direction == SyncDirection.Bidirectional
            ? SyncFieldDirection.Bidirectional
            : SyncFieldDirection.Forward);

    private static string CanonicalFieldName(ColumnMappingInput column) =>
        string.IsNullOrWhiteSpace(column.SourceTableAlias)
            ? column.SourceColumn
            : $"{column.SourceTableAlias}.{column.SourceColumn}";

    private static void ValidateDeletionMode(
        DeletionMode mode,
        string logicalDeleteColumn,
        string logicalDeleteValue,
        string side,
        List<string> errors)
    {
        if (mode != DeletionMode.Logical)
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(logicalDeleteColumn))
        {
            errors.Add($"{side}を論理削除にする場合は論理削除列が必須です。");
        }
        else if (logicalDeleteColumn.Length > 128)
        {
            errors.Add($"{side}の論理削除列は128文字以内です。");
        }
        if (string.IsNullOrWhiteSpace(logicalDeleteValue))
        {
            errors.Add($"{side}を論理削除にする場合は削除時の値が必須です。");
        }
        else if (logicalDeleteValue.Length > 4000)
        {
            errors.Add($"{side}の論理削除値は4000文字以内です。");
        }
    }

    private sealed class FixedValueKeyComparer : IEqualityComparer<(MappingWriteDirection Direction, string TargetColumn)>
    {
        public bool Equals(
            (MappingWriteDirection Direction, string TargetColumn) x,
            (MappingWriteDirection Direction, string TargetColumn) y) =>
            x.Direction == y.Direction &&
            string.Equals(x.TargetColumn, y.TargetColumn, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((MappingWriteDirection Direction, string TargetColumn) obj) =>
            HashCode.Combine(obj.Direction, StringComparer.OrdinalIgnoreCase.GetHashCode(obj.TargetColumn));
    }

    private static void ThrowIfAny(List<string> errors)
    {
        if (errors.Count > 0)
        {
            throw new ConfigurationValidationException(errors);
        }
    }
}
