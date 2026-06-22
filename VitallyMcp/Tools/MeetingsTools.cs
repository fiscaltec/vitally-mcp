using System.ComponentModel;
using ModelContextProtocol.Server;

namespace VitallyMcp.Tools;

[McpServerToolType]
public static class MeetingsTools
{
    [McpServerTool(Name = "List_meetings", Title = "List meetings", ReadOnly = true, Destructive = false), Description("List Vitally meetings with optional pagination, filtering and field selection")]
    public static async Task<string> ListMeetings(
        VitallyService vitallyService,
        [Description("Maximum number of meetings to return (default: 20, max: 100). Ignored when a created date range is supplied.")] int limit = 20,
        [Description("Pagination cursor from previous response (use the 'next' value). Ignored when a created date range is supplied.")] string? from = null,
        [Description("Comma-separated list of fields to include. Defaults to: id,title,externalId,startDateTime,endDateTime,location,source,accountIds,organizationIds,participants,createdAt,updatedAt. Client-side filtering.")] string? fields = null,
        [Description("Sort by field: 'createdAt' or 'updatedAt' (default: updatedAt). Ignored when a created date range is supplied.")] string? sortBy = null,
        [Description("Set to 'true' to include archived meetings")] string? archived = null,
        [Description("Comma-separated list of trait names to include. If specified, must also include 'traits' in fields parameter. Client-side filtering.")] string? traits = null,
        [Description("ISO-8601 lower bound on createdAt. When set, the server pages and filters by date client-side (Vitally has no date filter) and returns {results, truncated, pagesFetched}; limit/from/sortBy are ignored.")] string? createdAfter = null,
        [Description("ISO-8601 upper bound on createdAt. See createdAfter.")] string? createdBefore = null)
    {
        var additionalParams = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(archived)) additionalParams["archived"] = archived;

        if (!string.IsNullOrWhiteSpace(createdAfter) || !string.IsNullOrWhiteSpace(createdBefore))
        {
            return await vitallyService.GetByCreatedRangeAsync("meetings", createdAfter, createdBefore, fields, traits, defaultsKey: "meetings", additionalParams: additionalParams);
        }

