namespace FastSim;

/// <summary>Application entry point. UI, flows and HSMS share this WinForms process.</summary>
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        // Construct the current GUI. It binds the existing FlowRunner and Communication.
        var form = new SimulatorForm(
            mockMode: args.Contains("--mock") || args.Contains("--ui-smoke"),
            role: Argument(args, "--role") ?? "EQP",
            projectPath: Argument(args, "--project"));

        if (Argument(args, "--ui-smoke") is { } smokeDirectory)
            form.Shown += async (_, _) => await form.RunSmoke(smokeDirectory);
        if (Argument(args, "--automation-pipe") is { } pipe)
            form.Shown += (_, _) => form.StartAutomation(pipe);
        if (args.Contains("--open"))
            form.Shown += (_, _) => form.OpenOnStart();

        Application.Run(form);
    }

    private static string? Argument(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
