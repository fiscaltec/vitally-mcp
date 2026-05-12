using System.ComponentModel;
using ModelContextProtocol.Server;

namespace VitallyMcp.Tools;

[McpServerToolType]
public static class SurveysTools
{
    [McpServerTool(Name = "List_survey_responses", Title = "List survey responses", ReadOnly = true, Destructive = false), Description("List responses for a Vitally custom survey. Responses are returned in full (no field filtering) because the survey API uses a 'data' envelope rather than the standard 'results' envelope.")]
    public static async Task<string> ListSurveyResponses(
        VitallyService vitallyService,
        [Description("The survey ID")] string surveyId)
    {
        return await vitallyService.GetRawAsync($"surveys/{surveyId}/responses");
    }

    [McpServerTool(Name = "Get_survey_response", Title = "Get survey response", ReadOnly = true, Destructive = false), Description("Get a single Vitally survey response by its response ID. Returns the response with its full questionResponses array.")]
    public static async Task<string> GetSurveyResponse(
        VitallyService vitallyService,
        [Description("The survey response ID")] string responseId)
    {
        return await vitallyService.GetRawAsync($"surveyResponses/{responseId}");
    }

    [McpServerTool(Name = "Get_survey_question", Title = "Get survey question", ReadOnly = true, Destructive = false), Description("Get metadata for a single Vitally survey question by its question ID")]
    public static async Task<string> GetSurveyQuestion(
        VitallyService vitallyService,
        [Description("The survey question ID")] string questionId)
    {
        return await vitallyService.GetRawAsync($"surveyQuestions/{questionId}");
    }
}
