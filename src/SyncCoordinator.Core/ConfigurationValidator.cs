using SyncCoordinator.Contracts;

namespace SyncCoordinator.Core;

public static class ConfigurationValidator
{
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
        else if (IsDirectRouteBetweenAAndB(input.SourceSystem, input.DestinationSystem))
        {
            errors.Add("AとBを直接同期するルールは作成できません。");
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
        if (input.Columns.Count == 0) errors.Add("列マッピングを1件以上指定してください。");
        if (input.Columns.Count > 0 && input.Columns.All(x => !x.IsKey)) errors.Add("キー列を1件以上指定してください。");
        if (input.Columns.Any(x => string.IsNullOrWhiteSpace(x.SourceColumn) || string.IsNullOrWhiteSpace(x.DestinationColumn))) errors.Add("同期元列と同期先列は必須です。");
        if (input.Columns.GroupBy(x => x.SourceColumn, StringComparer.OrdinalIgnoreCase).Any(x => x.Count() > 1)) errors.Add("同期元列が重複しています。");
        if (input.Columns.GroupBy(x => x.DestinationColumn, StringComparer.OrdinalIgnoreCase).Any(x => x.Count() > 1)) errors.Add("同期先列が重複しています。");

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
        }

        if (input.FixedValues
            .GroupBy(x => (x.Direction, x.TargetColumn), new FixedValueKeyComparer())
            .Any(x => x.Count() > 1))
        {
            errors.Add("同じ方向と書き込み先列の固定値が重複しています。");
        }

        var forwardColumns = input.Columns.Select(x => x.DestinationColumn).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var reverseColumns = input.Columns.Select(x => x.SourceColumn).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (input.FixedValues.Any(x =>
                x.Direction == MappingWriteDirection.Forward && forwardColumns.Contains(x.TargetColumn) ||
                x.Direction == MappingWriteDirection.Reverse && reverseColumns.Contains(x.TargetColumn)))
        {
            errors.Add("通常の列マッピングと固定値に同じ書き込み先列は指定できません。");
        }

        ThrowIfAny(errors);
    }

    private static bool ContainsCode(IReadOnlyCollection<string> codes, string? code) =>
        !string.IsNullOrWhiteSpace(code) && codes.Contains(code, StringComparer.OrdinalIgnoreCase);

    private static bool IsDirectRouteBetweenAAndB(string source, string destination) =>
        string.Equals(source, "A", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(destination, "B", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(source, "B", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(destination, "A", StringComparison.OrdinalIgnoreCase);

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
