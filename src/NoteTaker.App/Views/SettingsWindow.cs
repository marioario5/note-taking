using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using NoteTaker.AI;
using NoteTaker.AI.Transport;

namespace NoteTaker.App.Views;

public sealed class SettingsWindow : Window
{
    private readonly AppHost _host;

    private readonly ComboBox _visionProvider = new();
    private readonly TextBox _visionModel = new();
    private readonly PasswordBox _visionKey = new();
    private readonly TextBox _visionEndpoint = new();
    private readonly ComboBox _chatProvider = new();
    private readonly TextBox _chatModel = new();
    private readonly PasswordBox _chatKey = new();
    private readonly TextBox _chatEndpoint = new();
    private readonly ComboBox _chatImageDetail = new();

    private readonly TextBox _debounce = new();
    private readonly TextBox _minInterval = new();
    private readonly TextBox _callsPerHour = new();
    private readonly TextBox _dailyCap = new();
    private readonly TextBox _revealAfter = new();
    private readonly TextBox _clipPath = new();
    private readonly TextBox _pythonPath = new();

    public SettingsWindow(AppHost host)
    {
        _host = host;

        Title = "Settings";
        Width = 560;
        Height = 660;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        if (TryFindResource("ElleryWindowStyle") is Style windowStyle)
        {
            Style = windowStyle;
        }

        foreach (var kind in Enum.GetValues<LlmProviderKind>())
        {
            _visionProvider.Items.Add(kind);
            _chatProvider.Items.Add(kind);
        }

        var settings = host.Settings;
        _visionProvider.SelectedItem = settings.VisionProvider;
        _visionModel.Text = settings.VisionModel;
        _chatProvider.SelectedItem = settings.ChatProvider;
        _chatModel.Text = settings.ChatModel;
        _visionEndpoint.Text = settings.VisionEndpoint ?? string.Empty;
        _chatEndpoint.Text = settings.ChatEndpoint ?? string.Empty;

        _chatImageDetail.ItemsSource = Enum.GetValues<ImageDetail>();
        _chatImageDetail.SelectedItem = settings.ChatImageDetail;

        _debounce.Text = settings.LiveDebounceSeconds.ToString(CultureInfo.InvariantCulture);
        _minInterval.Text = settings.MinLiveIntervalSeconds.ToString(CultureInfo.InvariantCulture);
        _callsPerHour.Text = settings.MaxLiveCallsPerHour.ToString(CultureInfo.InvariantCulture);
        _dailyCap.Text = settings.MonthlyCostCapUsd.ToString(CultureInfo.InvariantCulture);
        _revealAfter.Text = settings.RevealAfterTurns.ToString(CultureInfo.InvariantCulture);
        _clipPath.Text = settings.ClipModelPath ?? string.Empty;
        _pythonPath.Text = settings.PythonPath ?? "python";

        LoadExistingKeys();

        var panel = new StackPanel { Margin = new Thickness(16) };

        panel.Children.Add(Header("Tutor models"));
        panel.Children.Add(Row("Vision provider (checks work)", _visionProvider));
        panel.Children.Add(Row("Vision model", _visionModel));
        panel.Children.Add(Row("Vision API key", _visionKey));
        panel.Children.Add(Row("Vision endpoint (blank = provider default)", _visionEndpoint));
        panel.Children.Add(Row("Chat provider (explanations)", _chatProvider));
        panel.Children.Add(Row("Chat model", _chatModel));
        panel.Children.Add(Row("Chat API key", _chatKey));
        panel.Children.Add(Row("Chat endpoint (blank = provider default)", _chatEndpoint));
        panel.Children.Add(Row("Page detail sent to chat", _chatImageDetail));
        panel.Children.Add(Note("Keys are stored in Windows Credential Manager, never in the notes database. " +
            "Endpoints are for a proxy or self-hosted gateway — leave blank unless you have one."));
        panel.Children.Add(Note("Page detail is the biggest cost lever: the image is most of what a question " +
            "costs, and Gemini prices it per level, not per pixel. Low tested 18/18 on reading handwritten " +
            "limits, exponents and signs — raise it to Medium if your writing is ever misread."));

        panel.Children.Add(Header("Cost guardrails"));
        panel.Children.Add(Row("Live debounce (seconds)", _debounce));
        panel.Children.Add(Row("Minimum gap between checks (seconds)", _minInterval));
        panel.Children.Add(Row("Max live checks per hour", _callsPerHour));
        panel.Children.Add(Row("Daily budget (USD)", _dailyCap));
        panel.Children.Add(Row("Reveal answer after N questions", _revealAfter));

        panel.Children.Add(Header("Advanced"));
        panel.Children.Add(RowWithBrowse("CLIP image encoder (.onnx, optional)", _clipPath));
        panel.Children.Add(Row("Python executable", _pythonPath));
        panel.Children.Add(Note($"Page similarity currently uses: {host.EmbeddingModelName}"));

        var save = new Button { Content = "Save", Width = 90, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", Width = 90, IsCancel = true };
        save.Click += (_, _) => Save();

        if (TryFindResource("PrimaryButton") is Style primary)
        {
            save.Style = primary;
            save.Width = double.NaN;
            save.MinWidth = 90;
        }

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);

        var footer = new Border { Child = buttons };
        if (TryFindResource("DialogFooter") is Style footerStyle)
        {
            footer.Style = footerStyle;
        }

        var root = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(new ScrollViewer
        {
            Content = panel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        });

        Content = root;
    }

