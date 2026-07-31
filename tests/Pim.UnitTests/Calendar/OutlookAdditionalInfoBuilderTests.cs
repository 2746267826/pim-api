using Pim.Module.Calendar.DTOs;
using Pim.Module.Calendar.Entities;
using Pim.Module.Calendar.Services;
using Xunit;

namespace Pim.UnitTests.Calendar;

public sealed class OutlookAdditionalInfoBuilderTests
{
    [Fact]
    public void Build_ReturnsAllowlistedFields_ForFullMetadata()
    {
        var entity = new EventEntity
        {
            Source = "outlook",
            ExternalMetadataJson = /*lang=json,strict*/ """
            {
                "responseRequested": true,
                "allowNewTimeProposals": false,
                "hideAttendees": true,
                "singleValueExtendedProperties": [
                    { "id": "String {guid} Name Custom", "value": "custom-value" }
                ]
            }
            """,
            OutlookSyncState = "synced",
            OutlookEventType = "singleInstance",
            OriginalStartTimeZone = "Asia/Shanghai",
            OriginalEndTimeZone = "Asia/Shanghai",
        };

        var result = OutlookAdditionalInfoBuilder.Build(entity);

        Assert.NotNull(result);
        Assert.True(result.HiddenFieldCount >= 0);

        var allItems = result.Groups
            .SelectMany(g => g.Items)
            .ToDictionary(i => i.Key, i => i.Value, StringComparer.OrdinalIgnoreCase);

        Assert.Contains("responseRequested", allItems.Keys);
        Assert.Contains("allowNewTimeProposals", allItems.Keys);
        Assert.Contains("hideAttendees", allItems.Keys);
    }

