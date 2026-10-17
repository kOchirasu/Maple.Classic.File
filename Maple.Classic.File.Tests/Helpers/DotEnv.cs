namespace Maple.Classic.File.Tests.Helpers;

public static class DotEnv {
    public static readonly string SolutionDir =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));

    public static void Load(bool required = false) {
        string dotenv = Path.Combine(SolutionDir, ".env");

        if (!System.IO.File.Exists(dotenv)) {
            if (required) {
                throw new FileNotFoundException(".env file not found!", dotenv);
            }
            return;
        }

        foreach (string line in System.IO.File.ReadAllLines(dotenv)) {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) {
                continue;
            }

            string[] parts = line.Split('=', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2) {
                continue;
            }

            Environment.SetEnvironmentVariable(parts[0], parts[1]);
        }
    }
}
