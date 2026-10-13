namespace PersonalDesktopHelper.Copilot;

public interface ICopilotCredentialStore
{
    bool HasCredential { get; }
    string? Load();
    void Save(string token);
    void Clear();
}