    [Fact]
    public void Build_DoesNotReturnRawBody()
    {
        var entity = new EventEntity
        {
            Source = "outlook",
            ExternalMetadataJson = /*lang=json,strict*/ """
            {
                "sourceSnapshot": {
                    "body": { "contentType": "html", "content": "secret body content" }
                }
            }
            """,
        };

        var result = OutlookAdditionalInfoBuilder.Build(entity);

        Assert.NotNull(result);
        var allValues = string.Join(" ", result.Groups
            .SelectMany(g => g.Items)
            .Select(i => i.Value));
        Assert.DoesNotContain("secret body content", allValues, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_DoesNotReturnEmailContentOrIds()
    {
        var entity = new EventEntity
        {
            Source = "outlook",
            ExternalMetadataJson = /*lang=json,strict*/ """
            {
                "id": "AAMkAD-event-id",
                "bodyPreview": "email-like content",
                "iCalUId": "ical-uid-value",
                "etag": "W-\"etag-value\""
            }
            """,
            OutlookEventId = "AAMkAD-event-id",
            OutlookEtag = "W-\"etag-value\"",
        };

        var result = OutlookAdditionalInfoBuilder.Build(entity);

        Assert.NotNull(result);
        var allValues = string.Join(" ", result.Groups
            .SelectMany(g => g.Items)
            .Select(i => i.Value));
        Assert.DoesNotContain("AAMkAD-", allValues);
        Assert.DoesNotContain("etag-value", allValues, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_DoesNotReturnTokenOrSecretLikeKeys()
    {
        var entity = new EventEntity
        {
            Source = "outlook",
            ExternalMetadataJson = /*lang=json,strict*/ """
            {
                "accessToken": "secret-token",
                "refreshToken": "secret-refresh",
                "clientSecret": "very-secret",
                "authorization": "Bearer tok"
            }
            """,
        };

        var result = OutlookAdditionalInfoBuilder.Build(entity);

        Assert.NotNull(result);
        var allKeys = result.Groups
            .SelectMany(g => g.Items)
            .Select(i => i.Key);
        Assert.DoesNotContain("accessToken", allKeys);
        Assert.DoesNotContain("refreshToken", allKeys);
        Assert.DoesNotContain("clientSecret", allKeys);
        Assert.DoesNotContain("authorization", allKeys);
    }

    [Fact]
    public void Build_TruncatesValuesTo200Characters()
    {
        var longValue = new string('A', 500);
        var entity = new EventEntity
        {
            Source = "outlook",
            ExternalMetadataJson = $$"""
            {
                "responseRequested": "{{longValue}}"
            }
            """,
        };

        var result = OutlookAdditionalInfoBuilder.Build(entity);

        Assert.NotNull(result);
        foreach (var item in result.Groups.SelectMany(g => g.Items))
        {
            Assert.True(item.Value.Length <= 200,
                $"Value for key '{item.Key}' is {item.Value.Length} chars, expected <= 200");
        }
    }

    [Fact]
    public void Build_LimitsNestingDepthTo3()
    {
        var entity = new EventEntity
        {
            Source = "outlook",
            ExternalMetadataJson = /*lang=json,strict*/ """
            {
                "level1": {
                    "level2": {
                        "level3": {
                            "level4": "should not appear"
                        }
                    }
                }
            }
            """,
        };

        var result = OutlookAdditionalInfoBuilder.Build(entity);

        Assert.NotNull(result);
        var allValues = string.Join(" ", result.Groups
            .SelectMany(g => g.Items)
            .Select(i => i.Value));
        Assert.DoesNotContain("level4", allValues);
    }

    [Fact]
    public void Build_HidesExtendedPropertyValues_ReportsCount()
    {
        var entity = new EventEntity
        {
            Source = "outlook",
            ExternalMetadataJson = /*lang=json,strict*/ """
            {
                "responseRequested": false,
                "singleValueExtendedProperties": [
                    { "id": "String {guid} Name Custom1", "value": "hidden-value-1" },
                    { "id": "String {guid} Name Custom2", "value": "hidden-value-2" }
                ]
            }
            """,
        };

        var result = OutlookAdditionalInfoBuilder.Build(entity);

        Assert.NotNull(result);
        Assert.True(result.HiddenFieldCount >= 2);

        var allValues = string.Join(" ", result.Groups
            .SelectMany(g => g.Items)
            .Select(i => i.Value));
        Assert.DoesNotContain("hidden-value-1", allValues);
        Assert.DoesNotContain("hidden-value-2", allValues);
    }

    [Fact]
    public void Build_WithUnmappedNestedAndEntityFields_IncludesSafeFieldsOnly()
    {
        var entity = new EventEntity
        {
            Source = "outlook",
            ExternalMetadataJson = /*lang=json,strict*/ """
            {
                "sourceSnapshot": {
                    "body": { "content": "raw body" }
                },
                "unmapped": {
                    "responseRequested": true,
                    "futureGraphField": { "nested": "private-value" },
                    "organizer": { "emailAddress": { "address": "owner@example.test" } }
                }
            }
            """,
            OutlookSyncState = "synced",
            OutlookEventType = "singleInstance",
            OriginalStartTimeZone = "Asia/Shanghai",
        };

        var result = OutlookAdditionalInfoBuilder.Build(entity);

        Assert.NotNull(result);
        var allItems = result.Groups
            .SelectMany(g => g.Items)
            .ToDictionary(i => i.Key, i => i.Value, StringComparer.OrdinalIgnoreCase);

        Assert.Contains("responseRequested", allItems.Keys);
        Assert.Equal("true", allItems["responseRequested"]);

        Assert.Contains("OutlookSyncState", allItems.Keys);
        Assert.Contains("OutlookEventType", allItems.Keys);
        Assert.Contains("OriginalStartTimeZone", allItems.Keys);

        var allKeys = string.Join(" ", result.Groups.SelectMany(g => g.Items).Select(i => i.Key));
        var allValues = string.Join(" ", result.Groups.SelectMany(g => g.Items).Select(i => i.Value));

        Assert.DoesNotContain("futureGraphField", allKeys, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private-value", allValues, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("owner@example.test", allValues, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("raw body", allValues, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sourceSnapshot", allKeys, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("unmapped", allKeys, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_WithEmptyMetadataAndEntityFields_ReturnsSyncGroup()
    {
        var entity = new EventEntity
        {
            Source = "outlook",
            ExternalMetadataJson = "{}",
            OutlookSyncState = "synced",
            OutlookEventType = "singleInstance",
        };

        var result = OutlookAdditionalInfoBuilder.Build(entity);

        Assert.NotNull(result);
        var allItems = result.Groups
            .SelectMany(g => g.Items)
            .ToDictionary(i => i.Key, i => i.Value, StringComparer.OrdinalIgnoreCase);

        Assert.Contains("OutlookSyncState", allItems.Keys);
        Assert.Contains("OutlookEventType", allItems.Keys);
    }

    [Fact]
    public void Build_ReturnsNull_ForEmptyExternalMetadata()
    {
        var entity = new EventEntity
        {
            Source = "outlook",
            ExternalMetadataJson = "{}",
        };

        var result = OutlookAdditionalInfoBuilder.Build(entity);

        Assert.Null(result);
    }

    [Fact]
    public void Build_ReturnsNull_ForBareV2Envelope()
    {
        var entity = new EventEntity
        {
            Source = "outlook",
            ExternalMetadataJson = /*lang=json,strict*/ """
            {
                "mappingVersion": 2,
                "sourceSnapshot": {
                    "body": { "contentType": "html", "content": "raw body content" },
                    "event": {
                        "attendees": [
                            { "status": { "response": "accepted" }, "emailAddress": { "address": "guest@example.test" } }
                        ],
                        "onlineMeeting": { "conferenceId": "987654321", "tollNumber": "+1 555 0100" }
                    }
                },
                "unmapped": {}
            }
            """,
        };

        var result = OutlookAdditionalInfoBuilder.Build(entity);

        Assert.Null(result);
    }

    [Fact]
    public void Build_WithV2EnvelopeAndUnmappedContent_DoesNotLeakSnapshotOrCountMappingVersion()
    {
        var entity = new EventEntity
        {
            Source = "outlook",
            ExternalMetadataJson = /*lang=json,strict*/ """
            {
                "mappingVersion": 2,
                "sourceSnapshot": {
                    "body": { "content": "raw body content" },
                    "event": {
                        "attendees": [
                            { "status": { "response": "accepted" }, "emailAddress": { "address": "guest@example.test" } }
                        ],
                        "onlineMeeting": { "conferenceId": "987654321" }
                    }
                },
                "unmapped": {
                    "responseRequested": true
                }
            }
            """,
        };

        var result = OutlookAdditionalInfoBuilder.Build(entity);

        Assert.NotNull(result);
        Assert.Equal(0, result.HiddenFieldCount);

        var allKeys = string.Join(" ", result.Groups.SelectMany(g => g.Items).Select(i => i.Key));
        var allValues = string.Join(" ", result.Groups.SelectMany(g => g.Items).Select(i => i.Value));
        Assert.Contains("responseRequested", allKeys);
        Assert.DoesNotContain("sourceSnapshot", allKeys, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("mappingVersion", allKeys, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("raw body content", allValues, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("guest@example.test", allValues, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("987654321", allValues, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_ReturnsNull_ForManualEvent()
    {
        var entity = new EventEntity
        {
            Source = "manual",
            ExternalMetadataJson = "{}",
        };

        var result = OutlookAdditionalInfoBuilder.Build(entity);

        Assert.Null(result);
    }

    [Fact]
    public void Build_WithOutlookSourceDifferentCase_ReturnsMetadata()
    {
        var entity = new EventEntity
        {
            Source = "Outlook",
            ExternalMetadataJson = /*lang=json,strict*/ """
            {
                "responseRequested": true
            }
            """,
        };

        var result = OutlookAdditionalInfoBuilder.Build(entity);

        Assert.NotNull(result);
        var allItems = result.Groups
            .SelectMany(g => g.Items)
            .ToDictionary(i => i.Key, i => i.Value, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("responseRequested", allItems.Keys);
    }
}
