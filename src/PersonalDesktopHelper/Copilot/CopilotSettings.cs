using System.IO;

namespace PersonalDesktopHelper.Copilot;

public sealed record CopilotSettings
{
    public string Model { get; init; } = "";

    public void Validate()
    {
        if (Model is null || Model.Any(char.IsControl))
        {
            throw new InvalidDataException("The Copilot model must be single-line text.");
        }
    }
}
