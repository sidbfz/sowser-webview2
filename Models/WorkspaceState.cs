using System.Collections.Generic;

namespace Sowser.Models
{
    /// <summary>
    /// Complete workspace state for serialization
    /// </summary>
    public class WorkspaceState
    {
        public List<CardState> Cards { get; set; } = new();
        public List<Connection> Connections { get; set; } = new();
        public List<Bookmark> Bookmarks { get; set; } = new();
        public double ViewportX { get; set; }
        public double ViewportY { get; set; }
        public double ZoomLevel { get; set; } = 1.0;
    }
}
