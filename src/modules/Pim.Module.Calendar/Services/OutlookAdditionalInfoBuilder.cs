using System.Globalization;
using System.Text.Json;
using Pim.Module.Calendar.DTOs;
using Pim.Module.Calendar.Entities;

namespace Pim.Module.Calendar.Services;

public static class OutlookAdditionalInfoBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly HashSet<string> AllowlistedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "responseRequested", "allowNewTimeProposals", "hideAttendees"
    };

    private static readonly HashSet<string> EntitySyncPropertyKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "OutlookSyncState", "OutlookEventType", "OriginalStartTimeZone", "OriginalEndTimeZone"
    };

    private static readonly HashSet<string> SecretKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "accesstoken", "refreshtoken", "clientsecret", "authorization",
        "token", "secret", "password", "apikey"
    };

    private static readonly HashSet<string> BlockedRootKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "sourceSnapshot", "body", "bodyPreview", "id", "etag",
        "@odata.etag", "changeKey", "iCalUId"
    };

    private const int MaxValueLength = 200;

    public static OutlookAdditionalInfoDto? Build(EventEntity entity)
    {
        if (!string.Equals(entity.Source, "outlook", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(entity.Source, "outlook-ics", StringComparison.OrdinalIgnoreCase))
            return null;

        var metadata = ParseExternalMetadata(entity.ExternalMetadataJson);

        var groups = new List<OutlookAdditionalInfoGroupDto>();
        var hiddenFieldCount = 0;
        var metadataItems = new List<OutlookAdditionalInfoItemDto>();
        var syncItems = new List<OutlookAdditionalInfoItemDto>();

        AddEntitySyncProperties(entity, syncItems);

        var metadataHasContent = false;

        if (metadata is not null)
        {
            foreach (var kvp in metadata.Value.EnumerateObject())
            {
                var key = kvp.Name;
                metadataHasContent = true;

                if (BlockedRootKeys.Contains(key))
                    continue;

                if (IsExtendedPropertiesKey(key))
                {
                    hiddenFieldCount += CountExtendedProperties(kvp.Value);
                    continue;
                }

                if (IsSecretKey(key))
                {
                    hiddenFieldCount++;
                    continue;
                }

                if (AllowlistedKeys.Contains(key))
                {
                    var item = ExtractScalarItem(key, kvp.Value, out _);
                    if (item is not null)
                        metadataItems.Add(item);
                    continue;
                }

                if (EntitySyncPropertyKeys.Contains(key))
                    continue;

                if (IsUnmappedKey(key) && kvp.Value.ValueKind == JsonValueKind.Object)
                {
                    ProcessUnmapped(kvp.Value, metadataItems, ref hiddenFieldCount);
                    continue;
                }

                hiddenFieldCount++;
            }
        }

        if (syncItems.Count > 0)
            groups.Add(new OutlookAdditionalInfoGroupDto("sync", "同步信息", syncItems));

        if (metadataItems.Count > 0)
            groups.Add(new OutlookAdditionalInfoGroupDto("metadata", "Outlook 字段", metadataItems));

        if (groups.Count == 0 && hiddenFieldCount == 0 && !metadataHasContent)
            return null;

        return new OutlookAdditionalInfoDto(groups, hiddenFieldCount);
    }

    private static void AddEntitySyncProperties(EventEntity entity, List<OutlookAdditionalInfoItemDto> syncItems)
    {
        foreach (var key in EntitySyncPropertyKeys)
        {
            var value = GetEntityPropertyValue(entity, key);
            if (value is not null)
                syncItems.Add(new OutlookAdditionalInfoItemDto(key, GetChineseLabel(key), value));
        }
    }

    private static void ProcessUnmapped(JsonElement unmappedObj, List<OutlookAdditionalInfoItemDto> metadataItems, ref int hiddenFieldCount)
    {
        foreach (var kvp in unmappedObj.EnumerateObject())
        {
            var key = kvp.Name;

            if (IsExtendedPropertiesKey(key))
            {
                hiddenFieldCount += CountExtendedProperties(kvp.Value);
                continue;
            }

            if (IsSecretKey(key))
            {
                hiddenFieldCount++;
                continue;
            }

            if (AllowlistedKeys.Contains(key))
            {
                var item = ExtractScalarItem(key, kvp.Value, out _);
                if (item is not null)
                    metadataItems.Add(item);
                continue;
            }

            hiddenFieldCount++;
        }
    }

    private static JsonElement? ParseExternalMetadata(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement.Clone();
            if (root.ValueKind != JsonValueKind.Object)
                return null;
            return root;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static OutlookAdditionalInfoItemDto? ExtractScalarItem(string key, JsonElement value, out bool isSecretValue)
    {
        isSecretValue = false;

        if (value.ValueKind == JsonValueKind.Object || value.ValueKind == JsonValueKind.Array)
            return null;

        var raw = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };

        if (raw is null)
            return null;

        if (IsSecretValue(raw))
        {
            isSecretValue = true;
            return null;
        }

        var truncated = raw.Length > MaxValueLength ? raw[..MaxValueLength] : raw;
        return new OutlookAdditionalInfoItemDto(key, GetChineseLabel(key), truncated);
    }

    private static int CountExtendedProperties(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
            return 0;

        var count = 0;
        foreach (var prop in element.EnumerateArray())
        {
            if (prop.ValueKind == JsonValueKind.Object)
                count++;
        }
        return count;
    }

    private static bool IsSecretKey(string key)
    {
        var lower = key.AsSpan();
        foreach (var secret in SecretKeys)
        {
            if (lower.Equals(secret, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool IsSecretValue(string? value)
    {
        if (value is null)
            return false;
        var lower = value.AsSpan();
        return lower.Contains("Bearer ".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || lower.Contains("token".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || lower.Contains("secret".AsSpan(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsExtendedPropertiesKey(string key)
    {
        return key.Equals("singleValueExtendedProperties", StringComparison.OrdinalIgnoreCase)
            || key.Equals("multiValueExtendedProperties", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUnmappedKey(string key)
    {
        return key.Equals("unmapped", StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetEntityPropertyValue(EventEntity entity, string key)
    {
        return key switch
        {
            "OutlookSyncState" => entity.OutlookSyncState,
            "OutlookEventType" => entity.OutlookEventType,
            "OriginalStartTimeZone" => entity.OriginalStartTimeZone,
            "OriginalEndTimeZone" => entity.OriginalEndTimeZone,
            _ => null
        };
    }

    private static string GetChineseLabel(string key)
    {
        return key switch
        {
            "responseRequested" => "需要响应",
            "allowNewTimeProposals" => "允许新时间提议",
            "hideAttendees" => "隐藏参会者",
            "OutlookSyncState" => "同步状态",
            "OutlookEventType" => "事件类型",
            "OriginalStartTimeZone" => "原始开始时间时区",
            "OriginalEndTimeZone" => "原始结束时间时区",
            _ => key
        };
    }
}
