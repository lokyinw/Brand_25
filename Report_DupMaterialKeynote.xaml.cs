using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace Brand_25
{
    public partial class Report_DupMaterialKeynote : Window
    {
        // Path to the CSV export of this report, written once in the constructor so
        // it exists regardless of how the user closes the dialog (Close button or
        // the title-bar X). Null if the write failed — OpenFolderButton stays
        // hidden in that case.
        private readonly string _csvPath;

        public Report_DupMaterialKeynote(List<List<Material>> duplicateGroups, string credit = "Report_DupMaterialKeynote Default")
        {
            InitializeComponent();

            TitleText.Text = $"Duplicate Material Keynotes — {duplicateGroups.Sum(g => g.Count)} materials across {duplicateGroups.Count} keynotes";
            FooterText.Text = credit;

            minimizeImage.Source = LoadEmbeddedImage("minimize_32.png");
            maximizeImage.Source = LoadEmbeddedImage("maximize_32.png");
            closeImage.Source = LoadEmbeddedImage("close_32.png");
            brandLogo.Source = LoadEmbeddedImage("Brand_logo.png");
            icon.Source = LoadEmbeddedImage("B_icon_32.png");

            // Flatten groups into rows, alternating GroupShade per group
            // so duplicate sets are visually distinct in the table
            var rows = new List<VM_DupMaterialRow>();
            bool shade = false;
            foreach (List<Material> group in duplicateGroups.OrderBy(g => g.First().Name))
            {
                foreach (Material mat in group.OrderBy(m => m.Name))
                    rows.Add(new VM_DupMaterialRow(mat, shade));
                shade = !shade;
            }

            MaterialsDataGrid.ItemsSource = rows;

            // Export the same rows the DataGrid shows, in the same column order, so
            // the report survives closing the dialog. Written eagerly here — never
            // triggered from Close_Click/buttonClose_Click, since there's no
            // guarantee the user goes through either of those (title-bar X etc.).
            _csvPath = WriteCsv(rows);
            if (_csvPath != null)
            {
                OpenFolderButton.Visibility = System.Windows.Visibility.Visible;
            }
        }

        private static string WriteCsv(List<VM_DupMaterialRow> rows)
        {
            try
            {
                string tempFolder = Path.GetTempPath();
                string fileName = $"DuplicateMaterialKeynotes_{DateTime.Now:yyyy-MM-dd_HHmmss}.csv";
                string csvPath = Path.Combine(tempFolder, fileName);

                StringBuilder sb = new StringBuilder();
                sb.AppendLine(string.Join(",", "Keynote", "Material Name", "Description", "Comments", "Colour"));

                foreach (VM_DupMaterialRow row in rows)
                {
                    sb.AppendLine(string.Join(",",
                        CsvEscape(row.Keynote),
                        CsvEscape(row.Name),
                        CsvEscape(row.Description),
                        CsvEscape(row.Comments),
                        CsvEscape(row.Colour)));
                }

                File.WriteAllText(csvPath, sb.ToString());
                return csvPath;
            }
            catch
            {
                // If the CSV can't be written, the dialog just doesn't show the
                // Log Folder button — the on-screen report is unaffected.
                return null;
            }
        }

        // Quotes any field containing a comma, quote, or newline; doubles up
        // embedded quotes per standard CSV escaping.
        private static string CsvEscape(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";

            if (value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0)
            {
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            }
            return value;
        }

        private void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_csvPath)) return;

            try
            {
                if (File.Exists(_csvPath))
                    System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{_csvPath}\"");
                else
                    new Warning("Not Found", $"Could not find:\n{_csvPath}", "Report_DupMaterialKeynote").ShowDialog();
            }
            catch (Exception ex)
            {
                new Warning("Could Not Open Folder", ex.Message, "Report_DupMaterialKeynote").ShowDialog();
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();
        private void buttonMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        private void buttonClose_Click(object sender, RoutedEventArgs e) => Close();

        private void buttonMaximize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
            maximizeImage.Source = LoadEmbeddedImage(
                WindowState == WindowState.Maximized ? "restore_32.png" : "maximize_32.png");
        }

        private BitmapImage LoadEmbeddedImage(string imageName)
        {
            string resourcePath = $"Brand_25.Resources.Images.{imageName}";
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourcePath))
            {
                if (stream == null) throw new Exception($"Embedded resource not found: {resourcePath}");
                BitmapImage image = new BitmapImage();
                image.BeginInit();
                image.StreamSource = stream;
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.EndInit();
                return image;
            }
        }
    }

    // Thin wrapper adding GroupShade to VM_DupMaterial for row colouring
    public class VM_DupMaterialRow : VM_DupMaterial
    {
        public bool GroupShade { get; }

        public VM_DupMaterialRow(Material material, bool groupShade) : base(material)
        {
            GroupShade = groupShade;
        }
    }
}
