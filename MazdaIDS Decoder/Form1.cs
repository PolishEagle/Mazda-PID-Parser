using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Windows.Forms.DataVisualization.Charting;

namespace MazdaIDS_Decoder
{
    public partial class Form1 : Form
    {
        private MazdaIDS_DDL_Parser _mazdaLogParser;
        private const string MAZDA_REG_SUB_KEY = "MazdaIDSLogConverter";
        private const string KEY_SAVE_PATH = "SavePath";
        private const string KEY_OPEN_PATH = "OpenPath";

        public Form1()
        {
            InitializeComponent();

            RegistryKey mazdaKey = Registry.CurrentUser.OpenSubKey(MAZDA_REG_SUB_KEY);
            if (mazdaKey != null)
            {
                var path = mazdaKey.GetValue(KEY_OPEN_PATH) as string;
                
                if (!string.IsNullOrEmpty(path))
                {
                    SetupMazdaLogParser(path);
                }
            }
        }

        private void btnClear_Click(object sender, EventArgs e)
        {
            foreach (int i in lstLogFiles.CheckedIndices)
            {
                lstLogFiles.SetItemCheckState(i, CheckState.Unchecked);
            }

            lblStatus.Text = "All items unchecked!";
        }

        private void btnConvert_Click(object sender, EventArgs e)
        {
            if (lstLogFiles.CheckedIndices.Count == 0)
            {
                lblStatus.Text = "Error: No items selected to convert!";
                return;
            }

            string savePath = string.Empty;
            var mazdaKey = GetMazdaRegKey();

            if (string.IsNullOrEmpty(mazdaKey.GetValue(KEY_SAVE_PATH) as string))
            {
                // Get a save path
                var dlg = folderBrowserDlg.ShowDialog();

                if (dlg != DialogResult.OK)
                {
                    lblStatus.Text = "Error: No save folder selected.";
                    return;
                }
                savePath = folderBrowserDlg.SelectedPath;
            }
            else
            {
                savePath = mazdaKey.GetValue(KEY_SAVE_PATH) as string;
            }

            // Get selected index before we go to a separate thread
            List<int> indices = new List<int>();
            indices.AddRange(lstLogFiles.CheckedIndices.Cast<int>());

            // get the temperature units
            var celsiusChecked = radioCelsius.Checked;
            var kphChecked = radioKPH.Checked;

            Task convert = Task.Run(() =>
            {
                SetEssentialControlsEnable(false);
                SetStatusBarText("Conversion: running...");

                _mazdaLogParser.ConvertFileToCSV(savePath, indices, celsiusChecked, kphChecked);
                
                SetStatusBarText(lblStatus.Text = "Conversion: Completed!");
                SetEssentialControlsEnable(true);
            });
        }

        private void SetStatusBarText(string text)
        {
            if (InvokeRequired)
            {
                Invoke((Action<string>)SetStatusBarText, text);
                return;
            }
            lblStatus.Text = text;
        }

        private void SetEssentialControlsEnable(bool enabled)
        {
            if (InvokeRequired)
            {
                Invoke((Action<bool>)SetEssentialControlsEnable, enabled);
                return;
            }
            btnConvert.Enabled = enabled;
            btnClear.Enabled = enabled;
            lstLogFiles.Enabled = enabled;
        }

        private void PlotDataOnChart(string noneuse, bool isCelsius, bool kphChecked)
        {
            if (InvokeRequired)
            {
                Invoke((Action<string, bool, bool>)PlotDataOnChart, string.Empty);
                return;
            }

            // Plot the data
            if (_mazdaLogParser.ParsedData.Count != 0)
            {
                // TODO: Make this work with any log file, not just 2
                var dic = _mazdaLogParser.ParsedData;
                var data = dic[_mazdaLogParser.Logs[lstLogFiles.SelectedIndex]];

                chart1.Series.Clear();
                chkPids.Items.Clear();

                foreach (var pid in data)
                {
                    var pidName = pid.PidName.Equals("FUELPW") ? "Injector Pulse Width (ms)" : pid.PidFriendlyName;

                    if (chart1.Series.Where<Series>(s => s.Name.Equals(pidName)).Count() == 0)
                    {
                        chart1.Series.Add(pidName);

                        foreach (var entry in pid.DataEntries)
                        {
                            var convertedValue = (double)pid.GetConvertedValue(entry.Value, isCelsius, kphChecked);

                            chart1.Series[pidName].ToolTip = "#VALY, #VALX";
                            chart1.Series[pidName].Points.Add(new DataPoint(entry.Time, convertedValue));
                        }
                    }

                    chart1.Series[pidName].Enabled = false;

                    // The checkbox for enabling and disabling series
                    if (!chkPids.Items.Contains(pidName))
                    {
                        chkPids.Items.Add(pidName);
                    }
                }
            }
        }

