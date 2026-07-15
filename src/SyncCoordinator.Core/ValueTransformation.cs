using System.Globalization;
using System.Text.Json.Nodes;

namespace SyncCoordinator.Core;

public sealed class ValueTransformationException(
    string fieldName,
    string targetColumn,
    string reasonCode,
    string message) : Exception(message)
{
    public string FieldName { get; } = fieldName;
    public string TargetColumn { get; } = targetColumn;
    public string ReasonCode { get; } = reasonCode;
}

public static class ValueTransformEngine
{
    private static readonly IFormatProvider Invariant = CultureInfo.InvariantCulture;

    public static JsonNode? Transform(
        JsonNode? input,
        ValueTransformInput? transform,
        ColumnValueContract contract,
        string fieldName,
        string targetColumn)
    {
        transform ??= new ValueTransformInput();
        var value = input?.DeepClone();
        if (value is null && transform.UseNullFallback)
        {
            value = JsonValue.Create(transform.NullFallback);
        }

        if (value is null)
        {
            if (contract.IsKnown && !contract.IsNullable)
            {
                throw Error(fieldName, targetColumn, "null-not-allowed", "NULLを許可しない列へNULLは書き込めません。");
            }
            return null;
        }

        if (transform.ValueMap.Count > 0)
        {
            var sourceValue = ScalarText(value);
            var mapped = transform.ValueMap.FirstOrDefault(entry =>
                string.Equals(entry.SourceValue, sourceValue, StringComparison.Ordinal));
            if (mapped is not null)
            {
                value = JsonValue.Create(mapped.TargetValue);
            }
            else if (transform.RejectUnmappedValues)
            {
                throw Error(
                    fieldName,
                    targetColumn,
                    "unmapped-value",
                    $"値 '{sourceValue}' に対応するコード変換がありません。");
            }
        }

        if (!contract.IsKnown)
        {
            return NormalizeDateTimeIfRequested(value, transform, fieldName, targetColumn);
        }

        var dataType = contract.DataType.Trim().ToLowerInvariant();
        if (IsString(dataType))
        {
            var text = ScalarText(value);
            if (contract.MaxLength is { } maxLength && maxLength >= 0 && text.Length > maxLength)
            {
                if (transform.StringOverflow != StringOverflowBehavior.Truncate)
                {
                    throw Error(
                        fieldName,
                        targetColumn,
                        "string-overflow",
                        $"{text.Length}文字の値は最大{maxLength}文字の列へ書き込めません。");
                }
                text = SafeTruncate(text, maxLength);
            }
            return JsonValue.Create(text);
        }

        if (IsInteger(dataType))
        {
            var number = ParseDecimal(value, fieldName, targetColumn);
            if (number != decimal.Truncate(number))
            {
                throw Error(fieldName, targetColumn, "integer-required", "整数列へ小数を含む値は書き込めません。");
            }
            ValidateIntegerRange(number, dataType, fieldName, targetColumn);
            ValidatePrecision(number, contract.NumericPrecision, 0, fieldName, targetColumn);
            return JsonValue.Create(number);
        }

        if (IsDecimal(dataType))
        {
            var number = ParseDecimal(value, fieldName, targetColumn);
            var scale = contract.NumericScale;
            if (scale is { } targetScale && FractionDigits(number) > targetScale)
            {
                if (transform.NumericScale != NumericScaleBehavior.Round)
                {
                    throw Error(
                        fieldName,
                        targetColumn,
                        "numeric-scale-overflow",
                        $"小数部が{FractionDigits(number)}桁の値はscale {targetScale}の列へ書き込めません。");
                }
                number = decimal.Round(number, targetScale, MidpointRounding.AwayFromZero);
            }
            ValidatePrecision(number, contract.NumericPrecision, scale, fieldName, targetColumn);
            return JsonValue.Create(number);
        }

        if (IsFloatingPoint(dataType))
        {
            if (!double.TryParse(ScalarText(value), NumberStyles.Float, Invariant, out var number) ||
                double.IsNaN(number) ||
                double.IsInfinity(number))
            {
                throw Error(fieldName, targetColumn, "invalid-number", "数値へ変換できません。");
            }
            return JsonValue.Create(number);
        }

        if (IsBoolean(dataType))
        {
            var text = ScalarText(value);
            if (bool.TryParse(text, out var boolean)) return JsonValue.Create(boolean);
            if (text == "1") return JsonValue.Create(true);
            if (text == "0") return JsonValue.Create(false);
            throw Error(fieldName, targetColumn, "invalid-boolean", "真偽値へ変換できません。");
        }

        if (IsDateTime(dataType))
        {
            return NormalizeDateTimeIfRequested(value, transform, fieldName, targetColumn, requireDateTime: true);
        }

        if (IsGuid(dataType))
        {
            if (!Guid.TryParse(ScalarText(value), out var guid))
            {
                throw Error(fieldName, targetColumn, "invalid-guid", "UUIDへ変換できません。");
            }
            return JsonValue.Create(guid.ToString("D"));
        }

        return value;
    }

    public static string? CompatibilityWarning(
        ColumnValueContract source,
        ColumnValueContract destination,
        ValueTransformInput forwardTransform)
    {
        if (source.NumericPrecision is { } sourcePrecision &&
            destination.NumericPrecision is { } destinationPrecision &&
            sourcePrecision - (source.NumericScale ?? 0) >
            destinationPrecision - (destination.NumericScale ?? 0))
        {
            return "同期先の数値整数部が短いため、範囲外の値は保留されます。";
        }
        if (source.NumericScale is { } sourceScale &&
            destination.NumericScale is { } destinationScale &&
            sourceScale > destinationScale &&
            forwardTransform.NumericScale == NumericScaleBehavior.Reject)
        {
            return $"同期先の小数桁が少ないため、scale {destinationScale}を超える値は保留されます。";
        }
        return null;
    }

