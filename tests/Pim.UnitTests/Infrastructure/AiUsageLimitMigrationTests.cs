using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Data;
using Pim.UnitTests.Harness.RealDb;
using Xunit;

namespace Pim.UnitTests.InfrastructureCoverage;

[Trait("DataSource", "RealDb")]
public sealed class AiUsageLimitMigrationTests
{
    private const string PreviousPublishedHead = "20261123040416_AddNoteProcessingAutoSetting";

    [SkippableFact]
    public async Task LegacyModelsAndRequestLogsReceiveStableConnectionBackfill()
    {
        await using var database = await TempMigrationDatabase.CreateAsync(
            "test_ai_usage_limit_backfill");
        await database.MigrateToAsync(PreviousPublishedHead);

        var profileId = Guid.NewGuid();
        var matchedLogId = Guid.NewGuid();
        var orphanLogId = Guid.NewGuid();
        await database.ExecuteAsync($$"""
INSERT INTO ai_model_profiles
    (id, revision, name, normalized_name, base_url, model, enabled, capabilities,
     api_key_secret, timeout_seconds, max_output_tokens, created_at, updated_at)
VALUES
    ('{{profileId:D}}', '{{Guid.NewGuid():D}}', '旧模型', '旧模型', 'https://legacy.example.invalid/v1/',
     'legacy-model', true, 1, 'legacy-secret', 60, 1024, now(), now());

INSERT INTO ai_request_logs
    (id, module, purpose, source_object_type, source_object_id, provider, model,
     correlation_id, status, attempt_number, max_attempts, started_at,
     request_messages_json, request_payload_json, response_raw_json,
     schema_validation_errors_json, input_chars, output_chars, input_hash, output_hash,
     metadata_json, model_profile_id)
VALUES
    ('{{matchedLogId:D}}', 'system', 'ai.text-call', 'ai-model', 'legacy',
     'openai-compatible', 'legacy-model', 'legacy-matched', 'succeeded', 1, 1, now(),
     '[]'::jsonb, '{}'::jsonb, '{}'::jsonb, '[]'::jsonb, 0, 0, 'input-hash', 'output-hash',
     '{}'::jsonb, '{{profileId:D}}'),
    ('{{orphanLogId:D}}', 'system', 'ai.text-call', 'ai-model', 'legacy-orphan',
     'openai-compatible', 'legacy-model', 'legacy-orphan', 'succeeded', 1, 1, now(),
     '[]'::jsonb, '{}'::jsonb, '{}'::jsonb, '[]'::jsonb, 0, 0, 'input-hash-2', 'output-hash-2',
     '{}'::jsonb, NULL);
""");

        await database.MigrateAsync();

        var profile = await database.Db.AiModelProfiles.AsNoTracking()
            .SingleAsync(x => x.Id == profileId);
        var matchedLog = await database.Db.AiRequestLogs.AsNoTracking()
            .SingleAsync(x => x.Id == matchedLogId);
        var orphanLog = await database.Db.AiRequestLogs.AsNoTracking()
            .SingleAsync(x => x.Id == orphanLogId);
        var connection = await database.Db.AiProviderConnections.AsNoTracking()
            .SingleAsync(x => x.Id == profileId);
        var rule = await database.Db.AiUsageLimitRules.AsNoTracking()
            .SingleAsync(x => x.ConnectionId == profileId);

        Assert.Equal(profileId, profile.ProviderConnectionId);
        Assert.Equal(profileId, matchedLog.ProviderConnectionId);
        Assert.Null(orphanLog.ProviderConnectionId);
        Assert.Equal(profile.Name, connection.Name);
        Assert.False(rule.Enabled);
        Assert.Equal("month", rule.Period);
        Assert.Equal("Asia/Shanghai", rule.TimeZoneId);
    }
}
