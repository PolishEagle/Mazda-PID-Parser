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
                                                "SPARKADV", "Throttle Position", "VSS", "VT_ACT",
                                                "WGC" };

        // How many bytes after the start of the magic where the data starts
        private const int DATA_STARTS_AHEAD = 53;

        // How far back to look for the text
        private const int DESCRIPTION_PARSE_ROLLBACK = 843;

        private const int BUFFER_SIZE = 112000;
        private const int PREPEND_SIZE = 8000;

        private const string MAZDA_IDS_LOG_PATH = @"C:\ProgramData\Ford Motor Company\IDS\Sessions";
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
        
        public int ReadAvailableMazdaFiles()
        {
            // Open the default directory and check for the DDL files
            foreach (var logFile in Directory.EnumerateFiles(MAZDA_IDS_LOG_PATH, "*" + MAZDA_IDS_LOG_EXT))
            {
                _logs.Add(new FileInfo(logFile));
            }

            return _logs.Count;
        }

        public bool ConvertFileToCSV(ICollection<int> indices)
        {
            if (indices.Count < 1)
            {
                return false;
            }

            List<PidData> data = new List<PidData>();

            foreach (int index in indices)
            {
                data.Clear();

                ReadDDLFile(data, _logs[index]);

                if (data.Count != 0 && !_convertedData.ContainsKey(_logs[index]))
                {
                    _convertedData.Add(_logs[index], data);
                }

                long minTime = long.MaxValue;
                long maxTime = long.MinValue;
                GetMaxAndMinTime(data, ref minTime, ref maxTime);

                FormatAndCreateCSV(_logs[index], minTime, maxTime);
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
                int foundSpot = -1;
                int endSpot = -1;
                long offset = 0;
                long time = 0;
                uint value = 0;

                int readSize = BUFFER_SIZE + PREPEND_SIZE;
                do
                {
                    byte[] buffer = new byte[readSize];
                    offset = stream.Read(buffer, 0, readSize);

                    foundSpot = IndexOf(buffer, DATA_MAGIC);
                    endSpot = IndexOf(buffer, DATA_END_MAGIC, foundSpot + 1);

                    if (foundSpot != -1 && endSpot != -1 && endSpot > foundSpot)
                    {
                        // Get the PID name
                        string pidName = RetrievePID(stream, foundSpot, buffer.Length);

                        if (string.IsNullOrEmpty(pidName))
                        {
                            throw new Exception("Couldn't find PID name.");
                        }

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

                    if (stream.Position != stream.Length)
                    {
                        stream.Seek(-(PREPEND_SIZE * 2), SeekOrigin.Current);
                    }

                    readSize = (int)Math.Max(0, Math.Min(BUFFER_SIZE + PREPEND_SIZE, stream.Length - stream.Position));
                } while (readSize != 0);
            }
        }

        private static int IndexOf(byte[] arrayToSearchThrough, byte[] patternToFind, int offset = 0)
        {
            if (patternToFind.Length > arrayToSearchThrough.Length || offset < 0)
                return -1;

            for (int i = offset; i < arrayToSearchThrough.Length - patternToFind.Length; i++)
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

        private long ReadTimestamp(byte[] buffer, ref int foundSpot)
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

        private uint ReadValue(byte[] buffer, ref int foundSpot)
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

        private void FormatAndCreateCSV(FileInfo info, long minTime, long maxTime)
        {
            // Stores the index of the current position in the list for each PID entry
            Dictionary<string, int> currentPidIndex = new Dictionary<string, int>();
            long currentTime = minTime;

            List<string> pidsPresent = new List<string>();
            var pidList = _convertedData[info];

            for(int i = 0; i < pidList.Count; i++)
            {
                pidsPresent.Add(pidList[i].PidName);

                // Add the PID's and their  respective indexes to the dictionary
                currentPidIndex.Add(pidList[i].PidName, 0);
            }

            string tempFile = Guid.NewGuid().ToString();
            using (StreamWriter wr = new StreamWriter(string.Format(@"{1}\{0}.csv", tempFile, info.DirectoryName)))
            {
                wr.Write("Time,");

                // Write the headers
                wr.Write(string.Format("{0}{1}", string.Join(",", pidsPresent), Environment.NewLine));

                PrintRowToCSV(currentTime, currentPidIndex, pidList, wr);

                // Loop until we've displayed all the values.
                do
                {
                    GetNextPids(currentPidIndex, pidList, ref currentTime);
                    PrintRowToCSV(currentTime, currentPidIndex, pidList, wr);
                } while (currentTime < maxTime);
            }
        }

        private void PrintRowToCSV(long currentTime, Dictionary<string, int> currentPidIndex, List<PidData> pidList, StreamWriter wr)
        {
            // Write the time
            wr.Write(string.Format("{0},", currentTime));

            // Write the first row values
            for (int i = 0; i < pidList.Count; i++)
            {
                var readIndex = currentPidIndex[pidList[i].PidName];
                var value = pidList[i].GetConvertedValue(pidList[i].DataEntries[readIndex].Value);

                // Write the values to file
                if (pidList[i].PidName.Equals("LOAD"))
                {
                    wr.Write(string.Format("{0:0.000}", value));
                }
                else
                {
                    wr.Write(string.Format("{0:0.00}", value));
                }

                if (i < (pidList.Count - 1))
                {
                    wr.Write(",");
                }
            }
            wr.Write(Environment.NewLine);
        }

        private void GetNextPids(Dictionary<string, int> currentPidEntry, List<PidData> pidList, ref long currentTime)
        {
            long newTime = long.MaxValue;

            for (int i = 0; i < pidList.Count; i++)
            {
                var pid = pidList[i];

                for (int j = currentPidEntry[pid.PidName]; j < pid.DataEntries.Count; j++)
                {
                    var entry = pid.DataEntries[j];

                    if (entry.Time > currentTime && entry.Time < newTime)
                    {
                        newTime = entry.Time;
                        break;
                    }
                    else if (entry.Time > newTime)
                    {
                        break;
                    }
                }
            }

            currentTime = newTime;

            // Now that we have the newer time lets adjust the current PID indexes accordingly
            for (int i = 0; i < pidList.Count; i++)
            {
                var pid = pidList[i];

                for (int j = currentPidEntry[pid.PidName] + 1; j < pid.DataEntries.Count; j++)
                {
                    var entry = pid.DataEntries[j];
                    var existingTime = pid.DataEntries[currentPidEntry[pid.PidName]];

                    if (entry.Time <= newTime)
                    {
                        currentPidEntry[pid.PidName] = j;
                        break;
                    }
                    else
                    {
                        break;
                    }
                }
            }
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

            public object GetConvertedValue(uint value)
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
                    case "BAT":
                        return (double)useValue - 40;
                    case "FUEL_PRES":
                        return (double)useValue * 1.45;
                    case "IAT":
                        return (double)useValue - 40;
                    case "KNOCKR":
                        return Math.Abs((double)(useValue * 0.001956) - 0.004898);
                    case "LOAD":
                        return (double)useValue / 255;
                    case "MAF_V":
                        return (double)((useValue * 61) / 62500) - 0.00454;
                    case "MAFg/s":
                        return (double)useValue / 100;
                    case "MAP":
                        return (double)useValue * 0.145;
                    case "RPM":
                        return (double)useValue / 4;
                    case "SHRTFT":
                        return (double)((useValue - 128) * 100) / 128;
                    case "SPARKADV":
                        return (double)(useValue - 128) / 2;
                    case "Throttle Position":
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
                }

                return null;
            }
        }
    }
}