        return await vitallyService.GetResourcesAsync("meetings", limit, from, fields, sortBy, additionalParams, traits);
    }

    [McpServerTool(Name = "List_meetings_by_account", Title = "List meetings by account", ReadOnly = true, Destructive = false), Description("List Vitally meetings for a specific account")]
    public static async Task<string> ListMeetingsByAccount(
        VitallyService vitallyService,
        [Description("The account ID")] string accountId,
        [Description("Maximum number of meetings to return (default: 20, max: 100)")] int limit = 20,
        [Description("Pagination cursor from previous response (use the 'next' value)")] string? from = null,
        [Description("Comma-separated list of fields to include. Client-side filtering.")] string? fields = null,
        [Description("Sort by field: 'createdAt' or 'updatedAt' (default: updatedAt)")] string? sortBy = null,
        [Description("Comma-separated list of trait names to include. If specified, must also include 'traits' in fields parameter. Client-side filtering.")] string? traits = null)
    {
        return await vitallyService.GetResourcesAsync($"accounts/{accountId}/meetings", limit, from, fields, sortBy, null, traits);
    }

    [McpServerTool(Name = "List_meetings_by_organization", Title = "List meetings by organization", ReadOnly = true, Destructive = false), Description("List Vitally meetings for a specific organisation")]
    public static async Task<string> ListMeetingsByOrganization(
        VitallyService vitallyService,
        [Description("The organisation ID")] string organizationId,
        [Description("Maximum number of meetings to return (default: 20, max: 100)")] int limit = 20,
        [Description("Pagination cursor from previous response (use the 'next' value)")] string? from = null,
        [Description("Comma-separated list of fields to include. Client-side filtering.")] string? fields = null,
        [Description("Sort by field: 'createdAt' or 'updatedAt' (default: updatedAt)")] string? sortBy = null,
        [Description("Comma-separated list of trait names to include. If specified, must also include 'traits' in fields parameter. Client-side filtering.")] string? traits = null)
    {
        return await vitallyService.GetResourcesAsync($"organizations/{organizationId}/meetings", limit, from, fields, sortBy, null, traits);
    }

    [McpServerTool(Name = "Get_meeting", Title = "Get meeting", ReadOnly = true, Destructive = false), Description("Get a single Vitally meeting by ID or externalId")]
    public static async Task<string> GetMeeting(
        VitallyService vitallyService,
        [Description("The meeting ID or externalId")] string id,
        [Description("Comma-separated list of fields to include. Client-side filtering.")] string? fields = null,
        [Description("Comma-separated list of trait names to include. If specified, must also include 'traits' in fields parameter. Client-side filtering.")] string? traits = null)
    {
        return await vitallyService.GetResourceByIdAsync("meetings", id, fields, traits);
    }

    [McpServerTool(Name = "Create_meeting", Title = "Create meeting", ReadOnly = false, Destructive = false), Description("Create a new Vitally meeting")]
    public static async Task<string> CreateMeeting(
        VitallyService vitallyService,
        [Description("JSON body containing meeting data. Required: title (string), externalId (string), participants (array of objects with userId/vitallyUserId/email plus type 'organizer' or 'attendee'). Optional: description, location, startDateTime, endDateTime, recordingUrl, summary, keyPoints, riskAssessment, source, traits (object), transcript")] string jsonBody)
    {
        return await vitallyService.CreateResourceAsync("meetings", jsonBody);
    }

    [McpServerTool(Name = "Update_meeting", Title = "Update meeting", ReadOnly = false, Destructive = true), Description("Update an existing Vitally meeting")]
    public static async Task<string> UpdateMeeting(
        VitallyService vitallyService,
        [Description("The meeting ID or externalId")] string id,
        [Description("JSON body containing fields to update. All fields optional: title, description, location, startDateTime, endDateTime, recordingUrl, summary, keyPoints, riskAssessment, traits (object)")] string jsonBody)
    {
        return await vitallyService.UpdateResourceAsync("meetings", id, jsonBody);
    }

    [McpServerTool(Name = "Delete_meeting", Title = "Delete meeting", ReadOnly = false, Destructive = true), Description("Archive a Vitally meeting (soft delete)")]
    public static async Task<string> DeleteMeeting(
        VitallyService vitallyService,
        [Description("The meeting ID or externalId")] string id)
    {
        return await vitallyService.DeleteResourceAsync("meetings", id);
    }

    [McpServerTool(Name = "Add_meeting_participant", Title = "Add meeting participant", ReadOnly = false, Destructive = false), Description("Add a participant to a Vitally meeting")]
    public static async Task<string> AddMeetingParticipant(
        VitallyService vitallyService,
        [Description("The meeting ID")] string id,
        [Description("JSON body for the participant. Required: exactly one of userId, vitallyUserId, or email; plus type ('organizer' or 'attendee'). Optional: responseStatus, isOrganizer")] string jsonBody)
    {
        return await vitallyService.PostRawAsync($"meetings/{id}/participants", jsonBody);
    }

    [McpServerTool(Name = "Remove_meeting_participant", Title = "Remove meeting participant", ReadOnly = false, Destructive = true), Description("Remove a participant from a Vitally meeting")]
    public static async Task<string> RemoveMeetingParticipant(
        VitallyService vitallyService,
        [Description("The meeting ID")] string id,
        [Description("The participant ID (from the participant object's 'id' field)")] string participantId)
    {
        return await vitallyService.DeleteRawAsync($"meetings/{id}/participants/{participantId}");
    }

    [McpServerTool(Name = "List_meeting_transcripts", Title = "List meeting transcripts", ReadOnly = true, Destructive = false), Description("List Vitally meeting transcripts with optional pagination")]
    public static async Task<string> ListMeetingTranscripts(
        VitallyService vitallyService,
        [Description("Maximum number of transcripts to return (default: 20, max: 100)")] int limit = 20,
        [Description("Pagination cursor from previous response (use the 'next' value)")] string? from = null,
        [Description("Comma-separated list of fields to include. Defaults to: id,meetingId,createdAt,updatedAt. Client-side filtering.")] string? fields = null,
        [Description("Sort by field: 'createdAt' or 'updatedAt' (default: updatedAt)")] string? sortBy = null)
    {
        return await vitallyService.GetResourcesAsync("meetingTranscripts", limit, from, fields, sortBy, null, null);
    }

    [McpServerTool(Name = "Get_meeting_transcript", Title = "Get meeting transcript", ReadOnly = true, Destructive = false), Description("Get a single Vitally meeting transcript by its transcript ID")]
    public static async Task<string> GetMeetingTranscript(
        VitallyService vitallyService,
        [Description("The transcript ID (not the meeting ID)")] string id,
        [Description("Comma-separated list of fields to include. Client-side filtering.")] string? fields = null)
    {
        return await vitallyService.GetResourceByIdAsync("meetingTranscripts", id, fields, null);
    }

    [McpServerTool(Name = "Get_transcript_for_meeting", Title = "Get transcript for meeting", ReadOnly = true, Destructive = false), Description("Get the transcript belonging to a specific meeting (returned in full, no field filtering)")]
    public static async Task<string> GetTranscriptForMeeting(
        VitallyService vitallyService,
        [Description("The meeting ID or externalId")] string meetingId)
    {
        return await vitallyService.GetRawAsync($"meetings/{meetingId}/transcript");
    }

    [McpServerTool(Name = "Create_meeting_transcript", Title = "Create meeting transcript", ReadOnly = false, Destructive = true), Description("Create or replace the transcript for a Vitally meeting")]
    public static async Task<string> CreateMeetingTranscript(
        VitallyService vitallyService,
        [Description("The meeting ID or externalId")] string meetingId,
        [Description("JSON body with required 'transcript' array of monologues. Each monologue has a 'speaker' object (externalId required; optional email, name) and a 'sentences' array (each with text, startTime, endTime)")] string jsonBody)
    {
        return await vitallyService.PostRawAsync($"meetings/{meetingId}/transcript", jsonBody);
    }
}
