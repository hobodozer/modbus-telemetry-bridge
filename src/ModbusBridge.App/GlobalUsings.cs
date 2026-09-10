// This project enables both WPF and WinForms (the latter purely for the tray NotifyIcon), so a
// handful of type names exist in both worlds. These aliases pin them to the WPF meaning, which is
// what everything except TrayIcon.cs wants; TrayIcon deliberately uses the System.Drawing types
// that are not aliased here (Bitmap, Graphics, Icon, Pen, SolidBrush, Color).

// The shared, UI-framework-free view models. They live in their own net8.0 library so the
// Avalonia app can use the same ones; nothing in them may reference WPF.
global using ModbusBridge.ViewModels;

global using Application = System.Windows.Application;
global using MessageBox = System.Windows.MessageBox;
global using MessageBoxButton = System.Windows.MessageBoxButton;
global using MessageBoxImage = System.Windows.MessageBoxImage;
global using MessageBoxResult = System.Windows.MessageBoxResult;
global using Brush = System.Windows.Media.Brush;
global using Brushes = System.Windows.Media.Brushes;
global using DataGrid = System.Windows.Controls.DataGrid;
global using Label = System.Windows.Controls.Label;
global using TabControl = System.Windows.Controls.TabControl;
global using TabItem = System.Windows.Controls.TabItem;
global using Binding = System.Windows.Data.Binding;