        private void MazdaChart_MouseDown(object sender, System.Windows.Forms.MouseEventArgs e)
        {
            HitTestResult result = chart1.HitTest(e.X, e.Y);
            if (result != null && result.Series != null)
            {
                result.Series.Enabled = !result.Series.Enabled;
                chart1.ChartAreas["ChartArea1"].RecalculateAxesScale();
                chart1.Update();
            }
        }

        private void chkPids_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (sender is ListBox)
            {
                ListBox chkLst = sender as ListBox;
                string name = chkLst.SelectedItem.ToString();

                chart1.Series[name].Enabled = !chart1.Series[name].Enabled;
                chart1.ChartAreas["ChartArea1"].RecalculateAxesScale();
                chart1.Update();
            }
        }

        private void SetupMazdaLogParser(string logFolder)
        {
            _mazdaLogParser = new MazdaIDS_DDL_Parser(logFolder);

            SetStatusBarText("Reading log files from directory...");

            // Get the files and details
            int logs = _mazdaLogParser.ReadAvailableMazdaFiles();

            SetStatusBarText(string.Format("{0} log(s) found!", logs));

            // Display the files to the checkbox
            foreach (FileInfo fileDetails in _mazdaLogParser.Logs)
            {
                lstLogFiles.Items.Add(string.Format("{0}  - ({1})", fileDetails.Name, fileDetails.CreationTime.ToString()));
            }
        }

        private void btnGraphHighlightedLog_Click(object sender, EventArgs e)
        {
            SetEssentialControlsEnable(false);
            SetStatusBarText("Conversion: running...");

            var index = lstLogFiles.SelectedIndex;
            _mazdaLogParser.ConvertSelectedLog(index);

            SetStatusBarText(lblStatus.Text = "Conversion: Completed!");
            SetEssentialControlsEnable(true);

            PlotDataOnChart(null, radioCelsius.Checked, radioKPH.Checked);
            tabCtrl.SelectedIndex = 1;
        }


        /*************************************************************************************************
         * Menu Strip Controls
         */

        private void openToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (Directory.Exists(MazdaIDS_DDL_Parser.KnownPaths[0]))
            {
                folderBrowserDlg.SelectedPath = MazdaIDS_DDL_Parser.KnownPaths[0];
            }

            DialogResult rst = folderBrowserDlg.ShowDialog();

            if (rst == DialogResult.OK)
            {
                lstLogFiles.Items.Clear();
                var logFolder = folderBrowserDlg.SelectedPath;

                SetupMazdaLogParser(logFolder);

                var mazdaKey = GetMazdaRegKey();
                mazdaKey.SetValue(KEY_OPEN_PATH, logFolder, RegistryValueKind.String);
            }
        }

        private void exitToolStripMenuItem_Click(object sender, EventArgs e)
        {
            this.Close();
        }

        private void saveToToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (Directory.Exists(MazdaIDS_DDL_Parser.KnownPaths[1]))
            {
                folderBrowserDlg.SelectedPath = MazdaIDS_DDL_Parser.KnownPaths[1];
            }

            DialogResult rst = folderBrowserDlg.ShowDialog();

            if (rst == DialogResult.OK)
            {
                var logFolder = folderBrowserDlg.SelectedPath;

                SetupMazdaLogParser(logFolder);

                var mazdaKey = GetMazdaRegKey();
                mazdaKey.SetValue(KEY_SAVE_PATH, logFolder, RegistryValueKind.String);
            }
        }

        private RegistryKey GetMazdaRegKey()
        {
            RegistryKey mazdaKey = Registry.CurrentUser.OpenSubKey(MAZDA_REG_SUB_KEY, true);

            if (mazdaKey == null)
            {
                mazdaKey = Registry.CurrentUser.CreateSubKey(MAZDA_REG_SUB_KEY);
            }

            return mazdaKey;
        }
    }
}
