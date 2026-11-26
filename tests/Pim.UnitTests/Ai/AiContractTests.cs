using System.Text.Json;
using Pim.Core.Ai;
using Pim.Infrastructure.Ai;
using Xunit;

namespace Pim.UnitTests.Ai;

public class AiContractTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public void SaveSettings_MissingPurposeFieldsPreserveStoredValues()
    {
        var request = JsonSerializer.Deserialize<SaveAiModelSettingsRequest>(
            """{"expectedRevision":"11111111-1111-1111-1111-111111111111","enabled":true,"textModelId":null,"visionModelId":null,"transcriptionModelId":null}""",
            Json)!;

        Assert.False(request.HasNoteOrganizationModelId);
        Assert.False(request.HasNoteTodoModelId);
        Assert.False(request.HasCalendarPlanningModelId);
        Assert.Null(request.NoteOrganizationModelId);
    }

    [Fact]
    public void SaveSettings_ExplicitNullPurposeFieldClearsIt()
    {
        var request = JsonSerializer.Deserialize<SaveAiModelSettingsRequest>(
            """{"expectedRevision":"11111111-1111-1111-1111-111111111111","enabled":true,"textModelId":null,"visionModelId":null,"transcriptionModelId":null,"noteTodoModelId":null}""",
            Json)!;

        Assert.True(request.HasNoteTodoModelId);
        Assert.Null(request.NoteTodoModelId);
        Assert.False(request.HasNoteOrganizationModelId);
    }

    [Fact]
    public void SaveSettings_ExplicitPurposeModelIsConsumed()
    {
        var id = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var request = JsonSerializer.Deserialize<SaveAiModelSettingsRequest>(
            $$"""{"expectedRevision":"11111111-1111-1111-1111-111111111111","enabled":true,"textModelId":null,"visionModelId":null,"transcriptionModelId":null,"calendarPlanningModelId":"{{id}}"}""",
            Json)!;

        Assert.True(request.HasCalendarPlanningModelId);
        Assert.Equal(id, request.CalendarPlanningModelId);
    }

    [Fact]
    public void PurposeResolver_FallsBackOnlyWhenThePurposeIsUnset()
    {
        var text = Guid.NewGuid();
        var vision = Guid.NewGuid();
        var purpose = Guid.NewGuid();
        var runtime = new Pim.Infrastructure.Data.Entities.AiModelRuntimeEntity
        {
            TextModelId = text,
            VisionModelId = vision,
            NoteOrganizationModelId = purpose
        };

        Assert.Equal(purpose, AiPurposeModelResolver.ResolveId(runtime, AiPurpose.NoteOrganization));
        Assert.Equal(text, AiPurposeModelResolver.ResolveId(runtime, AiPurpose.NoteTodo));
        Assert.Equal(text, AiPurposeModelResolver.ResolveId(runtime, AiPurpose.CalendarPlanning));

        runtime.NoteOrganizationModelId = null;
        Assert.Equal(text, AiPurposeModelResolver.ResolveId(runtime, AiPurpose.NoteOrganization));
        Assert.Equal(vision, AiPurposeModelResolver.ResolveId(runtime, AiPurpose.NoteOrganization, hasImages: true));
        Assert.Equal(text, AiPurposeModelResolver.ResolveDestinationDefaultId(runtime,
            AiPurpose.NoteOrganization));

        runtime.TextModelId = null;
        Assert.Equal(vision, AiPurposeModelResolver.ResolveDestinationDefaultId(runtime,
            AiPurpose.NoteOrganization));
        Assert.Null(AiPurposeModelResolver.ResolveId(runtime, AiPurpose.NoteOrganization));
    }

    [Fact]
    public void CapabilityFlags_KeepStablePersistenceBits()
    {
        Assert.Equal(1, AiModelCapabilityFlags.Text);
        Assert.Equal(2, AiModelCapabilityFlags.Vision);
        Assert.Equal(4, AiModelCapabilityFlags.Transcription);
        Assert.True(AiModelCapabilityFlags.Has(3, AiModelCapability.Text));
        Assert.True(AiModelCapabilityFlags.Has(3, AiModelCapability.Vision));
        Assert.False(AiModelCapabilityFlags.Has(3, AiModelCapability.Transcription));
    }

    [Fact]
    public void ConnectionTestRequest_SerializesCapabilityAsString()
    {
        var request = new AiConnectionTestRequest(Guid.NewGuid(), Guid.NewGuid(), AiModelCapability.Vision);
        var json = JsonSerializer.Serialize(request, Json);

        Assert.Contains("\"capability\":\"Vision\"", json);
        Assert.DoesNotContain("\"capability\":1", json);
    }
}