    private void LoadExistingKeys()
    {
        var visionKey = _host.Secrets.Get(LlmSettings.SecretKey(_host.Settings.VisionProvider));
        var chatKey = _host.Secrets.Get(LlmSettings.SecretKey(_host.Settings.ChatProvider));

        // Show a placeholder rather than the secret itself.
        if (!string.IsNullOrEmpty(visionKey))
        {
            _visionKey.Password = new string('•', 12);
        }

        if (!string.IsNullOrEmpty(chatKey))
        {
            _chatKey.Password = new string('•', 12);
        }
    }

    private static UIElement Header(string text) => new TextBlock
    {
        Text = text.ToUpperInvariant(),
        FontWeight = FontWeights.SemiBold,
        FontSize = 11,
        Opacity = 0.6,
        Margin = new Thickness(0, 14, 0, 6),
    };

    private static UIElement Note(string text) => new TextBlock
    {
        Text = text,
        FontSize = 11,
        Opacity = 0.65,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 4, 0, 0),
    };

    private static UIElement Row(string label, FrameworkElement editor)
    {
        editor.Margin = new Thickness(0, 2, 0, 8);

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = label, FontSize = 12 });
        panel.Children.Add(editor);
        return panel;
    }

    private UIElement RowWithBrowse(string label, TextBox editor)
    {
        var browse = new Button { Content = "Browse…", Width = 90, Margin = new Thickness(8, 2, 0, 8) };
        browse.Click += (_, _) =>
        {
            var dialog = new OpenFileDialog { Filter = "ONNX model (*.onnx)|*.onnx" };
            if (dialog.ShowDialog(this) == true)
            {
                editor.Text = dialog.FileName;
            }
        };

        editor.Margin = new Thickness(0, 2, 0, 8);

        var row = new DockPanel();
        DockPanel.SetDock(browse, Dock.Right);
        row.Children.Add(browse);
        row.Children.Add(editor);

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = label, FontSize = 12 });
        panel.Children.Add(row);
        return panel;
    }

    private void Save()
    {
        var settings = _host.Settings;

        settings.VisionProvider = (LlmProviderKind)(_visionProvider.SelectedItem ?? LlmProviderKind.Gemini);
        settings.VisionModel = _visionModel.Text.Trim();
        settings.ChatProvider = (LlmProviderKind)(_chatProvider.SelectedItem ?? LlmProviderKind.OpenAiCompatible);
        settings.ChatModel = _chatModel.Text.Trim();
        settings.VisionEndpoint = string.IsNullOrWhiteSpace(_visionEndpoint.Text) ? null : _visionEndpoint.Text.Trim();
        settings.ChatEndpoint = string.IsNullOrWhiteSpace(_chatEndpoint.Text) ? null : _chatEndpoint.Text.Trim();
        settings.ChatImageDetail = (ImageDetail)(_chatImageDetail.SelectedItem ?? ImageDetail.Low);

        settings.LiveDebounceSeconds = ParseDouble(_debounce.Text, settings.LiveDebounceSeconds);
        settings.MinLiveIntervalSeconds = ParseDouble(_minInterval.Text, settings.MinLiveIntervalSeconds);
        settings.MaxLiveCallsPerHour = (int)ParseDouble(_callsPerHour.Text, settings.MaxLiveCallsPerHour);
        settings.MonthlyCostCapUsd = (decimal)ParseDouble(_dailyCap.Text, (double)settings.MonthlyCostCapUsd);
        settings.RevealAfterTurns = (int)ParseDouble(_revealAfter.Text, settings.RevealAfterTurns);
        settings.ClipModelPath = string.IsNullOrWhiteSpace(_clipPath.Text) ? null : _clipPath.Text.Trim();
        settings.PythonPath = string.IsNullOrWhiteSpace(_pythonPath.Text) ? null : _pythonPath.Text.Trim();

        StoreKey(settings.VisionProvider, _visionKey.Password);
        StoreKey(settings.ChatProvider, _chatKey.Password);

        settings.Save();
        DialogResult = true;
    }

    private void StoreKey(LlmProviderKind provider, string password)
    {
        // Untouched placeholder means "leave the stored key alone".
        if (string.IsNullOrEmpty(password) || password.All(c => c == '•'))
        {
            return;
        }

        _host.Secrets.Set(LlmSettings.SecretKey(provider), password);
    }

    private static double ParseDouble(string text, double fallback) =>
        double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var value) ? value : fallback;
}
