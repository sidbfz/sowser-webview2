using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using Sowser.Controls;
using Sowser.Models;

namespace Sowser
{
    /// <summary>
    /// Sowser - A spatial web browser with draggable cards on an infinite canvas
    /// </summary>
    public partial class MainWindow : Window
    {
        // Data collections
        private readonly Dictionary<string, BrowserCard> _cards = new();
        private readonly List<Connection> _connections = new();
        private readonly Dictionary<string, System.Windows.Shapes.Path> _connectionLines = new();
        private readonly List<Bookmark> _bookmarks = new();
        private readonly List<HistoryEntry> _history = new();
        private readonly List<DownloadItem> _downloads = new();

        // Settings
        private AppSettings _settings = new();
        private readonly string[] _searchEngines = {
            "https://www.google.com/search?q=",
            "https://www.bing.com/search?q=",
            "https://duckduckgo.com/?q="
        };

        // Pan state
        private bool _isPanning;
        private Point _panStartPoint;
        private double _panStartOffsetX;
        private double _panStartOffsetY;

        // Zoom
        private double _zoomLevel = 1.0;
        private const double ZoomMin = 0.5;
        private const double ZoomMax = 1.0;
        private const double ZoomStep = 0.05; // Smoother zoom

        // Auto-save timer
        private DispatcherTimer? _autoSaveTimer;

        private bool _isInitialized;
        private bool _isConnectionDrag;
        private string? _connectionStartCardId;
        private string? _connectionStartEdge;
        private System.Windows.Shapes.Path? _connectionPreviewPath;
        private string? _connectionPreviewTargetEdge;
        private string? _hoveredConnectionCardId;
        private string? _hoveredConnectionEdge;

        // Smooth scroll state
        private double _smoothScrollTargetY;
        private bool _isSmoothScrollActive;

        public MainWindow()
        {
            InitializeComponent();
            InitializeAutoSave();
            
            // Center viewport initially and set up horizontal scroll hook
            Loaded += (s, e) => 
            {
                CenterViewport();
                _isInitialized = true;
                SetupHorizontalScrollHook();
            };

            // Handle ESC to close expanded card view
            PreviewKeyDown += MainWindow_PreviewKeyDown;
        }

