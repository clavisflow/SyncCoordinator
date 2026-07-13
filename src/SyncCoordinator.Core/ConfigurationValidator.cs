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

        ThrowIfAny(errors);
    }

    public static void ValidateRoute(
        RouteConfigurationInput input,
        IReadOnlyCollection<string> systemCodes)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(input.Name))
        {
            errors.Add("ルート名は必須です。");
        }

        if (string.IsNullOrWhiteSpace(input.EntityType))
        {
            errors.Add("Entity Typeは必須です。");
        }

        if (!ContainsCode(systemCodes, input.SourceSystem))
        {
            errors.Add("発生元システムが存在しません。");
        }

        if (input.DestinationMode == DestinationMode.FixedSystem)
        {
            if (!ContainsCode(systemCodes, input.DestinationSystem))
            {
                errors.Add("固定宛先システムが存在しません。");
            }
            else if (string.Equals(
                         input.SourceSystem,
                         input.DestinationSystem,
                         StringComparison.OrdinalIgnoreCase))
            {
                errors.Add("発生元と宛先に同じシステムは指定できません。");
            }
            else if (IsDirectRouteBetweenAAndB(input.SourceSystem, input.DestinationSystem!))
            {
                errors.Add("AとBを直接同期するルートは作成できません。");
            }
        }

        var fieldNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in input.FieldPolicies)
        {
            var name = field.FieldName.Trim();
            if (name.Length == 0)
            {
                errors.Add("項目別ポリシーの項目名は必須です。");
            }
            else if (name.Length > 128)
            {
                errors.Add($"項目名 '{name}' は128文字以内です。");
            }
            else if (!fieldNames.Add(name))
            {
                errors.Add($"項目名 '{name}' が重複しています。");
            }
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
        if (input.RouteId == Guid.Empty) errors.Add("同期ルートは必須です。");
        if (string.IsNullOrWhiteSpace(input.DestinationSystem)) errors.Add("宛先システムは必須です。");
        if (string.IsNullOrWhiteSpace(input.SourceSchema) || string.IsNullOrWhiteSpace(input.SourceTable)) errors.Add("同期元テーブルは必須です。");
        if (string.IsNullOrWhiteSpace(input.DestinationSchema) || string.IsNullOrWhiteSpace(input.DestinationTable)) errors.Add("同期先テーブルは必須です。");
        if (input.Columns.Count == 0) errors.Add("列マッピングを1件以上指定してください。");
        if (input.Columns.Count > 0 && input.Columns.All(x => !x.IsKey)) errors.Add("キー列を1件以上指定してください。");
        if (input.Columns.Any(x => string.IsNullOrWhiteSpace(x.SourceColumn) || string.IsNullOrWhiteSpace(x.DestinationColumn))) errors.Add("同期元列と同期先列は必須です。");
        if (input.Columns.GroupBy(x => x.SourceColumn, StringComparer.OrdinalIgnoreCase).Any(x => x.Count() > 1)) errors.Add("同期元列が重複しています。");
        if (input.Columns.GroupBy(x => x.DestinationColumn, StringComparer.OrdinalIgnoreCase).Any(x => x.Count() > 1)) errors.Add("同期先列が重複しています。");

        if (route.DestinationMode == DestinationMode.FixedSystem &&
            !string.Equals(route.DestinationSystem, input.DestinationSystem, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("固定宛先ルートの宛先とマッピングの宛先が一致しません。");
        }
        if (string.Equals(route.SourceSystem, input.DestinationSystem, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("同期元と同期先に同じシステムは指定できません。");
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

    private static void ThrowIfAny(List<string> errors)
    {
        if (errors.Count > 0)
        {
            throw new ConfigurationValidationException(errors);
        }
    }
}
