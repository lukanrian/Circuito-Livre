using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Microsoft.Win32;
using RafaelElectrical.Services;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using Grid = System.Windows.Controls.Grid;
using Image = System.Windows.Controls.Image;
using RadioButton = System.Windows.Controls.RadioButton;
using TextBox = System.Windows.Controls.TextBox;

namespace RafaelElectrical.Commands
{
    /// <summary>
    /// Reportar erro / sugerir função: a pessoa preenche uma janela no próprio Revit (com print opcional, arrastado,
    /// colado ou escolhido no computador) e o plugin envia para um Google Apps Script do mantenedor, que manda um
    /// e-mail para ele com o anexo (o e-mail dele não aparece no código). Sem esse serviço, usa o formulário do Google
    /// (só texto); sem internet direta, abre o formulário preenchido no navegador.
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    public class ReportBugCommand : IExternalCommand
    {
        public const string RepoUrl = "https://github.com/lukanrian/Plugin-el-trico-rvt";

        /// <summary>App da Web do Apps Script (tools/apps-script/Code.gs). Vazio = só formulário.</summary>
        private const string WebAppUrl = "https://script.google.com/macros/s/AKfycbxOqn5LCZPTKhSbHzALZZoE11Xlb-fH5IbADCPf_vuJ9StOaL7N04Tafotecv-eWpXK/exec";

        // Formulário público "RafaelElectrical — Reportar erro ou sugestão" (sem anexo)
        private const string FormBase = "https://docs.google.com/forms/d/e/1FAIpQLSdym3NpvCxjqw1r15iR3Kqn8wfr3SFHZdpN4WkrcirSUbs30g";
        private const string EntryType = "entry.1322718412";
        private const string EntryCommand = "entry.643553189";
        private const string EntryDescription = "entry.740961880";
        private const string EntryRevit = "entry.1832873014";
        private const string EntryPlugin = "entry.2142028488";
        private const string EntryContact = "entry.82622890";

        private const int MaxImageBytes = 5 * 1024 * 1024;

        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var app = data.Application.Application;
            string revit = $"{app.VersionName} ({app.VersionBuild}) — {app.Language}";
            string plugin = RevitHelpers.PluginVersion;
            string windows = Environment.OSVersion.VersionString;

            var window = new ReportWindow(revit, plugin);
            RevitHelpers.ShowOwned(window, data);
            if (window.ShowDialog() != true) return Result.Cancelled;

            if (window.OpenGitHub)
            {
                Open($"{RepoUrl}/issues");
                return Result.Succeeded;
            }

            string type = window.IsBug ? "Erro" : "Sugestão de função";

            // 1) serviço com anexo
            if (!string.IsNullOrEmpty(WebAppUrl) && TrySubmitWebApp(new Dictionary<string, string>
                {
                    ["tipo"] = type,
                    ["comando"] = window.Command,
                    ["descricao"] = window.Description,
                    ["revit"] = revit,
                    ["plugin"] = plugin,
                    ["windows"] = windows,
                    ["contato"] = window.Contact,
                    ["imagem"] = window.ImageBytes != null ? Convert.ToBase64String(window.ImageBytes) : null,
                    ["imagemTipo"] = window.ImageMime,
                    ["imagemNome"] = window.ImageName,
                }))
            {
                TaskDialog.Show("Reportar erro", "Enviado" + (window.ImageBytes != null ? " com a imagem" : "") +
                                                 ". Obrigado por ajudar a melhorar o RafaelElectrical!");
                return Result.Succeeded;
            }

            // 2) formulário (só texto)
            var fields = new Dictionary<string, string>
            {
                [EntryType] = type,
                [EntryCommand] = window.Command,
                [EntryDescription] = window.Description + $"\n\n[Windows: {windows}]" +
                                     (window.ImageBytes != null ? "\n[A pessoa anexou uma imagem, mas ela não pôde ser enviada.]" : ""),
                [EntryRevit] = revit,
                [EntryPlugin] = plugin,
                [EntryContact] = window.Contact,
            };
            if (TrySubmitForm(fields))
            {
                TaskDialog.Show("Reportar erro", "Enviado (sem a imagem). Obrigado por ajudar a melhorar o RafaelElectrical!");
                return Result.Succeeded;
            }

            // 3) sem internet direta: abre o formulário preenchido
            string url = FormBase + "/viewform?usp=pp_url&" + string.Join("&",
                fields.Select(kv => $"{kv.Key}={Uri.EscapeDataString(Trim(kv.Value, 1500))}"));
            Open(url);
            TaskDialog.Show("Reportar erro", "Não foi possível enviar direto. O formulário foi aberto no navegador já preenchido: confira e clique em Enviar.");
            return Result.Succeeded;
        }

