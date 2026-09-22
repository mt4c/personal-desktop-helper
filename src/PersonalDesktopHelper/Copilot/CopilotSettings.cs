using System.IO;

namespace PersonalDesktopHelper.Copilot;

public sealed record CopilotSettings
{
    public string Model { get; init; } = "";
    public string AdditionalSystemPrompt { get; init; } = "";

    public void Validate()
    {
        if (Model is null || Model.Any(char.IsControl))
        {
            throw new InvalidDataException("The Copilot model must be single-line text.");
        }

        if (AdditionalSystemPrompt is null || AdditionalSystemPrompt.Contains('\0'))
        {
            throw new InvalidDataException("The editable system prompt must be text without null characters.");
        }
    }
}
