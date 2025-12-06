namespace Sowser.Models
{
    /// <summary>
    /// Application settings
    /// </summary>
    public class AppSettings
    {
        public string DefaultSearchEngine { get; set; } = "https://www.google.com/search?q=";
        public string Theme { get; set; } = "Light";
        public string CanvasBackground { get; set; } = "#F5F5F5";
        public bool AutoSaveEnabled { get; set; } = true;
        public int AutoSaveIntervalSeconds { get; set; } = 30;
    }
}
