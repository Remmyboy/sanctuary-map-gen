using System.Text.Json;

namespace SanctuaryMapConverter.Core
{
    // What the window remembers between runs.
    //
    // Auto-detection covers the common installs, but someone with the game
    // somewhere unusual - a second drive, a portable FAF copy, a network
    // share - should have to point at it once, not once per session. Saved
    // beside the user's other app data rather than next to the exe, so it
    // survives replacing the exe and works from a read-only folder.
    public sealed class UserSettings
    {
        public string FaInstall { get; set; }
        public string SanctuaryInstall { get; set; }
        public string MapsFolder { get; set; }
        public string ExportFolder { get; set; }

        static string PathFor()
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SanctuaryMapConverter");
            return Path.Combine(dir, "settings.json");
        }

        public static UserSettings Load()
        {
            try
            {
                string p = PathFor();
                if (File.Exists(p))
                    return JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(p)) ?? new UserSettings();
            }
            catch { }
            return new UserSettings();
        }

        /// Best effort: a settings file that cannot be written is not worth
        /// interrupting the user over.
        public void Save()
        {
            try
            {
                string p = PathFor();
                Directory.CreateDirectory(Path.GetDirectoryName(p));
                File.WriteAllText(p, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }
    }
}
