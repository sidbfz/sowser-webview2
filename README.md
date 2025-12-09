# Sowser

A spatial web browser prototype that uses an infinite canvas instead of traditional tabs.

![.NET](https://img.shields.io/badge/.NET-8.0-512BD4?style=flat-square&logo=dotnet)
![WPF](https://img.shields.io/badge/WPF-Desktop-blue?style=flat-square)
![WebView2](https://img.shields.io/badge/WebView2-Chromium-green?style=flat-square)

## Demo

https://github.com/user-attachments/assets/d74108a1-f5dc-4cb9-b901-3904ac0fa59a

## What is Sowser?

Sowser reimagines web browsing as a visual workspace. Instead of cramming dozens of tabs into a tiny bar, web pages appear as draggable cards on an infinite canvas that you can organize spatially.

## Features

- **Draggable Cards** - Each web page is a resizable card you can move anywhere
- **Visual Connections** - Draw arrows between related pages to show relationships
- **Infinite Canvas** - Pan and zoom across your browsing space
- **Spatial Organization** - Arrange content visually instead of linearly
- **Workspace Saving** - Save and load your entire browsing layout
- **Quick Links** - Fast access to popular sites (YouTube, GitHub, ChatGPT, etc.)
- **Bookmarks & History** - Built-in sidebar for managing saved pages
- **Smooth Scrolling** - Trackpad-friendly navigation with smooth animations

## Tech Stack

- **C# / .NET 8.0** - Core application framework
- **WPF** - Windows Presentation Foundation for the UI
- **WebView2** - Microsoft's Chromium-based web rendering engine
- **Material Design in XAML** - Modern UI components and styling

## Getting Started

### Prerequisites

- Windows 10/11
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [WebView2 Runtime](https://developer.microsoft.com/en-us/microsoft-edge/webview2/) (usually pre-installed on Windows 10/11)

### Build & Run

```bash
git clone https://github.com/sidbfz/sowser-webview2.git
cd sowser-webview2
dotnet build
dotnet run
```

## Usage

- **New Card** - Click the globe icon or use the search bar
- **Move Cards** - Drag the title bar
- **Resize Cards** - Drag the edges or corners
- **Connect Cards** - Hover over a card, click a connection point, drag to another card
- **Pan Canvas** - Middle-click and drag, or use trackpad gestures
- **Zoom** - Ctrl + scroll wheel
- **Fullscreen Card** - Double-click the title bar

## Limitations

This is a prototype built with WebView2. Due to WebView2's nature, some browser features can't be implemented directly without building a complete browser engine. This prototype validates the spatial browsing concept.

## Full Version

Interested in a complete spatial browser with all the features? Join the waitlist:

👉 **https://sowser-waitlist.vercel.app/**

Goal: 500 signups to greenlight the full build with tab groups, collaboration features, cross-platform support, and more.

## License

MIT License - feel free to use, modify, and distribute.

## Author

Built by [@sidbfz](https://github.com/sidbfz)
