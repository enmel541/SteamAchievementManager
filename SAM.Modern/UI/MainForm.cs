using System.Diagnostics;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using SamModern.Core;
using SamModern.Web;

namespace SamModern.UI
{
    /// <summary>
    /// The only native window of the app. It contains nothing but a WebView2 control that shows the web UI served by
    /// <see cref="ApiHost"/> on 127.0.0.1. All application logic lives in the host and the web UI; this class only
    /// creates the browser control, locks it down and keeps navigation inside the app.
    /// </summary>
    internal sealed class MainForm : Form
    {
        private static readonly Color Background = Color.FromArgb(13, 15, 20);

        private readonly ApiHost _host;
        private readonly WebView2 _web = new();

        public MainForm(ApiHost host)
        {
            _host = host;

            Text = "Steam Achievement Manager";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1380, 880);
            MinimumSize = new Size(980, 640);
            BackColor = Background;
            Icon = AppIcon.Create();

            _web.Dock = DockStyle.Fill;
            _web.DefaultBackgroundColor = Background;
            Controls.Add(_web);

            Load += async (_, _) => await InitAsync();
        }

        private async Task InitAsync()
        {
            try
            {
                // Keep the browser profile (cache, cookies) inside the app's own data folder, not next to the exe.
                var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(AppPaths.Root, "webview2"));
                await _web.EnsureCoreWebView2Async(env);

                // The window should feel like an app, not like a browser: no context menu, status bar or zoom,
                // and no F5/Ctrl+F/... accelerator keys unless a debugger is attached or --devtools was given.
                var core = _web.CoreWebView2;
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.IsZoomControlEnabled = false;
                core.Settings.AreBrowserAcceleratorKeysEnabled = Debugger.IsAttached || Environment.GetCommandLineArgs().Contains("--devtools");

                // Open external links in the default browser, never in the app window
                core.NewWindowRequested += (_, e) =>
                {
                    e.Handled = true;
                    OpenExternal(e.Uri);
                };
                core.NavigationStarting += (_, e) =>
                {
                    if (e.Uri.StartsWith(_host.Url, StringComparison.Ordinal) == false)
                    {
                        e.Cancel = true;
                        OpenExternal(e.Uri);
                    }
                };

                core.Navigate(_host.UrlWithToken);
            }
            catch (WebView2RuntimeNotFoundException)
            {
                MessageBox.Show(
                    "The Microsoft WebView2 runtime was not found. The interface will be opened in your default browser instead.\n\n" +
                    "This window must stay open for that.",
                    "Steam Achievement Manager", MessageBoxButtons.OK, MessageBoxIcon.Information);
                OpenExternal(_host.UrlWithToken);
            }
        }

        /// <summary>Opens a link in the default browser. Only http(s) is allowed (no file:, ms-settings:, ... schemes).</summary>
        private static void OpenExternal(string uri)
        {
            if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps))
            {
                try { Process.Start(new ProcessStartInfo(parsed.AbsoluteUri) { UseShellExecute = true }); } catch (Exception) { }
            }
        }
    }

    /// <summary>Draws the window icon at runtime (no image file needed).</summary>
    internal static class AppIcon
    {
        public static Icon Create()
        {
            using var bmp = new Bitmap(64, 64);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using var bg = new System.Drawing.Drawing2D.LinearGradientBrush(new Rectangle(0, 0, 64, 64), Color.FromArgb(124, 92, 255), Color.FromArgb(56, 189, 248), 45f);
                using var path = RoundedRect(new Rectangle(2, 2, 60, 60), 16);
                g.FillPath(bg, path);

                // Trophy
                using var white = new SolidBrush(Color.White);
                g.FillPolygon(white, new[] { new Point(20, 14), new Point(44, 14), new Point(42, 30), new Point(36, 37), new Point(28, 37), new Point(22, 30) });
                g.FillRectangle(white, 29, 36, 6, 8);
                g.FillRectangle(white, 22, 44, 20, 5);
                using var pen = new Pen(Color.White, 3f);
                g.DrawArc(pen, 11, 16, 12, 12, 90, 180);
                g.DrawArc(pen, 41, 16, 12, 12, -90, 180);
            }
            return Icon.FromHandle(bmp.GetHicon());
        }

        private static System.Drawing.Drawing2D.GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            var path = new System.Drawing.Drawing2D.GraphicsPath();
            int d = radius * 2;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
