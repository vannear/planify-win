using Microsoft.UI.Xaml;
namespace Planify.App;
public partial class App : Application
{
    private Window? window;
    private Mutex? instance;
    public App()
    {
        UnhandledException += (_, args) => Log(args.Exception);
        InitializeComponent();
    }
    private static void Log(Exception error)
    {
        string directory = Environment.GetEnvironmentVariable("PLANIFY_DATA_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PlanifyWindowsCommunity");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "startup-error.txt"), error.ToString());
        foreach (System.Collections.DictionaryEntry item in error.Data)
            File.AppendAllText(Path.Combine(directory, "startup-error.txt"), $"\n{item.Key}: {item.Value}");
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        string? testDirectory = Environment.GetEnvironmentVariable("PLANIFY_DATA_DIR");
        string suffix = testDirectory == null ? "" : "-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(testDirectory))));
        instance = new Mutex(true, "Local\\PlanifyWindowsCommunity" + suffix, out bool first);
        if (!first) { Exit(); return; }
        try { window = new MainWindow(); window.Activate(); }
        catch (Exception error) { Log(error); throw; }
    }
}
