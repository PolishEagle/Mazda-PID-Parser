//#define MAZDA_IDS_FOLDER
//#define PRINT_TIMES_FOR_ALL

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MazdaIDS_Decoder
{
    public class MazdaIDS_DDL_Parser
    {
        // Magic for data: 0x04 0x25 0x2E 0x30 0x66 0x01 
        private byte[] DATA_MAGIC = new byte[] { 0x04, 0x25, 0x2E, 0x30, 0x66, 0x01, 0x00 };
        private byte[] DATA_END_MAGIC = new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xC0, 0xD4, 0x01, 0x00 };
        internal string[] PIDS = new string[] { "AFR", "APP", "BAT", "FUEL_PRES", "IAT", "KNOCKR",
                                                "LOAD", "MAF_V", "MAFg/s", "MAP", "RPM", "SHRTFT",
                                                "SPARKADV", "TP1_MZ", "TP_REL", "VSS", "VT_ACT",
                                                "LONGFT", "WGC", "FUELPW", "BARO" };
        internal string[] PID_TITLES = new string[] { "Actual AFR (AFR)", "APP (%)", "Boost Air Temp. (°C)",
                                                "HPFP (PSI)", "Intake Air Temp. (°C)", "KNOCKR (°)",
                                                "Calculated Load (Load)", "MAF Voltage (V)", "Mass Airflow (g/s)", "Boost (PSI)",
                                                "RPM", "Short Term FT (%)", "Spark Adv. (°)", "Throttle Position (%)", "Relative Throttle Position (%)",
                                                "Speed (KPH)", "Intake Valve Adv. (°)", "Long Term FT (%)", "Wastegate Duty (%)", "Injector Duty (%)",
                                                "Barometric Pressure (PSI)" };

        // How many bytes after the start of the magic where the data starts
        private const int DATA_STARTS_AHEAD = 53;

        // How far back to look for the text
        private const int DESCRIPTION_PARSE_ROLLBACK = 843;

        private const int BUFFER_SIZE = 4539422;

        private const string MAZDA_IDS_LOG_PATH = @"C:\ProgramData\Ford Motor Company\IDS\Sessions";
        private const string USER_LOG_FILES = @"C:\Users\janw\Documents\SPEED3\VersaTune Maps\Mazda IDS Logs";
        private const string MAZDA_IDS_LOG_EXT = ".ddl";

        private List<FileInfo> _logs = new List<FileInfo>();
        private Dictionary<FileInfo, List<PidData>> _convertedData = new Dictionary<FileInfo, List<PidData>>();

        public Dictionary<FileInfo, List<PidData>> ParsedData
        {
            get { return _convertedData; }
        }

        public List<FileInfo> Logs
        {
            get { return _logs; }
        }

        private void UpdateTempUnits(bool celsiusChecked)
        {
            for (int i = 0; i < PID_TITLES.Length; i++)
            {
                if (!celsiusChecked && PID_TITLES[i].Contains("(°C)"))
                {
                    PID_TITLES[i] = PID_TITLES[i].Replace("(°C)", "(°F)");
                }
                else if (celsiusChecked && PID_TITLES[i].Contains("(°F)"))
                {
                    PID_TITLES[i] = PID_TITLES[i].Replace("(°F)", "(°C)");
                }
            }
        }


        
        public int ReadAvailableMazdaFiles()
        {
#if MAZDA_IDS_FOLDER
            // Open the default directory and check for the DDL files
            foreach (var logFile in Directory.EnumerateFiles(MAZDA_IDS_LOG_PATH, "*" + MAZDA_IDS_LOG_EXT))
            {
                _logs.Add(new FileInfo(logFile));
            }
#else

            foreach (var logFile in Directory.EnumerateFiles(USER_LOG_FILES, "*" + MAZDA_IDS_LOG_EXT))
            {
                _logs.Add(new FileInfo(logFile));
            }
#endif

            return _logs.Count;
        }

        public bool ConvertFileToCSV(ICollection<int> indices, bool isCelsius)
        {
            if (indices.Count < 1)
            {
                return false;
            }

            // update the title units accordingly
            UpdateTempUnits(isCelsius);

            foreach (int index in indices)
            {
                List<PidData> data = new List<PidData>();

                ReadDDLFile(data, _logs[index]);

                if (data.Count != 0 && !_convertedData.ContainsKey(_logs[index]))
                {
                    _convertedData.Add(_logs[index], data);
                }

                long minTime = long.MaxValue;
                long maxTime = long.MinValue;
                GetMaxAndMinTime(data, ref minTime, ref maxTime);

                FormatAndCreateCSV(_logs[index], minTime, maxTime, isCelsius);
            }

            return true;
        }

        private void ReadDDLFile(List<PidData> data, FileInfo info)
        {
            // Bail out if the log doesn't exist
            if (!info.Exists)
                return;

            // Read the file
            using (FileStream stream = File.Open(info.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                long foundSpot = -1;
                long endSpot = -1;
                long offset = 0;
                long bytesRead = 0;
                long time = 0;
                uint value = 0;
                int minBlockSize = 0;

                long readSize = stream.Length; // BUFFER_SIZE;
                byte[] buffer = new byte[readSize];
                while ((bytesRead = stream.Read(buffer, 0, (int)readSize)) != 0)
                {
                    // go through the buffer
                    do
                    {
                        foundSpot = IndexOf(buffer, DATA_MAGIC, offset);
                        endSpot = IndexOf(buffer, DATA_END_MAGIC, foundSpot == -1 ? offset + 1 : foundSpot);

                        if (foundSpot != -1 && endSpot != -1 && endSpot > (foundSpot + DATA_STARTS_AHEAD))
                        {
                            if (minBlockSize == 0)
                            {
                                minBlockSize = (int)endSpot - (int)foundSpot;
                            }

                            // Get the PID name
                            // string pidName = RetrievePID(stream, foundSpot, buffer.Length);
                            string pidName = RetrievePIDFromArray(buffer, foundSpot);

                            if (!string.IsNullOrEmpty(pidName))
                            {
                                foundSpot += DATA_STARTS_AHEAD;

                                PidData pid = new PidData(pidName);

                                // Get the time from the log
                                while (foundSpot < endSpot)
                                {
                                    time = ReadTimestamp(buffer, ref foundSpot);

                                    // Get the value from the log
                                    value = ReadValue(buffer, ref foundSpot);

                                    pid.AddData(time, value);
                                }

                                // Save the dataset
                                data.Add(pid);
                            }
                        }

                        if (foundSpot != -1 && endSpot != -1)
                        {
                            // Advance in the array
                            offset = endSpot;
                        }
                        else
                        {
                            if ((offset + minBlockSize) <= bytesRead)
                            {
                                offset += minBlockSize;
                            }
                            else
                            {
                                break;
                            }
                        }
                    } while (offset < bytesRead);

                    offset = 0;

                    if (stream.Position == stream.Length)
                    {
                        break;
                    }
                    else
                    {
                        stream.Position -= (int)(minBlockSize * 0.85);
                        readSize = (int)Math.Max(0, Math.Min(BUFFER_SIZE, stream.Length - stream.Position));
                        buffer = new byte[readSize];
                    }
                }
            }
        }

        // TODO: the values don't appear to align later in the file, need to determine how they do it.

        private static long IndexOf(byte[] arrayToSearchThrough, byte[] patternToFind, long offset = 0)
        {
            if (patternToFind.Length > arrayToSearchThrough.Length || offset < 0)
                return -1;

            for (long i = offset; i < arrayToSearchThrough.Length - patternToFind.Length; i++)
            {
                bool found = true;
                for (int j = 0; j < patternToFind.Length; j++)
                {
                    if (arrayToSearchThrough[i + j] != patternToFind[j])
                    {
                        found = false;
                        break;
                    }
                }
                if (found)
                {
                    return i;
                }
            }
            return -1;
        }

        private long ReadTimestamp(byte[] buffer, ref long foundSpot)
        {
            // Not enough data to read from the buffer
            if ((foundSpot + 4) > buffer.Length)
                return 0;

            long time = (uint)buffer[foundSpot]
                | (uint)buffer[foundSpot + 1] << 8
                | (uint)buffer[foundSpot + 2] << 16
                | (uint)buffer[foundSpot + 3] << 24;

            foundSpot += 4;

            return time;
        }

        private uint ReadValue(byte[] buffer, ref long foundSpot)
        {
            // Not enough data to read from the buffer
            if ((foundSpot + 2) >= buffer.Length)
                return 0;

            uint value = (uint)buffer[foundSpot]
                | (uint)buffer[foundSpot + 1] << 8;

            foundSpot += 8;

            return value;
        }

        private string RetrievePID(FileStream stream, long startSpot, long arrayLen)
        {
            if (stream == null)
            {
                return string.Empty;
            }

            string pidName = string.Empty;

            // Get the original position so we can return to it afterwards
            long currentPosition = stream.Position;

            try
            {
                byte[] buffer = new byte[DESCRIPTION_PARSE_ROLLBACK];

                // Lets roll back the position to attempt to parse the PID text
                stream.Position -= (arrayLen - startSpot + DESCRIPTION_PARSE_ROLLBACK);

                stream.Read(buffer, 0, DESCRIPTION_PARSE_ROLLBACK);

                foreach (string pid in PIDS)
                {
                    if (IndexOf(buffer, Encoding.Default.GetBytes(pid)) != -1)
                    {
                        pidName = pid;
                        break;
                    }
                }
            }
            catch (Exception e)
            {}

            // Return the stream back to where it was
            stream.Position = currentPosition;

            return pidName;
        }

        private string RetrievePIDFromArray(byte[] buffer, long offset)
        {
            string pidName = string.Empty;

            try
            {
                byte[] buf = new byte[DESCRIPTION_PARSE_ROLLBACK];

                for (int i = 0; i < DESCRIPTION_PARSE_ROLLBACK; i++)
                {
                    buf[i] = buffer[offset + i - DESCRIPTION_PARSE_ROLLBACK];
                }

                foreach (string pid in PIDS)
                {
                    if (IndexOf(buf, Encoding.Default.GetBytes(pid)) != -1)
                    {
                        pidName = pid;
                        break;
                    }
                }
            }
            catch (Exception e)
            { }

            return pidName;
        }

        private void GetMaxAndMinTime(List<PidData> data, ref long minTime, ref long maxTime)
        {
            foreach (PidData pid in data)
            {
                foreach (var entry in pid.DataEntries)
                {
                    if (entry.Time < minTime)
                        minTime = entry.Time;

                    if (entry.Time > maxTime)
                        maxTime = entry.Time;
                }
            }
        }

        private void FormatAndCreateCSV(FileInfo info, long minTime, long maxTime, bool isCelsius)
        {
            // Stores the index of the current position in the list for each PID entry
            Dictionary<string, int> currentPidIndex = new Dictionary<string, int>();
            long currentTime = minTime;

            List<string> pidsPresent = new List<string>();
            List<string> pidTitles = new List<string>();
            var pidList = _convertedData[info];

            for(int i = 0; i < pidList.Count; i++)
            {
                pidsPresent.Add(pidList[i].PidName);

                // Add the PID's and their  respective indexes to the dictionary
                currentPidIndex.Add(pidList[i].PidName, 0);
            }

            foreach (string pid in pidsPresent)
            {
                var index = Array.IndexOf(PIDS, pid);
                pidTitles.Add(PID_TITLES[index]);
            }

            // string tempFile = Guid.NewGuid().ToString();
            string tempFile = info.Name + "-" + Guid.NewGuid().ToString().Substring(0, 8);
            using (StreamWriter wr = new StreamWriter(string.Format(@"{1}\{0}.csv", tempFile, info.DirectoryName), false, Encoding.GetEncoding("Windows-1252")))
            {
                wr.Write("Time (sec),");

                // Write the headers
                wr.Write(string.Format("{0}{1}", string.Join(",", pidTitles), Environment.NewLine));

                PrintRowToCSV(currentTime, currentPidIndex, pidList, wr, isCelsius, minTime);

                // Loop until we've displayed all the values.
                do
                {
                    GetNextPids(currentPidIndex, pidList, ref currentTime);
                    PrintRowToCSV(currentTime, currentPidIndex, pidList, wr, isCelsius, minTime);
                } while (currentTime < maxTime);
            }
        }

        private void PrintRowToCSV(long currentTime, Dictionary<string, int> currentPidIndex, List<PidData> pidList, StreamWriter wr, bool isCelsius, long minTime)
        {
            // Write the time
            wr.Write(string.Format("{0:0.000},", (double)currentTime/1000));

            var pid = pidList.Where<PidData>(p => p.PidName.Equals("RPM")).First<PidData>();
            var rpmPidDataIndex = pidList.IndexOf(pid);

            for (int i = 0; i < pidList.Count; i++)
            {
                var readIndex = currentPidIndex[pidList[i].PidName];
                var value = pidList[i].GetConvertedValue(pidList[i].DataEntries[readIndex].Value, isCelsius);

                var rpmIndex = currentPidIndex["RPM"];
                var rpm = pidList[rpmPidDataIndex].GetConvertedValue(pidList[rpmPidDataIndex].DataEntries[rpmIndex].Value, isCelsius);

                // Write the values to file
                if (pidList[i].PidName.Equals("LOAD"))
                {
                    wr.Write(string.Format("{0:0.000}", value));
                }
                else if (pidList[i].PidName.Equals("FUELPW"))
                {
                    wr.Write(string.Format("{0:0.00}", (double)((double)value * (double)rpm) / 1200));
                }
                else
                {
                    wr.Write(string.Format("{0:0.00}", value));
                }

#if PRINT_TIMES_FOR_ALL
                wr.Write(" ({0})", pidList[i].DataEntries[readIndex].Time - minTime);
#endif

                if (i < (pidList.Count - 1))
                {
                    wr.Write(",");
                }
            }
            wr.Write(Environment.NewLine);
        }

        private void GetNextPids(Dictionary<string, int> currentPidEntry, List<PidData> pidList, ref long currentTime)
        {
            long newTime = long.MinValue;

            // Now that we have the newer time lets adjust the current PID indexes accordingly
            for (int i = 0; i < pidList.Count; i++)
            {
                var pid = pidList[i];

                if ((currentPidEntry[pid.PidName] + 1) < pid.DataEntries.Count)
                {
                    currentPidEntry[pid.PidName]++;
                }

                var entry = pid.DataEntries[currentPidEntry[pid.PidName]];

                if (entry.Time > newTime)
                {
                    newTime = entry.Time;
                }
            }

            currentTime = newTime;
        }


        /************************************************************************************************************************************/
        /************************************************************************************************************************************/
        /************************************************************************************************************************************/
        /************************************************************************************************************************************/

        public class PidEntry
        {
            private long time;
            private uint value;

            public long Time
            {
                get { return time; }
            }

            public uint Value
            {
                get { return value; }
            }

            public PidEntry(long time, uint value)
            {
                this.time = time;
                this.value = value;
            }
        }

        public class PidData
        {
            private string _pidName;
            private List<PidEntry> _dataEntries;

            public string PidName
            {
                get { return _pidName; }
            }

            public List<PidEntry> DataEntries
            {
                get { return _dataEntries; }
            }

            public PidData(string name)
            {
                if (string.IsNullOrEmpty(name))
                {
                    throw new Exception("PID name not included.");
                }

                _dataEntries = new List<PidEntry>();
                _pidName = name;
            }

            public void AddData(long time, uint value)
            {
                var newEntry = new PidEntry(time, value);

                _dataEntries.Add(newEntry);
            }

            public object GetConvertedValue(uint value, bool isCelsius)
            {
                // AFR:  (<value> * 49) / 430
                // APP:  (<value> * 100) / 255                   %
                // BAT:  <value> - 40                            Deg. Celsius
                // HPFP: <value>[2] * 1.45                       PSI
                // IAT:  <value> - 40                            Deg. Celsius
                // KR:   (<value>[2] * 0.001956) -0.004898       Deg.                        **** Looks to be correct, double check ****
                // Load: <value> / 255
                // MAF:  <value>[2] / 100                        g/sec
                // MAFV: (<value>[2] * 61 / 62500) - 0.00454)    V
                // MAP:  <value> * 0.145                         PSI
                // RPM:  <value>[2] / 4                          RPM
                // STFT: (<value> - 128) * (100 / 128)           %
                // IGN:  (<value> - 128) / 2                     Deg.
                // TPS:  (<value> * 100) / 255                   %
                // VSS:  <value>                                 KPH                         The value is the speed
                // VVT:  (<value>[2] - 0.08)/16                  Deg.
                // WGDC: (<value>[2] / 32768) * 100              %

                var useValue = (double)value;

                switch (_pidName)
                {
                    case "AFR":
                        return (double)(useValue * 49) / 430;
                    case "APP":
                        return (double)(useValue * 100) / 255;
                    case "FUEL_PRES":
                        return (double)useValue * 1.45;
                    case "IAT":
                    case "BAT":
                        var celsius = (double)useValue - 40;
                        return GetCorrectTemp(celsius, isCelsius);
                    case "KNOCKR":
                        return Math.Abs((double)(useValue * 0.001956) - 0.004898);
                    case "LOAD":
                        return (double)useValue / 255;
                    case "MAF_V":
                        return (double)((useValue * 61) / 62500) - 0.00454;
                    case "MAFg/s":
                        return (double)useValue / 100;
                    case "MAP":
                    case "BARO":
                        return (double)useValue * 0.145;
                    case "RPM":
                        return (double)useValue / 4;
                    case "SHRTFT":
                    case "LONGFT":
                        return (double)((useValue - 128) * 100) / 128;
                    case "SPARKADV":
                        return (double)(useValue - 128) / 2;
                    case "TP1_MZ":
                        return (double)(useValue * 100) / 255;
                    case "VSS":
                        return (double)useValue;
                    case "VT_ACT":
                        var retVal = (double)(useValue - 0.08) / 16;

                        if (retVal < 0)
                            return (double)0;
                        else
                            return retVal;
                    case "WGC":
                        return (double)(useValue * 100) / 32768;
                    case "TP_REL":
                        return (double)(useValue * 100) / 255;
                    case "FUELPW":
                        return (double)useValue / 125;
                    default:
                        return useValue;
                }
            }

            private double GetCorrectTemp(double temp, bool isCelsius)
            {
                if (isCelsius)
                    return temp;
                else
                    return ((temp * 9) / 5) + 32;
            }
        }
    }
}