        private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape && ExpandedCardOverlay.Visibility == Visibility.Visible)
            {
                CloseExpandedCard();
                e.Handled = true;
            }
        }

        #region Horizontal Scroll Support (Trackpad)

        private const int WM_MOUSEHWHEEL = 0x020E;

        private void SetupHorizontalScrollHook()
        {
            var source = PresentationSource.FromVisual(this) as HwndSource;
            source?.AddHook(WndProc);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_MOUSEHWHEEL)
            {
                // Extract the scroll delta (high-order word of wParam)
                int delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
                
                // Scroll horizontally - delta is positive for right, negative for left
                double newOffset = CanvasScrollViewer.HorizontalOffset + delta;
                CanvasScrollViewer.ScrollToHorizontalOffset(Math.Max(0, newOffset));
                
                handled = true;
            }
            return IntPtr.Zero;
        }

        #endregion

        #region Initialization

        private void InitializeAutoSave()
        {
            _autoSaveTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(_settings.AutoSaveIntervalSeconds)
            };
            _autoSaveTimer.Tick += (s, e) => AutoSaveWorkspace();
            if (_settings.AutoSaveEnabled)
            {
                _autoSaveTimer.Start();
            }
        }

        private void CenterViewport()
        {
            // Center on canvas middle
            double centerX = (CardsCanvas.Width / 2) - (CanvasScrollViewer.ActualWidth / 2);
            double centerY = (CardsCanvas.Height / 2) - (CanvasScrollViewer.ActualHeight / 2);
            CanvasScrollViewer.ScrollToHorizontalOffset(Math.Max(0, centerX));
            CanvasScrollViewer.ScrollToVerticalOffset(Math.Max(0, centerY));
        }

        /// <summary>
        /// Update canvas size to fit all cards with padding, ensuring scrollable area
        /// </summary>
        private void UpdateCanvasSize()
        {
            const double padding = 800; // Extra space around cards for scrolling

            // Minimum size should be at least viewport size + padding for scrolling
            double viewportWidth = CanvasScrollViewer.ActualWidth > 0 ? CanvasScrollViewer.ActualWidth : 1200;
            double viewportHeight = CanvasScrollViewer.ActualHeight > 0 ? CanvasScrollViewer.ActualHeight : 800;
            double minWidth = viewportWidth + padding;
            double minHeight = viewportHeight + padding;

            if (_cards.Count == 0)
            {
                CardsCanvas.Width = minWidth;
                CardsCanvas.Height = minHeight;
                ConnectionsCanvas.Width = minWidth;
                ConnectionsCanvas.Height = minHeight;
                return;
            }

            double maxRight = 0;
            double maxBottom = 0;

            foreach (var card in _cards.Values)
            {
                double left = Canvas.GetLeft(card);
                double top = Canvas.GetTop(card);
                if (double.IsNaN(left)) left = 0;
                if (double.IsNaN(top)) top = 0;

                maxRight = Math.Max(maxRight, left + card.ActualWidth + padding);
                maxBottom = Math.Max(maxBottom, top + card.ActualHeight + padding);
            }

            double newWidth = Math.Max(minWidth, maxRight);
            double newHeight = Math.Max(minHeight, maxBottom);

            CardsCanvas.Width = newWidth;
            CardsCanvas.Height = newHeight;
            ConnectionsCanvas.Width = newWidth;
            ConnectionsCanvas.Height = newHeight;
        }

        #endregion

        #region Card Management

        /// <summary>
        /// Create a new browser card at specified position
        /// </summary>
        private BrowserCard CreateCard(double x, double y, string? url = null)
        {
            var card = new BrowserCard();
            
            // Position on canvas
            Canvas.SetLeft(card, x);
            Canvas.SetTop(card, y);
            
            // Wire up events
            card.CloseRequested += Card_CloseRequested;
            card.LinkClicked += Card_LinkClicked;
            card.BookmarkRequested += Card_BookmarkRequested;
            card.NavigationCompleted += Card_NavigationCompleted;
            card.CardMoved += Card_CardMoved;
            card.DownloadStarted += Card_DownloadStarted;
            card.ConnectionPointPressed += Card_ConnectionPointPressed;
            card.ConnectionPointHoverChanged += Card_ConnectionPointHoverChanged;
            card.FullscreenRequested += Card_FullscreenRequested;
            
            // Add to canvas and tracking
            CardsCanvas.Children.Add(card);
            _cards[card.CardId] = card;
            
            // Navigate if URL provided
            if (!string.IsNullOrEmpty(url))
            {
                card.NavigateDelayed(url);
            }

            // Update canvas size to fit new card
            Dispatcher.BeginInvoke(new Action(UpdateCanvasSize), DispatcherPriority.Loaded);
            
            return card;
        }

        /// <summary>
        /// Get viewport center position on canvas
        /// </summary>
        private Point GetViewportCenter()
        {
            double x = (CanvasScrollViewer.HorizontalOffset + CanvasScrollViewer.ActualWidth / 2) / _zoomLevel;
            double y = (CanvasScrollViewer.VerticalOffset + CanvasScrollViewer.ActualHeight / 2) / _zoomLevel;
            return new Point(x, y);
        }

        private void Card_CloseRequested(object? sender, string cardId)
        {
            if (_cards.TryGetValue(cardId, out var card))
            {
                card.ConnectionPointPressed -= Card_ConnectionPointPressed;
                card.ConnectionPointHoverChanged -= Card_ConnectionPointHoverChanged;

                if (_isConnectionDrag && (cardId == _connectionStartCardId || cardId == _hoveredConnectionCardId))
                {
                    CancelConnectionDrag();
                }

                // Remove associated connections
                var connectionsToRemove = _connections
                    .Where(c => c.FromCardId == cardId || c.ToCardId == cardId)
                    .ToList();
                
                foreach (var conn in connectionsToRemove)
                {
                    _connections.Remove(conn);
                    if (_connectionLines.TryGetValue(conn.Id, out var path))
                    {
                        ConnectionsCanvas.Children.Remove(path);
                        _connectionLines.Remove(conn.Id);
                    }
                    // Remove arrow
                    var arrow = ConnectionsCanvas.Children.OfType<Polygon>()
                        .FirstOrDefault(p => p.Tag?.ToString() == conn.Id + "_arrow");
                    if (arrow != null)
                        ConnectionsCanvas.Children.Remove(arrow);
                }
                
                // Remove card
                CardsCanvas.Children.Remove(card);
                _cards.Remove(cardId);
            }
        }

        private void Card_LinkClicked(object? sender, LinkClickedEventArgs e)
        {
            if (_cards.TryGetValue(e.SourceCardId, out var sourceCard))
            {
                // Calculate new card position (to the right of source card)
                double sourceX = Canvas.GetLeft(sourceCard);
                double sourceY = Canvas.GetTop(sourceCard);
                double cardWidth = sourceCard.ActualWidth > 0 ? sourceCard.ActualWidth : 700;
                double newX = sourceX + cardWidth + 50; // Place to the right with 50px gap
                double newY = sourceY + 200; // Same vertical position
                
                // Create new cardwh
                var newCard = CreateCard(newX, newY, e.Url);
                
                // Create connection
                var connection = new Connection
                {
                    FromCardId = e.SourceCardId,
                    ToCardId = newCard.CardId,
                    Url = e.Url,
                    Timestamp = DateTime.Now
                };
                _connections.Add(connection);
                
                // Draw connection line (delayed to ensure card is rendered)
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    DrawConnectionLine(connection, sourceCard, newCard);
                }), DispatcherPriority.Loaded);
            }
        }

        private void Card_BookmarkRequested(object? sender, string url)
        {
            if (sender is BrowserCard card && !string.IsNullOrEmpty(url))
            {
                // Check if already bookmarked
                var existingBookmark = _bookmarks.FirstOrDefault(b => b.Url == url);
                if (existingBookmark != null)
                {
                    // Remove bookmark (toggle off)
                    _bookmarks.Remove(existingBookmark);
                    card.SetBookmarked(false);
                }
                else
                {
                    // Add new bookmark
                    var bookmark = new Bookmark
                    {
                        Title = card.CurrentTitle,
                        Url = url
                    };
                    _bookmarks.Add(bookmark);
                    card.SetBookmarked(true);
                }
                RefreshBookmarksList();
            }
        }

        private void Card_FullscreenRequested(object? sender, string cardId)
        {
            if (_cards.TryGetValue(cardId, out var card))
            {
                OpenCardExpanded(card);
            }
        }

        private string? _expandedCardId;

        private async void OpenCardExpanded(BrowserCard card)
        {
            _expandedCardId = card.CardId;
            ExpandedCardUrl.Text = card.CurrentUrl;
            ExpandedCardOverlay.Visibility = Visibility.Visible;

            // Initialize WebView2 and navigate
            await ExpandedWebView.EnsureCoreWebView2Async();
            ExpandedWebView.CoreWebView2.Navigate(card.CurrentUrl);

            // Focus for keyboard input
            ExpandedWebView.Focus();
        }

        private void CloseExpandedCard_Click(object sender, RoutedEventArgs e)
        {
            CloseExpandedCard();
        }

        private void CloseExpandedCard()
        {
            ExpandedCardOverlay.Visibility = Visibility.Collapsed;
            
            // Sync URL back to card if it changed
            if (!string.IsNullOrEmpty(_expandedCardId) && 
                _cards.TryGetValue(_expandedCardId, out var card) &&
                ExpandedWebView.CoreWebView2 != null)
            {
                string currentUrl = ExpandedWebView.CoreWebView2.Source;
                if (currentUrl != card.CurrentUrl)
                {
                    card.Navigate(currentUrl);
                }
            }
            
            _expandedCardId = null;
        }

        private void Card_NavigationCompleted(object? sender, HistoryEventArgs e)
        {
            var entry = new HistoryEntry
            {
                Title = e.Title,
                Url = e.Url
            };
            _history.Insert(0, entry); // Most recent first
            RefreshHistoryList();
        }

        private void Card_CardMoved(object? sender, EventArgs e)
        {
            // Update all connection lines
            UpdateAllConnectionLines();
            // Update canvas size if card moved near edge
            UpdateCanvasSize();
        }

        private void Card_DownloadStarted(object? sender, DownloadEventArgs e)
        {
            var download = new DownloadItem
            {
                FileName = System.IO.Path.GetFileName(e.FilePath),
                Url = e.Url,
                FilePath = e.FilePath,
                TotalBytes = e.TotalBytes
            };
            _downloads.Insert(0, download);
            RefreshDownloadsList();
        }

        private void Card_ConnectionPointPressed(object? sender, ConnectionPointEventArgs e)
        {
            if (sender is not BrowserCard card)
                return;

            if (_isConnectionDrag)
            {
                CancelConnectionDrag();
            }

            BeginConnectionDrag(card, e.Edge);
        }

        private void Card_ConnectionPointHoverChanged(object? sender, ConnectionPointEventArgs e)
        {
            if (!_isConnectionDrag)
                return;

            if (e.IsHovered)
            {
                _hoveredConnectionCardId = e.CardId;
                _hoveredConnectionEdge = e.Edge;
            }
            else if (_hoveredConnectionCardId == e.CardId && _hoveredConnectionEdge == e.Edge)
            {
                _hoveredConnectionCardId = null;
                _hoveredConnectionEdge = null;
            }
        }

        #endregion

        #region Connection Lines

        private void BeginConnectionDrag(BrowserCard card, string edge)
        {
            _isConnectionDrag = true;
            _connectionStartCardId = card.CardId;
            _connectionStartEdge = edge;
            _hoveredConnectionCardId = null;
            _hoveredConnectionEdge = null;
            _connectionPreviewTargetEdge = null;

            foreach (var item in _cards.Values)
            {
                item.SetConnectionMode(true);
            }

            _connectionPreviewPath = new System.Windows.Shapes.Path
            {
                Stroke = new SolidColorBrush(Color.FromRgb(0, 217, 255)),
                StrokeThickness = 2,
                StrokeDashArray = new DoubleCollection { 4, 2 },
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                IsHitTestVisible = false
            };

            ConnectionsCanvas.Children.Add(_connectionPreviewPath);
        }

        private void CompleteConnectionDrag()
        {
            if (!_isConnectionDrag)
                return;

            if (!string.IsNullOrEmpty(_connectionStartCardId) &&
                !string.IsNullOrEmpty(_hoveredConnectionCardId) &&
                _connectionStartCardId != _hoveredConnectionCardId &&
                _cards.TryGetValue(_connectionStartCardId, out var fromCard) &&
                _cards.TryGetValue(_hoveredConnectionCardId, out var toCard))
            {
                var connection = new Connection
                {
                    FromCardId = _connectionStartCardId,
                    ToCardId = _hoveredConnectionCardId,
                    Url = string.Empty,
                    Timestamp = DateTime.Now,
                    FromEdge = _connectionStartEdge ?? string.Empty,
                    ToEdge = _hoveredConnectionEdge ?? string.Empty,
                    IsManual = true
                };
                _connections.Add(connection);
                DrawConnectionLine(connection, fromCard, toCard);
            }

            CancelConnectionDrag();
        }

        private void CancelConnectionDrag()
        {
            if (_connectionPreviewPath != null)
            {
                ConnectionsCanvas.Children.Remove(_connectionPreviewPath);
                _connectionPreviewPath = null;
            }

            _isConnectionDrag = false;
            _connectionStartCardId = null;
            _connectionStartEdge = null;
            _connectionPreviewTargetEdge = null;
            _hoveredConnectionCardId = null;
            _hoveredConnectionEdge = null;

            foreach (var card in _cards.Values)
            {
                card.SetConnectionMode(false);
            }
        }

        private (Point from, Point to, string fromEdge, string toEdge) ResolveConnectionPoints(Connection connection, BrowserCard fromCard, BrowserCard toCard)
        {
            // Use stored edges when available
            if (!string.IsNullOrEmpty(connection.FromEdge) && !string.IsNullOrEmpty(connection.ToEdge))
            {
                return (fromCard.GetEdgeCenter(connection.FromEdge),
                        toCard.GetEdgeCenter(connection.ToEdge),
                        connection.FromEdge,
                        connection.ToEdge);
            }

            // Otherwise compute best edges based on relative card positions and persist them
            var (fromPoint, toPoint, fromEdge, toEdge) = GetEdgeConnectionPoints(fromCard, toCard);
            connection.FromEdge = fromEdge;
            connection.ToEdge = toEdge;
            return (fromPoint, toPoint, fromEdge, toEdge);
        }

        /// <summary>
        /// Get the best edge points for connecting two cards based on their positions
        /// </summary>
        private (Point from, Point to, string fromEdge, string toEdge) GetEdgeConnectionPoints(BrowserCard fromCard, BrowserCard toCard)
        {
            // Account for the 12px margin around the visual card border
            const double margin = 12;

            // Get card bounds (visual bounds inside the margin)
            double fromLeft = Canvas.GetLeft(fromCard) + margin;
            double fromTop = Canvas.GetTop(fromCard) + margin;
            double fromRight = fromLeft + fromCard.ActualWidth - margin * 2;
            double fromBottom = fromTop + fromCard.ActualHeight - margin * 2;
            double fromCenterX = (fromLeft + fromRight) / 2;
            double fromCenterY = (fromTop + fromBottom) / 2;

            double toLeft = Canvas.GetLeft(toCard) + margin;
            double toTop = Canvas.GetTop(toCard) + margin;
            double toRight = toLeft + toCard.ActualWidth - margin * 2;
            double toBottom = toTop + toCard.ActualHeight - margin * 2;
            double toCenterX = (toLeft + toRight) / 2;
            double toCenterY = (toTop + toBottom) / 2;

            // Determine direction from source to target
            double dx = toCenterX - fromCenterX;
            double dy = toCenterY - fromCenterY;

            Point fromPoint, toPoint;
            string fromEdge, toEdge;

            // Choose edges based on relative position
            if (Math.Abs(dx) > Math.Abs(dy))
            {
                // Horizontal connection
                if (dx > 0)
                {
                    // Target is to the right
                    fromPoint = new Point(fromRight, fromCenterY);
                    toPoint = new Point(toLeft, toCenterY);
                    fromEdge = "right";
                    toEdge = "left";
                }
                else
                {
                    // Target is to the left
                    fromPoint = new Point(fromLeft, fromCenterY);
                    toPoint = new Point(toRight, toCenterY);
                    fromEdge = "left";
                    toEdge = "right";
                }
            }
            else
            {
                // Vertical connection
                if (dy > 0)
                {
                    // Target is below
                    fromPoint = new Point(fromCenterX, fromBottom);
                    toPoint = new Point(toCenterX, toTop);
                    fromEdge = "bottom";
                    toEdge = "top";
                }
                else
                {
                    // Target is above
                    fromPoint = new Point(fromCenterX, fromTop);
                    toPoint = new Point(toCenterX, toBottom);
                    fromEdge = "top";
                    toEdge = "bottom";
                }
            }

            return (fromPoint, toPoint, fromEdge, toEdge);
        }

        /// <summary>
        /// Get the best edge for the target point based on direction from the start point.
        /// </summary>
        private string GetBestEdgeForPoint(Point from, Point to)
        {
            double dx = to.X - from.X;
            double dy = to.Y - from.Y;

            if (Math.Abs(dx) > Math.Abs(dy))
            {
                return dx > 0 ? "left" : "right";
            }
            else
            {
                return dy > 0 ? "top" : "bottom";
            }
        }

        /// <summary>
        /// Choose edge with a deadband to avoid rapid flipping; keeps current edge until movement is decisive.
        /// </summary>
        private string GetBestEdgeForPointWithHysteresis(Point from, Point to, string? currentEdge, double deadband = 60)
        {
            double dx = to.X - from.X;
            double dy = to.Y - from.Y;

            string candidate = GetBestEdgeForPoint(from, to);
            if (string.IsNullOrEmpty(currentEdge) || currentEdge == candidate)
            {
                return candidate;
            }

            bool preferHorizontal = Math.Abs(dx) > Math.Abs(dy) + deadband;
            bool preferVertical = Math.Abs(dy) > Math.Abs(dx) + deadband;

            return candidate switch
            {
                "left" or "right" => preferHorizontal ? candidate : currentEdge,
                "top" or "bottom" => preferVertical ? candidate : currentEdge,
                _ => candidate
            };
        }

        /// <summary>
        /// Draw a bezier curve connection between two cards with arrow
        /// </summary>
        private void DrawConnectionLine(Connection connection, BrowserCard fromCard, BrowserCard toCard)
        {
            var (fromPoint, toPoint, fromEdge, toEdge) = ResolveConnectionPoints(connection, fromCard, toCard);

            // Create bezier curve path
            var path = new System.Windows.Shapes.Path
            {
                Stroke = new SolidColorBrush(Color.FromRgb(0, 217, 255)),
                StrokeThickness = 2,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Fill = Brushes.Transparent
            };

            UpdateBezierPath(path, fromPoint, toPoint, fromEdge, toEdge);

            ConnectionsCanvas.Children.Add(path);
            _connectionLines[connection.Id] = path;

            // Add arrow head at target
            AddArrowHead(connection.Id, toPoint, fromEdge, toEdge);
        }

        /// <summary>
        /// Update bezier path geometry - modifies existing geometry instead of recreating
        /// </summary>
        private void UpdateBezierPath(System.Windows.Shapes.Path path, Point from, Point to, string fromEdge, string toEdge)
        {
            // Calculate control points for smooth bezier curve
            double distance = Math.Sqrt(Math.Pow(to.X - from.X, 2) + Math.Pow(to.Y - from.Y, 2));
            double controlOffset = Math.Min(distance * 0.4, 150); // Control point offset

            Point control1, control2;

            // Set control points based on edge directions
            switch (fromEdge)
            {
                case "right":
                    control1 = new Point(from.X + controlOffset, from.Y);
                    break;
                case "left":
                    control1 = new Point(from.X - controlOffset, from.Y);
                    break;
                case "bottom":
                    control1 = new Point(from.X, from.Y + controlOffset);
                    break;
                case "top":
                    control1 = new Point(from.X, from.Y - controlOffset);
                    break;
                default:
                    control1 = from;
                    break;
            }

            switch (toEdge)
            {
                case "right":
                    control2 = new Point(to.X + controlOffset, to.Y);
                    break;
                case "left":
                    control2 = new Point(to.X - controlOffset, to.Y);
                    break;
                case "bottom":
                    control2 = new Point(to.X, to.Y + controlOffset);
                    break;
                case "top":
                    control2 = new Point(to.X, to.Y - controlOffset);
                    break;
                default:
                    control2 = to;
                    break;
            }

            // Try to update existing geometry instead of recreating
            if (path.Data is PathGeometry pathGeometry &&
                pathGeometry.Figures.Count > 0 &&
                pathGeometry.Figures[0] is PathFigure pathFigure &&
                pathFigure.Segments.Count > 0 &&
                pathFigure.Segments[0] is BezierSegment bezierSegment)
            {
                // Update existing geometry points
                pathFigure.StartPoint = from;
                bezierSegment.Point1 = control1;
                bezierSegment.Point2 = control2;
                bezierSegment.Point3 = to;
            }
            else
            {
                // Create new geometry if structure doesn't exist
                pathGeometry = new PathGeometry();
                pathFigure = new PathFigure { StartPoint = from };
                bezierSegment = new BezierSegment(control1, control2, to, true);
                pathFigure.Segments.Add(bezierSegment);
                pathGeometry.Figures.Add(pathFigure);
                path.Data = pathGeometry;
            }
        }

        /// <summary>
        /// Add arrow head at the target point
        /// </summary>
        private void AddArrowHead(string connectionId, Point targetPoint, string fromEdge, string toEdge)
        {
            double arrowSize = 14;
            double angle;

            // Calculate arrow angle based on incoming direction
            switch (toEdge)
            {
                case "left":
                    angle = 0; // Arrow pointing right (into left edge)
                    break;
                case "right":
                    angle = 180; // Arrow pointing left (into right edge)
                    break;
                case "top":
                    angle = 90; // Arrow pointing down (into top edge)
                    break;
                case "bottom":
                    angle = -90; // Arrow pointing up (into bottom edge)
                    break;
                default:
                    angle = 0;
                    break;
            }

            // Create arrow polygon
            var arrow = new Polygon
            {
                Fill = new SolidColorBrush(Color.FromRgb(0, 217, 255)),
                Points = new PointCollection
                {
                    new Point(arrowSize, 0),
                    new Point(-arrowSize * 0.6, -arrowSize * 0.5),
                    new Point(-arrowSize * 0.6, arrowSize * 0.5)
                },
                Tag = connectionId + "_arrow"
            };

            // Position and rotate arrow
            arrow.RenderTransform = new TransformGroup
            {
                Children = new TransformCollection
                {
                    new RotateTransform(angle),
                    new TranslateTransform(targetPoint.X, targetPoint.Y)
                }
            };

            ConnectionsCanvas.Children.Add(arrow);
        }

        private void UpdateAllConnectionLines()
        {
            foreach (var connection in _connections)
            {
                if (_connectionLines.TryGetValue(connection.Id, out var path) &&
                    _cards.TryGetValue(connection.FromCardId, out var fromCard) &&
                    _cards.TryGetValue(connection.ToCardId, out var toCard))
                {
                    var (fromPoint, toPoint, fromEdge, toEdge) = ResolveConnectionPoints(connection, fromCard, toCard);
                    UpdateBezierPath(path, fromPoint, toPoint, fromEdge, toEdge);

                    // Update arrow position using transform (don't remove/recreate)
                    var arrowTag = connection.Id + "_arrow";
                    var arrow = ConnectionsCanvas.Children.OfType<Polygon>()
                        .FirstOrDefault(p => p.Tag?.ToString() == arrowTag);

                    if (arrow != null)
                    {
                        UpdateArrowTransform(arrow, toPoint, toEdge);
                    }
                }
            }
        }
        
        /// <summary>
        /// Update arrow transform without recreating it
        /// </summary>
        private void UpdateArrowTransform(Polygon arrow, Point targetPoint, string toEdge)
        {
            double angle = toEdge switch
            {
                "left" => 0,
                "right" => 180,
                "top" => 90,
                "bottom" => -90,
                _ => 0
            };

            // Update existing transform
            if (arrow.RenderTransform is TransformGroup group && 
                group.Children.Count >= 2 &&
                group.Children[0] is RotateTransform rotate &&
                group.Children[1] is TranslateTransform translate)
            {
                rotate.Angle = angle;
                translate.X = targetPoint.X;
                translate.Y = targetPoint.Y;
            }
            else
            {
                // Fallback: create new transform if structure is unexpected
                arrow.RenderTransform = new TransformGroup
                {
                    Children = new TransformCollection
                    {
                        new RotateTransform(angle),
                        new TranslateTransform(targetPoint.X, targetPoint.Y)
                    }
                };
            }
        }

        #endregion

        #region Pan and Zoom

        private void CanvasScrollViewer_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            // Middle mouse button for panning
            if (e.MiddleButton == MouseButtonState.Pressed)
            {
                _isPanning = true;
                _panStartPoint = e.GetPosition(CanvasScrollViewer);
                _panStartOffsetX = CanvasScrollViewer.HorizontalOffset;
                _panStartOffsetY = CanvasScrollViewer.VerticalOffset;
                CanvasScrollViewer.CaptureMouse();
                CanvasScrollViewer.Cursor = Cursors.ScrollAll;
                e.Handled = true;
            }
        }

        private void CanvasScrollViewer_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_isPanning)
            {
                Point currentPoint = e.GetPosition(CanvasScrollViewer);
                double deltaX = currentPoint.X - _panStartPoint.X;
                double deltaY = currentPoint.Y - _panStartPoint.Y;
                
                CanvasScrollViewer.ScrollToHorizontalOffset(_panStartOffsetX - deltaX);
                CanvasScrollViewer.ScrollToVerticalOffset(_panStartOffsetY - deltaY);
                e.Handled = true;
            }

            if (_isConnectionDrag && _connectionPreviewPath != null && !string.IsNullOrEmpty(_connectionStartCardId) && _cards.TryGetValue(_connectionStartCardId, out var dragCard))
            {
                // Get position relative to CardsCanvas to match the card coordinates (both use same scale transform)
                Point canvasPoint = e.GetPosition(CardsCanvas);
                Point startPoint = dragCard.GetEdgeCenter(_connectionStartEdge ?? "right");
                string dynamicEdge = GetBestEdgeForPointWithHysteresis(startPoint, canvasPoint, _connectionPreviewTargetEdge);
                _connectionPreviewTargetEdge = dynamicEdge;
                UpdateBezierPath(_connectionPreviewPath, startPoint, canvasPoint, _connectionStartEdge ?? "right", dynamicEdge);
            }
        }

        private void CanvasScrollViewer_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_isPanning && e.MiddleButton == MouseButtonState.Released)
            {
                _isPanning = false;
                CanvasScrollViewer.ReleaseMouseCapture();
                CanvasScrollViewer.Cursor = Cursors.Arrow;
                e.Handled = true;
            }

            if (_isConnectionDrag && e.ChangedButton == MouseButton.Left)
            {
                CompleteConnectionDrag();
                e.Handled = true;
            }
        }

        private void CanvasScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                // Get mouse position relative to the ScrollViewer viewport
                Point mouseViewport = e.GetPosition(CanvasScrollViewer);
                
                // Calculate the mouse position in unscaled canvas coordinates
                double mouseCanvasX = (CanvasScrollViewer.HorizontalOffset + mouseViewport.X) / _zoomLevel;
                double mouseCanvasY = (CanvasScrollViewer.VerticalOffset + mouseViewport.Y) / _zoomLevel;
                
                // Store old zoom level
                double oldZoom = _zoomLevel;
                
                // Zoom with Ctrl+Scroll
                double zoomFactor = e.Delta > 0 ? 1.08 : 0.92;
                _zoomLevel = Math.Clamp(_zoomLevel * zoomFactor, ZoomMin, ZoomMax);
                
                // Apply the new zoom
                CanvasScale.ScaleX = _zoomLevel;
                CanvasScale.ScaleY = _zoomLevel;
                CardsCanvasScale.ScaleX = _zoomLevel;
                CardsCanvasScale.ScaleY = _zoomLevel;
                ZoomIndicator.Text = $"{(int)(_zoomLevel * 100)}%";
                
                // Calculate new scroll offset to keep mouse point stationary
                // The mouse canvas point should remain at the same viewport position
                double newOffsetX = mouseCanvasX * _zoomLevel - mouseViewport.X;
                double newOffsetY = mouseCanvasY * _zoomLevel - mouseViewport.Y;
                
                CanvasScrollViewer.ScrollToHorizontalOffset(Math.Max(0, newOffsetX));
                CanvasScrollViewer.ScrollToVerticalOffset(Math.Max(0, newOffsetY));
                
                e.Handled = true;
            }
            else
            {
                // Smooth vertical scrolling (trackpad-friendly)
                double target = CanvasScrollViewer.VerticalOffset - e.Delta;
                StartSmoothVerticalScroll(target);
                e.Handled = true;
            }
        }

        private void ApplyZoom()
        {
            CanvasScale.ScaleX = _zoomLevel;
            CanvasScale.ScaleY = _zoomLevel;
            CardsCanvasScale.ScaleX = _zoomLevel;
            CardsCanvasScale.ScaleY = _zoomLevel;
            ZoomIndicator.Text = $"{(int)(_zoomLevel * 100)}%";
        }

        private void ZoomIn_Click(object sender, RoutedEventArgs e)
        {
            ZoomTowardCenter(Math.Min(ZoomMax, _zoomLevel + 0.1));
        }

        private void ZoomOut_Click(object sender, RoutedEventArgs e)
        {
            ZoomTowardCenter(Math.Max(ZoomMin, _zoomLevel - 0.1));
        }

        private void ZoomTowardCenter(double newZoom)
        {
            // Get viewport center in content coordinates
            double viewportCenterX = (CanvasScrollViewer.HorizontalOffset + CanvasScrollViewer.ActualWidth / 2) / _zoomLevel;
            double viewportCenterY = (CanvasScrollViewer.VerticalOffset + CanvasScrollViewer.ActualHeight / 2) / _zoomLevel;
            
            double oldZoom = _zoomLevel;
            _zoomLevel = newZoom;
            
            // Apply the new zoom
            CanvasScale.ScaleX = _zoomLevel;
            CanvasScale.ScaleY = _zoomLevel;
            CardsCanvasScale.ScaleX = _zoomLevel;
            CardsCanvasScale.ScaleY = _zoomLevel;
            ZoomIndicator.Text = $"{(int)(_zoomLevel * 100)}%";
            
            // Adjust scroll to keep the viewport center stationary
            double newOffsetX = viewportCenterX * _zoomLevel - CanvasScrollViewer.ActualWidth / 2;
            double newOffsetY = viewportCenterY * _zoomLevel - CanvasScrollViewer.ActualHeight / 2;
            
            CanvasScrollViewer.ScrollToHorizontalOffset(Math.Max(0, newOffsetX));
            CanvasScrollViewer.ScrollToVerticalOffset(Math.Max(0, newOffsetY));
        }

        private void ZoomIndicator_Click(object sender, MouseButtonEventArgs e)
        {
            // Reset to 100%
            _zoomLevel = 1.0;
            ApplyZoom();
        }

        private void StartSmoothVerticalScroll(double targetY)
        {
            // Clamp to scrollable range
            double clamped = Math.Clamp(targetY, 0, CanvasScrollViewer.ScrollableHeight);
            _smoothScrollTargetY = clamped;
            if (_isSmoothScrollActive)
                return;

            _isSmoothScrollActive = true;
            CompositionTarget.Rendering += SmoothVerticalScrollStep;
        }

        private void SmoothVerticalScrollStep(object? sender, EventArgs e)
        {
            double current = CanvasScrollViewer.VerticalOffset;
            double next = current + (_smoothScrollTargetY - current) * 0.25; // easing factor

            // Snap when close enough to avoid jitter
            if (Math.Abs(next - _smoothScrollTargetY) < 0.5)
            {
                CanvasScrollViewer.ScrollToVerticalOffset(_smoothScrollTargetY);
                CompositionTarget.Rendering -= SmoothVerticalScrollStep;
                _isSmoothScrollActive = false;
                return;
            }

            CanvasScrollViewer.ScrollToVerticalOffset(next);
        }

        private void ResetView_Click(object sender, RoutedEventArgs e)
        {
            _zoomLevel = 1.0;
            ApplyZoom();
            CenterViewport();
        }

        private void FitAllCards_Click(object sender, RoutedEventArgs e)
        {
            if (_cards.Count == 0) return;

            // Find bounding box of all cards
            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;

            foreach (var card in _cards.Values)
            {
                double left = Canvas.GetLeft(card);
                double top = Canvas.GetTop(card);
                if (double.IsNaN(left)) left = 0;
                if (double.IsNaN(top)) top = 0;

                minX = Math.Min(minX, left);
                minY = Math.Min(minY, top);
                maxX = Math.Max(maxX, left + card.ActualWidth);
                maxY = Math.Max(maxY, top + card.ActualHeight);
            }

            // Add padding
            double padding = 50;
            minX -= padding;
            minY -= padding;
            maxX += padding;
            maxY += padding;

            // Calculate required zoom to fit
            double contentWidth = maxX - minX;
            double contentHeight = maxY - minY;
            double viewWidth = CanvasScrollViewer.ActualWidth;
            double viewHeight = CanvasScrollViewer.ActualHeight;

            double zoomX = viewWidth / contentWidth;
            double zoomY = viewHeight / contentHeight;
            _zoomLevel = Math.Clamp(Math.Min(zoomX, zoomY), ZoomMin, ZoomMax);

            ApplyZoom();

            // Scroll to center of cards
            double centerX = (minX + maxX) / 2 * _zoomLevel - viewWidth / 2;
            double centerY = (minY + maxY) / 2 * _zoomLevel - viewHeight / 2;
            CanvasScrollViewer.ScrollToHorizontalOffset(Math.Max(0, centerX));
            CanvasScrollViewer.ScrollToVerticalOffset(Math.Max(0, centerY));
        }

        #endregion

        #region Toolbar Handlers

        private void NewCard_Click(object sender, RoutedEventArgs e)
        {
            Point center = GetViewportCenter();
            CreateCard(center.X - 400, center.Y - 300, "https://www.google.com");
        }

        private void MainUrlBar_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                string input = MainUrlBar.Text?.Trim() ?? "";
                if (!string.IsNullOrEmpty(input))
                {
                    string url = BrowserCard.ProcessUrlInput(input, _settings.DefaultSearchEngine);
                    Point center = GetViewportCenter();
                    CreateCard(center.X - 400, center.Y - 300, url);
                    MainUrlBar.Text = "";
                }
            }
        }

        private void QuickLink_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string url)
            {
                Point center = GetViewportCenter();
                CreateCard(center.X - 400, center.Y - 300, url);
            }
        }

        private void QuickLinkBorder_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border border && border.Tag is string url)
            {
                Point center = GetViewportCenter();
                CreateCard(center.X - 400, center.Y - 300, url);
            }
        }

        #endregion

        #region Sidebar Management

        private void ShowSidebar(string panel)
        {
            SidebarPanel.Visibility = Visibility.Visible;
            BookmarksPanel.Visibility = Visibility.Collapsed;
            HistoryPanel.Visibility = Visibility.Collapsed;
            DownloadsPanel.Visibility = Visibility.Collapsed;
            SettingsPanel.Visibility = Visibility.Collapsed;
            
            switch (panel)
            {
                case "bookmarks":
                    SidebarTitle.Text = "☆ Bookmarks";
                    BookmarksPanel.Visibility = Visibility.Visible;
                    RefreshBookmarksList();
                    break;
                case "history":
                    SidebarTitle.Text = "📜 History";
                    HistoryPanel.Visibility = Visibility.Visible;
                    RefreshHistoryList();
                    break;
                case "downloads":
                    SidebarTitle.Text = "⬇ Downloads";
                    DownloadsPanel.Visibility = Visibility.Visible;
                    RefreshDownloadsList();
                    break;
                case "settings":
                    SidebarTitle.Text = "⚙ Settings";
                    SettingsPanel.Visibility = Visibility.Visible;
                    break;
            }
        }

        private void ToggleBookmarks_Click(object sender, RoutedEventArgs e) => ShowSidebar("bookmarks");
        private void ToggleHistory_Click(object sender, RoutedEventArgs e) => ShowSidebar("history");
        private void ToggleDownloads_Click(object sender, RoutedEventArgs e) => ShowSidebar("downloads");
        private void ToggleSettings_Click(object sender, RoutedEventArgs e) => ShowSidebar("settings");
        private void CloseSidebar_Click(object sender, RoutedEventArgs e) => SidebarPanel.Visibility = Visibility.Collapsed;

        private void RefreshBookmarksList()
        {
            BookmarksList.Children.Clear();
            foreach (var bookmark in _bookmarks)
            {
                var btn = new Button
                {
                    Content = $"☆ {bookmark.Title}\n{bookmark.Url}",
                    Style = (Style)FindResource("SidebarButtonStyle"),
                    Tag = bookmark.Url,
                    Margin = new Thickness(0, 0, 0, 5)
                };
                btn.Click += (s, e) =>
                {
                    Point center = GetViewportCenter();
                    CreateCard(center.X - 400, center.Y - 300, bookmark.Url);
                };
                BookmarksList.Children.Add(btn);
            }
        }

        private void RefreshHistoryList(string? filter = null)
        {
            HistoryList.Children.Clear();
            var items = string.IsNullOrEmpty(filter) 
                ? _history 
                : _history.Where(h => h.Title.Contains(filter, StringComparison.OrdinalIgnoreCase) || 
                                      h.Url.Contains(filter, StringComparison.OrdinalIgnoreCase));
            
            foreach (var entry in items.Take(50))
            {
                var btn = new Button
                {
                    Content = $"{entry.Title}\n{entry.Url}\n{entry.VisitedAt:g}",
                    Style = (Style)FindResource("SidebarButtonStyle"),
                    Tag = entry.Url,
                    Margin = new Thickness(0, 0, 0, 5),
                    FontSize = 11
                };
                btn.Click += (s, e) =>
                {
                    Point center = GetViewportCenter();
                    CreateCard(center.X - 400, center.Y - 300, entry.Url);
                };
                HistoryList.Children.Add(btn);
            }
        }

        private void RefreshDownloadsList()
        {
            DownloadsList.Children.Clear();
            foreach (var download in _downloads)
            {
                var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
                panel.Children.Add(new TextBlock 
                { 
                    Text = download.FileName, 
                    FontWeight = FontWeights.SemiBold 
                });
                panel.Children.Add(new TextBlock 
                { 
                    Text = $"{download.State} - {download.TotalBytes / 1024} KB",
                    Foreground = new SolidColorBrush(Colors.Gray),
                    FontSize = 11
                });
                DownloadsList.Children.Add(panel);
            }
        }

        private void HistorySearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            RefreshHistoryList(HistorySearchBox.Text);
        }

        #endregion

        #region Settings Handlers

        private void SearchEngineCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isInitialized) return;
            if (SearchEngineCombo.SelectedIndex >= 0 && SearchEngineCombo.SelectedIndex < _searchEngines.Length)
            {
                _settings.DefaultSearchEngine = _searchEngines[SearchEngineCombo.SelectedIndex];
            }
        }

        private void AutoSaveCheckbox_Changed(object sender, RoutedEventArgs e)
        {
            if (!_isInitialized) return;
            _settings.AutoSaveEnabled = AutoSaveToggle.IsChecked == true;
            if (_settings.AutoSaveEnabled)
                _autoSaveTimer?.Start();
            else
                _autoSaveTimer?.Stop();
        }

        #endregion

        #region Workspace Save/Load

        private void AutoSaveWorkspace()
        {
            try
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string sowserDir = System.IO.Path.Combine(appData, "Sowser");
                Directory.CreateDirectory(sowserDir);
                string autoSavePath = System.IO.Path.Combine(sowserDir, "autosave.json");
                SaveWorkspaceToFile(autoSavePath);
            }
            catch { /* Silent fail for auto-save */ }
        }

        private void SaveWorkspace_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog
            {
                Filter = "Sowser Workspace (*.sowser)|*.sowser|JSON Files (*.json)|*.json",
                DefaultExt = ".sowser",
                FileName = "workspace"
            };
            
            if (dialog.ShowDialog() == true)
            {
                SaveWorkspaceToFile(dialog.FileName);
                System.Windows.MessageBox.Show("Workspace saved!", "Sowser", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void SaveWorkspaceToFile(string filePath)
        {
            var state = new WorkspaceState
            {
                ViewportX = CanvasScrollViewer.HorizontalOffset,
                ViewportY = CanvasScrollViewer.VerticalOffset,
                ZoomLevel = _zoomLevel,
                Connections = _connections.ToList(),
                Bookmarks = _bookmarks.ToList()
            };
            
            // Save card states
            foreach (var kvp in _cards)
            {
                var card = kvp.Value;
                state.Cards.Add(new CardState
                {
                    Id = card.CardId,
                    X = Canvas.GetLeft(card),
                    Y = Canvas.GetTop(card),
                    Width = card.ActualWidth,
                    Height = card.ActualHeight,
                    Url = card.CurrentUrl,
                    Title = card.CurrentTitle
                });
            }
            
            string json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(filePath, json);
        }

        private void LoadWorkspace_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "Sowser Workspace (*.sowser)|*.sowser|JSON Files (*.json)|*.json|All Files (*.*)|*.*",
                DefaultExt = ".sowser"
            };
            
            if (dialog.ShowDialog() == true)
            {
                LoadWorkspaceFromFile(dialog.FileName);
            }
        }

        private void LoadWorkspaceFromFile(string filePath)
        {
            try
            {
                string json = File.ReadAllText(filePath);
                var state = JsonSerializer.Deserialize<WorkspaceState>(json);
                
                if (state == null) return;
                
                // Clear existing
                foreach (var card in _cards.Values.ToList())
                {
                    card.CloseRequested -= Card_CloseRequested;
                    CardsCanvas.Children.Remove(card);
                }
                _cards.Clear();
                _connections.Clear();
                ConnectionsCanvas.Children.Clear();
                _connectionLines.Clear();
                
                // Restore bookmarks
                _bookmarks.Clear();
                _bookmarks.AddRange(state.Bookmarks);
                
                // Create cards
                var cardIdMap = new Dictionary<string, string>(); // old ID -> new ID
                foreach (var cardState in state.Cards)
                {
                    var card = CreateCard(cardState.X, cardState.Y, cardState.Url);
                    card.Width = cardState.Width;
                    card.Height = cardState.Height;
                    cardIdMap[cardState.Id] = card.CardId;
                }
                
                // Restore connections with updated IDs
                foreach (var conn in state.Connections)
                {
                    if (cardIdMap.TryGetValue(conn.FromCardId, out var newFromId) &&
                        cardIdMap.TryGetValue(conn.ToCardId, out var newToId))
                    {
                        var newConn = new Connection
                        {
                            FromCardId = newFromId,
                            ToCardId = newToId,
                            Url = conn.Url,
                            Timestamp = conn.Timestamp,
                            FromEdge = conn.FromEdge,
                            ToEdge = conn.ToEdge,
                            IsManual = conn.IsManual
                        };
                        _connections.Add(newConn);
                        
                        if (_cards.TryGetValue(newFromId, out var fromCard) &&
                            _cards.TryGetValue(newToId, out var toCard))
                        {
                            // Delay line drawing until cards are rendered
                            var capturedConn = newConn;
                            var capturedFrom = fromCard;
                            var capturedTo = toCard;
                            Dispatcher.BeginInvoke(new Action(() =>
                            {
                                DrawConnectionLine(capturedConn, capturedFrom, capturedTo);
                            }), DispatcherPriority.Loaded);
                        }
                    }
                }
                
                // Restore viewport
                _zoomLevel = state.ZoomLevel;
                CanvasScale.ScaleX = _zoomLevel;
                CanvasScale.ScaleY = _zoomLevel;
                CardsCanvasScale.ScaleX = _zoomLevel;
                CardsCanvasScale.ScaleY = _zoomLevel;
                ZoomIndicator.Text = $"{(int)(_zoomLevel * 100)}%";
                
                CanvasScrollViewer.ScrollToHorizontalOffset(state.ViewportX);
                CanvasScrollViewer.ScrollToVerticalOffset(state.ViewportY);
                
                System.Windows.MessageBox.Show("Workspace loaded!", "Sowser", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Failed to load workspace: {ex.Message}", "Error", 
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion
    }
}
