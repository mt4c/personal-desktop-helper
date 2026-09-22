namespace PersonalDesktopHelper.Copilot;

public sealed class CopilotException(string message, Exception? innerException = null)
    : Exception(message, innerException);
