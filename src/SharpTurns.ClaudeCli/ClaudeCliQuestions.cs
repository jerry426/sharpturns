using System.Text.Json;
using System.Text.Json.Nodes;

namespace SharpTurns.ClaudeCli;

public sealed record ClaudeCliQuestionOption(string Label, string? Description, string? Preview);

public sealed record ClaudeCliQuestion(string Text, string? Header, IReadOnlyList<ClaudeCliQuestionOption> Options,
    bool MultiSelect);

/// <summary>
/// The AskUserQuestion tool's input and answer shape: the host returns the original input with an "answers" object
/// keyed by question text. Multi-select answers join the chosen labels with ", ".
/// </summary>
public static class ClaudeCliQuestions
{
    /// <summary>Throws InvalidDataException for input the host cannot present.</summary>
    public static IReadOnlyList<ClaudeCliQuestion> Parse(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty("questions", out var questions)
            || questions.ValueKind != JsonValueKind.Array || questions.GetArrayLength() == 0)
            throw new InvalidDataException("AskUserQuestion input has no questions.");
        return questions.EnumerateArray().Select(question =>
        {
            var text = ClaudeCliProtocol.String(question, "question");
            if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("AskUserQuestion question has no text.");
            var options = question.TryGetProperty("options", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Select(option => new ClaudeCliQuestionOption(
                        ClaudeCliProtocol.String(option, "label") is { Length: > 0 } label
                            ? label : throw new InvalidDataException("AskUserQuestion option has no label."),
                        ClaudeCliProtocol.String(option, "description"), ClaudeCliProtocol.String(option, "preview")))
                    .ToArray()
                : [];
            var multiSelect = question.TryGetProperty("multiSelect", out var multi) && multi.ValueKind == JsonValueKind.True;
            return new ClaudeCliQuestion(text, ClaudeCliProtocol.String(question, "header"), options, multiSelect);
        }).ToArray();
    }

    /// <summary>The updated tool input that carries the user's answers back to the CLI.</summary>
    public static JsonElement Answer(JsonElement input, IReadOnlyDictionary<string, string> answers)
    {
        var updated = JsonNode.Parse(input.GetRawText())!.AsObject();
        var values = new JsonObject();
        foreach (var (question, answer) in answers) values[question] = answer;
        updated["answers"] = values;
        return JsonSerializer.SerializeToElement(updated);
    }
}
