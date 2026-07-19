namespace SynthiaCode.Infrastructure;

public static class SystemPaths
{
    public static string AppDataDirectory
    {
        get
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localAppData))
            {
                localAppData = AppContext.BaseDirectory;
            }

            return Path.Combine(localAppData, "SynthiaCode");
        }
    }
}