    private static JsonNode NormalizeDateTimeIfRequested(
        JsonNode value,
        ValueTransformInput transform,
        string fieldName,
        string targetColumn,
        bool requireDateTime = false)
    {
        if (!transform.NormalizeDateTimeToUtc && !requireDateTime)
        {
            return value;
        }
        if (!DateTimeOffset.TryParse(
                ScalarText(value),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.RoundtripKind,
                out var dateTime))
        {
            throw Error(fieldName, targetColumn, "invalid-datetime", "日時へ変換できません。");
        }
        return transform.NormalizeDateTimeToUtc
            ? JsonValue.Create(dateTime.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))!
            : value;
    }

    private static decimal ParseDecimal(JsonNode value, string fieldName, string targetColumn)
    {
        if (!decimal.TryParse(
                ScalarText(value),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var number))
        {
            throw Error(fieldName, targetColumn, "invalid-number", "数値へ変換できません。");
        }
        return number;
    }

    private static void ValidatePrecision(
        decimal value,
        int? precision,
        int? scale,
        string fieldName,
        string targetColumn)
    {
        if (precision is not { } targetPrecision) return;
        var integerLimit = targetPrecision - (scale ?? 0);
        if (IntegerDigits(value) > integerLimit)
        {
            throw Error(
                fieldName,
                targetColumn,
                "numeric-precision-overflow",
                $"整数部が{IntegerDigits(value)}桁の値はprecision {targetPrecision}, scale {scale ?? 0}の列へ書き込めません。");
        }
    }

    private static void ValidateIntegerRange(
        decimal value,
        string dataType,
        string fieldName,
        string targetColumn)
    {
        (decimal Min, decimal Max)? range = dataType switch
        {
            "tinyint" => (byte.MinValue, byte.MaxValue),
            "smallint" or "int2" or "smallserial" => (short.MinValue, short.MaxValue),
            "int" or "integer" or "int4" or "serial" or "mediumint" => (int.MinValue, int.MaxValue),
            "bigint" or "int8" or "bigserial" => (long.MinValue, long.MaxValue),
            _ => null
        };
        if (range is { } targetRange && (value < targetRange.Min || value > targetRange.Max))
        {
            throw Error(fieldName, targetColumn, "integer-overflow", $"値 {value} は{dataType}の範囲外です。");
        }
    }

    private static int IntegerDigits(decimal value)
    {
        var integer = decimal.Abs(decimal.Truncate(value));
        if (integer == 0) return 0;
        var text = integer.ToString("0", CultureInfo.InvariantCulture);
        return text.Length;
    }

    private static int FractionDigits(decimal value)
    {
        var text = decimal.Abs(value).ToString("0.############################", CultureInfo.InvariantCulture);
        var separator = text.IndexOf('.');
        return separator < 0 ? 0 : text.Length - separator - 1;
    }

    private static string SafeTruncate(string value, int maxLength)
    {
        if (maxLength <= 0) return string.Empty;
        var length = Math.Min(maxLength, value.Length);
        if (length < value.Length && length > 0 && char.IsHighSurrogate(value[length - 1]))
        {
            length--;
        }
        return value[..length];
    }

    private static string ScalarText(JsonNode value)
    {
        if (value is not JsonValue scalar) return value.ToJsonString();
        if (scalar.TryGetValue<string>(out var text)) return text;
        if (scalar.TryGetValue<bool>(out var boolean)) return boolean ? "true" : "false";
        if (scalar.TryGetValue<decimal>(out var decimalValue)) return decimalValue.ToString(CultureInfo.InvariantCulture);
        if (scalar.TryGetValue<long>(out var integer)) return integer.ToString(CultureInfo.InvariantCulture);
        if (scalar.TryGetValue<double>(out var doubleValue)) return doubleValue.ToString("R", CultureInfo.InvariantCulture);
        if (scalar.TryGetValue<DateTimeOffset>(out var offset)) return offset.ToString("O", CultureInfo.InvariantCulture);
        if (scalar.TryGetValue<DateTime>(out var dateTime)) return dateTime.ToString("O", CultureInfo.InvariantCulture);
        return scalar.ToJsonString().Trim('"');
    }

    private static ValueTransformationException Error(
        string fieldName,
        string targetColumn,
        string reasonCode,
        string details) =>
        new(fieldName, targetColumn, reasonCode, $"{fieldName} → {targetColumn}: {details}");

    private static bool IsString(string type) =>
        type.Contains("char", StringComparison.Ordinal) ||
        type.Contains("text", StringComparison.Ordinal) ||
        type is "xml" or "json" or "jsonb" or "citext";

    private static bool IsInteger(string type) => type is
        "tinyint" or "smallint" or "mediumint" or "int" or "integer" or "bigint" or
        "int2" or "int4" or "int8" or "smallserial" or "serial" or "bigserial";

    private static bool IsDecimal(string type) => type is
        "decimal" or "numeric" or "money" or "smallmoney";

    private static bool IsFloatingPoint(string type) => type is
        "float" or "real" or "double" or "double precision";

    private static bool IsBoolean(string type) => type is "bit" or "bool" or "boolean";

    private static bool IsDateTime(string type) =>
        type.Contains("date", StringComparison.Ordinal) ||
        type.Contains("time", StringComparison.Ordinal);

    private static bool IsGuid(string type) => type is "uuid" or "uniqueidentifier";
}
