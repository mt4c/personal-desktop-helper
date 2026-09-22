namespace PersonalDesktopHelper.Copilot;

public interface ICopilotCredentialStore
{
    string? Load();
    void Save(string token);
    void Clear();
}
