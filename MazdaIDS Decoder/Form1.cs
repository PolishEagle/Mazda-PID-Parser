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

        public Form1()
        {
            InitializeComponent();

            _mazdaLogParser = new MazdaIDS_DDL_Parser();

            Task t = Task.Run(() =>
            {
                SetStatusBarText("Reading log files from directory...");

                // Get the files and details
                int logs = _mazdaLogParser.ReadAvailableMazdaFiles();

                SetStatusBarText(string.Format("{0} log(s) found!", logs));
            });
            t.Wait();

            // Display the files to the checkbox
            foreach (FileInfo fileDetails in _mazdaLogParser.Logs)
            {
                lstLogFiles.Items.Add(string.Format("{0}  - ({1})", fileDetails.Name, fileDetails.CreationTime.ToString()));
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

            // Get selected index before we go to a separate thread
            List<int> indices = new List<int>();
            indices.AddRange(lstLogFiles.CheckedIndices.Cast<int>());

            // get the temperature units
            var celsiusChecked = radioCelsius.Checked;

            Task convert = Task.Run(() =>
            {
                SetEssentialControlsEnable(false);
                SetStatusBarText("Conversion: running...");

                _mazdaLogParser.ConvertFileToCSV(indices, celsiusChecked);
                
                SetStatusBarText(lblStatus.Text = "Conversion: Completed!");
                SetEssentialControlsEnable(true);

                // TODO: Plotting disabled
                //PlotDataOnChart(string.Empty);
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

        private void PlotDataOnChart(string noneuse, bool isCelsius)
        {
            if (InvokeRequired)
            {
                Invoke((Action<string, bool>)PlotDataOnChart, string.Empty);
                return;
            }

            // Plot the data
            if (_mazdaLogParser.ParsedData.Count != 0)
            {
                // TODO: Make this work with any log file, not just 2
                var dic = _mazdaLogParser.ParsedData;
                var data = dic[_mazdaLogParser.Logs[1]];

                foreach (var pid in data)
                {
                    if (chart1.Series.Where<Series>(s => s.Name.Equals(pid.PidName)).Count() == 0)
                    {
                        chart1.Series.Add(pid.PidName);

                        foreach (var entry in pid.DataEntries)
                        {
                            var convertedValue = (double)pid.GetConvertedValue(entry.Value, isCelsius);

                            chart1.Series[pid.PidName].ToolTip = "#VALY, #VALX";
                            chart1.Series[pid.PidName].Points.Add(new DataPoint(entry.Time, convertedValue));
                        }
                    }

                    chart1.Series[pid.PidName].Enabled = false;

                    // The checkbox for enabling and disabling series
                    if (!chkPids.Items.Contains(pid.PidName))
                    {
                        chkPids.Items.Add(pid.PidName);
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
                chart1.Update();
                chart1.ChartAreas["ChartArea1"].RecalculateAxesScale();
            }
        }
    }
}
