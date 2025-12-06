using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace Sowser.Controls
{
    /// <summary>
    /// A draggable browser card with WebView2 integration
    /// </summary>
    public partial class BrowserCard : UserControl
    {
        // Static command for fullscreen
        public static readonly RoutedCommand FullscreenCommand = new RoutedCommand();

        public string CardId { get; } = Guid.NewGuid().ToString();
        public string CurrentUrl { get; private set; } = string.Empty;
        public string CurrentTitle { get; private set; } = "New Tab";

        // Events for parent window communication
        public event EventHandler<string>? CloseRequested;
        public event EventHandler<LinkClickedEventArgs>? LinkClicked;
        public event EventHandler<string>? BookmarkRequested;
        public event EventHandler<HistoryEventArgs>? NavigationCompleted;
        public event EventHandler? CardMoved;
        public event EventHandler<DownloadEventArgs>? DownloadStarted;
        public event EventHandler<ConnectionPointEventArgs>? ConnectionPointPressed;
        public event EventHandler<ConnectionPointEventArgs>? ConnectionPointHoverChanged;
        public event EventHandler<string>? FullscreenRequested;

        // Drag state
        private bool _isDragging;
        private Point _dragStartPoint;
        private double _dragStartLeft;
        private double _dragStartTop;

        // Resize state
        private bool _isResizing;
        private Point _resizeStartPoint;
        private double _resizeStartWidth;
        private double _resizeStartHeight;
        private FrameworkElement? _activeResizeHandle;

        // WebView initialization state
        private bool _webViewInitialized;
        private string? _pendingUrl;

        // Connection overlay state
        private bool _isPointerOver;
        private bool _forceConnectionOverlay;

        // Event throttling for CardMoved
        private DispatcherTimer? _cardMovedThrottleTimer;
        private bool _needsCardMovedEvent;

        public BrowserCard()
        {
            InitializeComponent();
            Loaded += BrowserCard_Loaded;
            
            // Register fullscreen command
            CommandBindings.Add(new CommandBinding(FullscreenCommand, OnFullscreenCommand));
            
            // Setup throttle timer for CardMoved events (60 FPS)
            _cardMovedThrottleTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(16) // ~60 FPS
            };
            _cardMovedThrottleTimer.Tick += (s, e) =>
            {
                if (_needsCardMovedEvent)
                {
                    _needsCardMovedEvent = false;
                    CardMoved?.Invoke(this, EventArgs.Empty);
                }
            };
        }

        private void OnFullscreenCommand(object sender, ExecutedRoutedEventArgs e)
        {
            FullscreenRequested?.Invoke(this, CardId);
        }

        private void BrowserCard_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded -= BrowserCard_Loaded; // Only run once
            InitializeWebView();
        }

        private void Card_MouseEnter(object sender, MouseEventArgs e)
        {
            _isPointerOver = true;
            UpdateConnectionOverlayVisibility();
        }

        private void Card_MouseLeave(object sender, MouseEventArgs e)
        {
            _isPointerOver = false;
            UpdateConnectionOverlayVisibility();
        }

        public void SetConnectionMode(bool isActive)
        {
            _forceConnectionOverlay = isActive;
            UpdateConnectionOverlayVisibility();
        }

        private void UpdateConnectionOverlayVisibility()
        {
            if (ConnectionOverlay == null)
                return;

            ConnectionOverlay.Visibility = (_forceConnectionOverlay || _isPointerOver)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private async void InitializeWebView()
        {
            if (_webViewInitialized) return;
            
            try
            {
                await WebView.EnsureCoreWebView2Async();
                
                if (_webViewInitialized) return; // Double-check after await
                _webViewInitialized = true;
                
                // Handle navigation events
                WebView.CoreWebView2.NavigationStarting += CoreWebView2_NavigationStarting;
                WebView.CoreWebView2.NavigationCompleted += CoreWebView2_NavigationCompleted;
                WebView.CoreWebView2.DownloadStarting += CoreWebView2_DownloadStarting;
                WebView.CoreWebView2.DocumentTitleChanged += CoreWebView2_DocumentTitleChanged;
                WebView.CoreWebView2.NewWindowRequested += CoreWebView2_NewWindowRequested;
                
                // Navigate to pending URL if any
                if (!string.IsNullOrEmpty(_pendingUrl))
                {
                    Navigate(_pendingUrl);
                    _pendingUrl = null;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"WebView2 initialization failed: {ex.Message}", "Error", 
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Navigate to a URL
        /// </summary>
        public void Navigate(string url)
        {
            CurrentUrl = url;
            UrlTextBlock.Text = url;
            
            if (WebView.CoreWebView2 != null)
            {
                WebView.CoreWebView2.Navigate(url);
            }
            else
            {
                // Queue for after initialization
                _pendingUrl = url;
            }
        }

        /// <summary>
        /// Navigate with delayed WebView2 initialization
        /// </summary>
        public void NavigateDelayed(string url)
        {
            _pendingUrl = url;
            CurrentUrl = url;
            UrlTextBlock.Text = url;
            
            // InitializeWebView will handle navigation when ready
            if (!_webViewInitialized)
            {
                InitializeWebView();
            }
            else if (WebView.CoreWebView2 != null)
            {
                WebView.CoreWebView2.Navigate(url);
            }
        }

        #region WebView2 Events

        private bool _isInitialNavigation = true;

        private void CoreWebView2_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
        {
            // Skip link interception during initial page load
            if (_isInitialNavigation)
            {
                return;
            }
            
            // Only intercept user-initiated navigations that are actual link clicks
            // Not redirects (same domain) or form submissions
            if (e.IsUserInitiated && !string.IsNullOrEmpty(CurrentUrl))
            {
                // Check if it's a different page (not just a redirect on same domain)
                if (!IsSameDomainRedirect(CurrentUrl, e.Uri))
                {
                    // Cancel navigation and spawn new card
                    e.Cancel = true;
                    LinkClicked?.Invoke(this, new LinkClickedEventArgs(CardId, e.Uri));
                }
            }
        }

        private bool IsSameDomainRedirect(string currentUrl, string newUrl)
        {
            try
            {
                var current = new Uri(currentUrl);
                var next = new Uri(newUrl);
                
                // Same host = likely a redirect, not a link click
                string currentHost = current.Host.Replace("www.", "");
                string nextHost = next.Host.Replace("www.", "");
                
                return currentHost.Equals(nextHost, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private void CoreWebView2_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            _isInitialNavigation = false; // First navigation complete
            
            if (e.IsSuccess && WebView.CoreWebView2 != null)
            {
                CurrentUrl = WebView.CoreWebView2.Source;
                UrlTextBlock.Text = CurrentUrl;
                NavigationCompleted?.Invoke(this, new HistoryEventArgs(CurrentTitle, CurrentUrl));
            }
        }

        private void CoreWebView2_DocumentTitleChanged(object? sender, object e)
        {
            if (WebView.CoreWebView2 != null)
            {
                CurrentTitle = WebView.CoreWebView2.DocumentTitle;
            }
        }

        private void CoreWebView2_DownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
        {
            DownloadStarted?.Invoke(this, new DownloadEventArgs(
                e.DownloadOperation.Uri,
                e.ResultFilePath,
                (long)(e.DownloadOperation.TotalBytesToReceive ?? 0)
            ));
        }

        private void CoreWebView2_NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
        {
            // Prevent popup windows - instead spawn a new card
            e.Handled = true;
            LinkClicked?.Invoke(this, new LinkClickedEventArgs(CardId, e.Uri));
        }

        #endregion

        #region Title Bar Drag

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 1)
            {
                _isDragging = true;
                _dragStartPoint = e.GetPosition(Parent as Canvas);
                _dragStartLeft = Canvas.GetLeft(this);
                _dragStartTop = Canvas.GetTop(this);
                
                if (double.IsNaN(_dragStartLeft)) _dragStartLeft = 0;
                if (double.IsNaN(_dragStartTop)) _dragStartTop = 0;
                
                ((UIElement)sender).CaptureMouse();
                
                // Start throttle timer
                _cardMovedThrottleTimer?.Start();
            }
        }

        private void TitleBar_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isDragging && Parent is Canvas canvas)
            {
                Point currentPoint = e.GetPosition(canvas);
                double deltaX = currentPoint.X - _dragStartPoint.X;
                double deltaY = currentPoint.Y - _dragStartPoint.Y;

                double newLeft = Math.Max(0, _dragStartLeft + deltaX);
                double newTop = Math.Max(0, _dragStartTop + deltaY);

                Canvas.SetLeft(this, newLeft);
                Canvas.SetTop(this, newTop);
                
                // Set flag for throttled event
                _needsCardMovedEvent = true;
            }
        }

        private void TitleBar_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isDragging)
            {
                _isDragging = false;
                ((UIElement)sender).ReleaseMouseCapture();
                
                // Stop throttle timer and fire final event
                _cardMovedThrottleTimer?.Stop();
                if (_needsCardMovedEvent)
                {
                    _needsCardMovedEvent = false;
                    CardMoved?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        #endregion

        #region Resize Handles

        private double _resizeStartLeft;
        private double _resizeStartTop;

        private void ResizeHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isResizing = true;
            _activeResizeHandle = sender as FrameworkElement;
            _resizeStartPoint = e.GetPosition(Parent as Canvas);
            _resizeStartWidth = ActualWidth;
            _resizeStartHeight = ActualHeight;
            _resizeStartLeft = Canvas.GetLeft(this);
            _resizeStartTop = Canvas.GetTop(this);
            if (double.IsNaN(_resizeStartLeft)) _resizeStartLeft = 0;
            if (double.IsNaN(_resizeStartTop)) _resizeStartTop = 0;
            ((UIElement)sender).CaptureMouse();
            
            // Start throttle timer
            _cardMovedThrottleTimer?.Start();
        }

        private void ResizeHandle_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isResizing && Parent is Canvas canvas)
            {
                Point currentPoint = e.GetPosition(canvas);
                double deltaX = currentPoint.X - _resizeStartPoint.X;
                double deltaY = currentPoint.Y - _resizeStartPoint.Y;

                // Right edge resize (E, SE, NE)
                if (_activeResizeHandle == ResizeHandleE || 
                    _activeResizeHandle == ResizeHandleSE || 
                    _activeResizeHandle == ResizeHandleNE)
                {
                    Width = Math.Max(MinWidth, _resizeStartWidth + deltaX);
                }
                
                // Left edge resize (W, SW, NW) - also moves position
                if (_activeResizeHandle == ResizeHandleW || 
                    _activeResizeHandle == ResizeHandleSW || 
                    _activeResizeHandle == ResizeHandleNW)
                {
                    double newWidth = _resizeStartWidth - deltaX;
                    if (newWidth >= MinWidth)
                    {
                        Width = newWidth;
                        Canvas.SetLeft(this, _resizeStartLeft + deltaX);
                    }
                }
                
                // Bottom edge resize (S, SE, SW)
                if (_activeResizeHandle == ResizeHandleS || 
                    _activeResizeHandle == ResizeHandleSE || 
                    _activeResizeHandle == ResizeHandleSW)
                {
                    Height = Math.Max(MinHeight, _resizeStartHeight + deltaY);
                }
                
                // Top edge resize (N, NE, NW) - also moves position
                if (_activeResizeHandle == ResizeHandleN || 
                    _activeResizeHandle == ResizeHandleNE || 
                    _activeResizeHandle == ResizeHandleNW)
                {
                    double newHeight = _resizeStartHeight - deltaY;
                    if (newHeight >= MinHeight)
                    {
                        Height = newHeight;
                        Canvas.SetTop(this, _resizeStartTop + deltaY);
                    }
                }
                
                // Set flag for throttled event
                _needsCardMovedEvent = true;
            }
        }

        private void ResizeHandle_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isResizing)
            {
                _isResizing = false;
                ((UIElement)sender).ReleaseMouseCapture();
                
                // Stop throttle timer and fire final event
                _cardMovedThrottleTimer?.Stop();
                if (_needsCardMovedEvent)
                {
                    _needsCardMovedEvent = false;
                    CardMoved?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        #endregion

        #region Connection Points

        private void ConnectionPoint_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement element && element.Tag is string edge)
            {
                ConnectionPointPressed?.Invoke(this, new ConnectionPointEventArgs(CardId, edge, true));
                e.Handled = true;
            }
        }

        private void ConnectionPoint_MouseEnter(object sender, MouseEventArgs e)
        {
            if (sender is FrameworkElement element && element.Tag is string edge)
            {
                ConnectionPointHoverChanged?.Invoke(this, new ConnectionPointEventArgs(CardId, edge, true));
            }
        }

        private void ConnectionPoint_MouseLeave(object sender, MouseEventArgs e)
        {
            if (sender is FrameworkElement element && element.Tag is string edge)
            {
                ConnectionPointHoverChanged?.Invoke(this, new ConnectionPointEventArgs(CardId, edge, false));
            }
        }

        public Point GetEdgeCenter(string edge)
        {
            double left = Canvas.GetLeft(this);
            double top = Canvas.GetTop(this);
            if (double.IsNaN(left)) left = 0;
            if (double.IsNaN(top)) top = 0;

            // Account for the 12px margin around the visual card border
            const double margin = 12;
            double visualLeft = left + margin;
            double visualTop = top + margin;
            double visualWidth = ActualWidth - margin * 2;
            double visualHeight = ActualHeight - margin * 2;
            double centerX = visualLeft + visualWidth / 2;
            double centerY = visualTop + visualHeight / 2;

            return edge switch
            {
                "top" => new Point(centerX, visualTop),
                "bottom" => new Point(centerX, visualTop + visualHeight),
                "left" => new Point(visualLeft, centerY),
                "right" => new Point(visualLeft + visualWidth, centerY),
                _ => new Point(centerX, centerY)
            };
        }

        #endregion

        #region Button Handlers

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (WebView.CoreWebView2?.CanGoBack == true)
            {
                WebView.CoreWebView2.GoBack();
            }
        }

        private void ForwardButton_Click(object sender, RoutedEventArgs e)
        {
            if (WebView.CoreWebView2?.CanGoForward == true)
            {
                WebView.CoreWebView2.GoForward();
            }
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            WebView.CoreWebView2?.Reload();
        }

        private void BookmarkButton_Click(object sender, RoutedEventArgs e)
        {
            BookmarkRequested?.Invoke(this, CurrentUrl);
        }

        /// <summary>
        /// Update the bookmark icon to show filled or outline star
        /// </summary>
        public void SetBookmarked(bool isBookmarked)
        {
            BookmarkIcon.Kind = isBookmarked 
                ? MaterialDesignThemes.Wpf.PackIconKind.Star 
                : MaterialDesignThemes.Wpf.PackIconKind.StarOutline;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            // Dispose WebView2 resources
            WebView.Dispose();
            CloseRequested?.Invoke(this, CardId);
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Process URL input - detect if URL or search query
        /// </summary>
        public static string ProcessUrlInput(string input, string searchEngine = "https://www.google.com/search?q=")
        {
            if (string.IsNullOrWhiteSpace(input))
                return "about:blank";

            // Check if it's already a valid URL
            if (Uri.TryCreate(input, UriKind.Absolute, out Uri? uri) && 
                (uri.Scheme == "http" || uri.Scheme == "https"))
            {
                return input;
            }

            // Check if it looks like a domain (contains dot, no spaces)
            if (input.Contains('.') && !input.Contains(' '))
            {
                return "https://" + input;
            }

            // Treat as search query
            return searchEngine + Uri.EscapeDataString(input);
        }

        /// <summary>
        /// Get center point of this card on the canvas
        /// </summary>
        public Point GetCenterPoint()
        {
            double left = Canvas.GetLeft(this);
            double top = Canvas.GetTop(this);
            if (double.IsNaN(left)) left = 0;
            if (double.IsNaN(top)) top = 0;
            
            return new Point(left + ActualWidth / 2, top + ActualHeight / 2);
        }

        #endregion
    }

    #region Event Args

    public class ConnectionPointEventArgs : EventArgs
    {
        public string CardId { get; }
        public string Edge { get; }
        public bool IsHovered { get; }

        public ConnectionPointEventArgs(string cardId, string edge, bool isHovered)
        {
            CardId = cardId;
            Edge = edge;
            IsHovered = isHovered;
        }
    }

    public class LinkClickedEventArgs : EventArgs
    {
        public string SourceCardId { get; }
        public string Url { get; }

        public LinkClickedEventArgs(string sourceCardId, string url)
        {
            SourceCardId = sourceCardId;
            Url = url;
        }
    }

    public class HistoryEventArgs : EventArgs
    {
        public string Title { get; }
        public string Url { get; }

        public HistoryEventArgs(string title, string url)
        {
            Title = title;
            Url = url;
        }
    }

    public class DownloadEventArgs : EventArgs
    {
        public string Url { get; }
        public string FilePath { get; }
        public long TotalBytes { get; }

        public DownloadEventArgs(string url, string filePath, long totalBytes)
        {
            Url = url;
            FilePath = filePath;
            TotalBytes = totalBytes;
        }
    }

    #endregion
}