        private static bool TrySubmitWebApp(Dictionary<string, string> payload)
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
                using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                HttpResponseMessage response = http.PostAsync(WebAppUrl, content).GetAwaiter().GetResult();
                string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                using JsonDocument json = JsonDocument.Parse(body);
                return json.RootElement.TryGetProperty("ok", out JsonElement ok) && ok.ValueKind == JsonValueKind.True;
            }
            catch (Exception) { return false; }
        }

        private static bool TrySubmitForm(Dictionary<string, string> fields)
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                using var content = new FormUrlEncodedContent(fields.Where(kv => !string.IsNullOrWhiteSpace(kv.Value)));
                HttpResponseMessage response = http.PostAsync(FormBase + "/formResponse", content).GetAwaiter().GetResult();
                return response.IsSuccessStatusCode;
            }
            catch (Exception) { return false; }
        }

        private static string Trim(string s, int max) => string.IsNullOrEmpty(s) || s.Length <= max ? s ?? "" : s.Substring(0, max) + "…";

        private static void Open(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception) { TaskDialog.Show("Reportar erro", "Não foi possível abrir o navegador:\n" + url); }
        }

        /// <summary>Janela do relatório (montada em código), com área para imagem.</summary>
        private class ReportWindow : Window
        {
            private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif" };

            private readonly RadioButton _bug;
            private readonly TextBox _command, _description, _contact;
            private readonly Image _preview;
            private readonly TextBlock _dropHint, _imageInfo;
            private readonly Button _removeImage;

            public bool IsBug => _bug.IsChecked == true;
            public string Command => _command.Text.Trim();
            public string Description => _description.Text.Trim();
            public string Contact => _contact.Text.Trim();
            public bool OpenGitHub { get; private set; }

            public byte[] ImageBytes { get; private set; }
            public string ImageMime { get; private set; }
            public string ImageName { get; private set; }

            public ReportWindow(string revit, string plugin)
            {
                Title = "Reportar erro / sugerir função — RafaelElectrical";
                Width = 620;
                Height = 780;
                Rect area = SystemParameters.WorkArea;
                Height = Math.Min(Height, area.Height * 0.92);
                WindowStartupLocation = WindowStartupLocation.CenterOwner;

                var p = new StackPanel { Margin = new Thickness(14) };
                p.Children.Add(new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 10),
                    Text = "Sua mensagem vai direto para o desenvolvedor. As versões do Revit e do plugin são enviadas junto. " +
                           "Não inclua dados de clientes.",
                });

                _bug = new RadioButton { Content = "Reportar um erro", IsChecked = true, GroupName = "t" };
                var feature = new RadioButton { Content = "Sugerir uma função", GroupName = "t", Margin = new Thickness(16, 0, 0, 0) };
                var types = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
                types.Children.Add(_bug);
                types.Children.Add(feature);
                p.Children.Add(types);

                p.Children.Add(new TextBlock { Text = "Comando ou função (ex.: Dimensionar Instalação):" });
                _command = new TextBox { Margin = new Thickness(0, 2, 0, 8) };
                p.Children.Add(_command);

                p.Children.Add(new TextBlock { Text = "Descrição — o que você fez, o que aconteceu e o que esperava:" });
                _description = new TextBox
                {
                    Height = 140, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 2, 0, 8),
                };
                p.Children.Add(_description);

                // ---- imagem ----
                p.Children.Add(new TextBlock { Text = "Imagem (opcional) — arraste aqui, cole com Ctrl+V ou procure no computador:" });
                _preview = new Image { Stretch = Stretch.Uniform, MaxHeight = 170, Visibility = System.Windows.Visibility.Collapsed };
                _dropHint = new TextBlock
                {
                    Text = "Arraste um print para cá\nou use os botões abaixo",
                    TextAlignment = TextAlignment.Center, Foreground = Brushes.Gray,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                };
                var dropContent = new Grid();
                dropContent.Children.Add(_dropHint);
                dropContent.Children.Add(_preview);
                var drop = new Border
                {
                    Height = 180, Margin = new Thickness(0, 2, 0, 4), AllowDrop = true,
                    BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(4),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(150, 150, 150)),
                    Background = new SolidColorBrush(Color.FromRgb(248, 248, 248)),
                    Child = dropContent,
                };
                drop.DragOver += (_, e) =>
                {
                    e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.Bitmap)
                        ? DragDropEffects.Copy : DragDropEffects.None;
                    e.Handled = true;
                };
                drop.Drop += (_, e) => OnDrop(e.Data);
                p.Children.Add(drop);

                var browse = new Button { Content = "Procurar imagem...", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 0) };
                browse.Click += (_, _) => Browse();
                var paste = new Button { Content = "Colar (Ctrl+V)", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 0) };
                paste.Click += (_, _) => PasteFromClipboard();
                _removeImage = new Button { Content = "Remover", Padding = new Thickness(10, 3, 10, 3), IsEnabled = false };
                _removeImage.Click += (_, _) => SetImage(null, null, null);
                _imageInfo = new TextBlock { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.Gray };
                var imageButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
                imageButtons.Children.Add(browse);
                imageButtons.Children.Add(paste);
                imageButtons.Children.Add(_removeImage);
                imageButtons.Children.Add(_imageInfo);
                p.Children.Add(imageButtons);

                // Ctrl+V em qualquer lugar fora das caixas de texto cola a imagem
                PreviewKeyDown += (_, e) =>
                {
                    if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && !(Keyboard.FocusedElement is TextBox) &&
                        Clipboard.ContainsImage())
                    {
                        PasteFromClipboard();
                        e.Handled = true;
                    }
                };

                p.Children.Add(new TextBlock { Text = "Seu contato para retorno (opcional: e-mail ou WhatsApp):" });
                _contact = new TextBox { Margin = new Thickness(0, 2, 0, 8) };
                p.Children.Add(_contact);

                p.Children.Add(new TextBlock { Foreground = Brushes.Gray, Text = $"Revit: {revit}   •   Plugin: {plugin}" });

                var github = new Button { Content = "Ver/abrir no GitHub", Padding = new Thickness(10, 3, 10, 3) };
                github.Click += (_, _) => { OpenGitHub = true; DialogResult = true; };
                var cancel = new Button { Content = "Cancelar", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 0), IsCancel = true };
                var send = new Button { Content = "Enviar", Padding = new Thickness(18, 3, 18, 3), IsDefault = true };
                send.Click += (_, _) =>
                {
                    if (Description.Length < 10)
                    {
                        MessageBox.Show(this, "Descreva o erro ou a sugestão (pelo menos algumas palavras).", Title);
                        return;
                    }
                    Cursor = Cursors.Wait;
                    DialogResult = true;
                };

                var buttons = new DockPanel { Margin = new Thickness(0, 12, 0, 0), LastChildFill = false };
                DockPanel.SetDock(github, Dock.Left);
                DockPanel.SetDock(send, Dock.Right);
                DockPanel.SetDock(cancel, Dock.Right);
                buttons.Children.Add(github);
                buttons.Children.Add(send);
                buttons.Children.Add(cancel);
                p.Children.Add(buttons);

                Content = new ScrollViewer { Content = p, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            }

            private void OnDrop(IDataObject data)
            {
                if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files)
                {
                    string file = files.FirstOrDefault(f => ImageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()));
                    if (file == null)
                    {
                        MessageBox.Show(this, "Arraste uma imagem (PNG, JPG, BMP ou GIF).", Title);
                        return;
                    }
                    LoadFile(file);
                }
                else if (data.GetDataPresent(DataFormats.Bitmap) && data.GetData(DataFormats.Bitmap) is BitmapSource bmp)
                {
                    SetFromBitmap(bmp, "print.png");
                }
            }

            private void Browse()
            {
                var dialog = new OpenFileDialog { Title = "Escolher imagem", Filter = "Imagens (*.png;*.jpg;*.jpeg;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.gif" };
                if (dialog.ShowDialog(this) == true) LoadFile(dialog.FileName);
            }

            private void PasteFromClipboard()
            {
                try
                {
                    if (Clipboard.ContainsImage()) { SetFromBitmap(Clipboard.GetImage(), "print.png"); return; }
                    if (Clipboard.ContainsFileDropList())
                    {
                        string file = Clipboard.GetFileDropList().Cast<string>()
                            .FirstOrDefault(f => ImageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()));
                        if (file != null) { LoadFile(file); return; }
                    }
                    MessageBox.Show(this, "A área de transferência não tem imagem. Tire um print (Win+Shift+S) e tente de novo.", Title);
                }
                catch (Exception ex) { MessageBox.Show(this, "Não foi possível colar a imagem:\n" + ex.Message, Title); }
            }

            private void LoadFile(string file)
            {
                try
                {
                    byte[] bytes = File.ReadAllBytes(file);
                    string ext = Path.GetExtension(file).ToLowerInvariant();
                    string mime = ext == ".png" ? "image/png" : ext == ".gif" ? "image/gif" : ext == ".bmp" ? "image/bmp" : "image/jpeg";
                    if (bytes.Length > MaxImageBytes || ext == ".bmp")
                    {
                        // muito grande (ou BMP): reduz e converte para JPEG
                        SetFromBitmap(Decode(bytes), Path.GetFileNameWithoutExtension(file) + ".jpg", forceJpeg: true);
                        return;
                    }
                    SetImage(bytes, mime, Path.GetFileName(file));
                }
                catch (Exception ex) { MessageBox.Show(this, "Não foi possível abrir a imagem:\n" + ex.Message, Title); }
            }

            private void SetFromBitmap(BitmapSource bmp, string name, bool forceJpeg = false)
            {
                if (bmp == null) return;
                byte[] png = forceJpeg ? null : Encode(bmp, new PngBitmapEncoder());
                if (png != null && png.Length <= MaxImageBytes) { SetImage(png, "image/png", name); return; }

                // reduz até caber (lado maior até 2000 px, JPEG 85%)
                BitmapSource small = bmp;
                double scale = Math.Min(1.0, 2000.0 / Math.Max(bmp.PixelWidth, bmp.PixelHeight));
                if (scale < 1) small = new TransformedBitmap(bmp, new ScaleTransform(scale, scale));
                byte[] jpg = Encode(small, new JpegBitmapEncoder { QualityLevel = 85 });
                if (jpg.Length > MaxImageBytes)
                {
                    MessageBox.Show(this, "A imagem é grande demais mesmo depois de reduzida (máximo 5 MB).", Title);
                    return;
                }
                SetImage(jpg, "image/jpeg", Path.ChangeExtension(name, ".jpg"));
            }

            private static BitmapSource Decode(byte[] bytes)
            {
                using var ms = new MemoryStream(bytes);
                var img = new BitmapImage();
                img.BeginInit();
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.StreamSource = ms;
                img.EndInit();
                img.Freeze();
                return img;
            }

            private static byte[] Encode(BitmapSource bmp, BitmapEncoder encoder)
            {
                encoder.Frames.Add(BitmapFrame.Create(bmp));
                using var ms = new MemoryStream();
                encoder.Save(ms);
                return ms.ToArray();
            }

            private void SetImage(byte[] bytes, string mime, string name)
            {
                ImageBytes = bytes;
                ImageMime = mime;
                ImageName = name;
                if (bytes == null)
                {
                    _preview.Source = null;
                    _preview.Visibility = System.Windows.Visibility.Collapsed;
                    _dropHint.Visibility = System.Windows.Visibility.Visible;
                    _imageInfo.Text = "";
                    _removeImage.IsEnabled = false;
                    return;
                }
                try { _preview.Source = Decode(bytes); } catch (Exception) { _preview.Source = null; }
                _preview.Visibility = System.Windows.Visibility.Visible;
                _dropHint.Visibility = System.Windows.Visibility.Collapsed;
                _imageInfo.Text = $"{name} — {bytes.Length / 1024.0:N0} KB";
                _removeImage.IsEnabled = true;
            }
        }
    }
}
